#nowarn "3511"

namespace Circuit.Core

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Circuit.Core.CircuitGraph

/// Describes immutable context for one agent leaf evaluation.
[<Sealed>]
type RunContext
    internal
    (
        runId: RunId,
        agent: AgentDefinition,
        signatureId: DefinitionId,
        signatureVersion: SemanticVersion,
        options: RunOptions,
        nodePath: string,
        idempotencyKey: string
    ) =
    new(runId, agent, signatureId, signatureVersion, options) =
        RunContext(runId, agent, signatureId, signatureVersion, options, agent.Id.Value, runId.Value)

    /// <summary>Gets the run id value.</summary>
    member _.RunId = runId
    /// <summary>Gets the agent value.</summary>
    member _.Agent = agent
    /// <summary>Gets the signature id value.</summary>
    member _.SignatureId = signatureId
    /// <summary>Gets the signature version value.</summary>
    member _.SignatureVersion = signatureVersion
    /// <summary>Gets the options value.</summary>
    member _.Options = options
    /// <summary>Gets the node path value.</summary>
    member _.NodePath = nodePath
    /// Gets the stable scheduler idempotency key for this leaf invocation.
    member _.IdempotencyKey = idempotencyKey

/// The single runtime interface for every Circuit definition and execution mode.
type ICircuitRuntime =
    /// Starts a new unified Circuit run.
    abstract StartAsync<'Input, 'Output> :
        circuit: Circuit.Core.Circuit<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        cancellationToken: CancellationToken ->
            Task<CircuitRun<'Output>>

    /// Resumes an exact Circuit checkpoint with rebound process-local services.
    abstract ResumeAsync<'Input, 'Output> :
        circuit: Circuit.Core.Circuit<'Input, 'Output> *
        checkpoint: CircuitCheckpoint<'Output> *
        options: ResumeOptions *
        cancellationToken: CancellationToken ->
            Task<CircuitRun<'Output>>

    /// Serializes adapter-owned provider session state.
    abstract SerializeSessionAsync:
        agent: AgentDefinition * session: CircuitSession * cancellationToken: CancellationToken ->
            ValueTask<JsonElement>

    /// Deserializes adapter-owned provider session state.
    abstract DeserializeSessionAsync:
        agent: AgentDefinition * state: JsonElement * cancellationToken: CancellationToken -> ValueTask<CircuitSession>

type internal CircuitObservation =
    | CircuitRunStarted of RunInfo
    | CircuitNodeStarted of RunId * NodeInfo
    | CircuitApprovalRequested of RunId * ApprovalRequest
    | CircuitNodeCompleted of RunId * NodeInfo * CircuitFailure voption
    | CircuitRunCompleted of RunId * CircuitFailure voption * RunUsage * DateTimeOffset * DateTimeOffset

type internal IAgentLeafExecutor =
    abstract ExecuteAsync<'Input, 'Output> :
        runId: RunId *
        nodePath: string *
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        idempotencyKey: string *
        onDelta: (string -> Task) *
        onApproval: (ApprovalRequest -> Task<ApprovalResponse>) *
        onSession: (CircuitSession -> Task) *
        cancellationToken: CancellationToken ->
            Task<RunResult<'Output>>

    /// Serializes adapter-owned session state for a durable checkpoint.
    abstract SerializeSessionAsync:
        agent: AgentDefinition * session: CircuitSession * options: RunOptions * cancellationToken: CancellationToken ->
            ValueTask<JsonElement>

    /// Restores adapter-owned session state with the receiving process's rebound run options.
    abstract DeserializeSessionAsync:
        agent: AgentDefinition * state: JsonElement * options: RunOptions * cancellationToken: CancellationToken ->
            ValueTask<CircuitSession>

    abstract ObserveAsync:
        observation: CircuitObservation * options: RunOptions * cancellationToken: CancellationToken -> Task

module private SchedulerFailure =
    let create code runId operationId requestId message innerException =
        CircuitFailure(code, message, ValueSome runId, operationId, requestId, innerException)

    let cancelled runId path =
        create CircuitFailureCode.Cancelled runId (ValueSome path) ValueNone "The Circuit run was cancelled." ValueNone

    let engine runId path message innerException =
        create CircuitFailureCode.Engine runId (ValueSome path) ValueNone message innerException

    let generated runId path message innerException =
        create CircuitFailureCode.GeneratedGraphIntegrity runId (ValueSome path) ValueNone message innerException

    let mismatch runId message =
        create CircuitFailureCode.CheckpointMismatch runId ValueNone ValueNone message ValueNone

    let checkpoint runId message innerException =
        create CircuitFailureCode.NotCheckpointable runId ValueNone ValueNone message innerException

    let approval runId requestId message =
        create CircuitFailureCode.InvalidApprovalResponse runId ValueNone (ValueSome requestId) message ValueNone

    let restamp runId path (failure: CircuitFailure) =
        CircuitFailure(
            failure.Code,
            failure.Message,
            ValueSome runId,
            (failure.OperationId |> ValueOption.orElse (ValueSome path)),
            failure.RequestId,
            failure.Exception
        )

module internal SchedulerInternals =
    exception ResourceLimitExceeded of string
    exception CheckpointFingerprintMismatch of string

    type BranchSelection =
        | ExactBranch of string * Node
        | DefaultBranch of Node
        | MissingBranch of string
        | BranchSelectionFailed of exn

    let selectBranch (handler: IBranchHandler) input =
        try
            let selected = handler.Select input

            match handler.Cases.TryGetValue selected with
            | true, child -> ExactBranch(selected, child)
            | _ ->
                match handler.Default with
                | ValueSome child -> DefaultBranch child
                | ValueNone -> MissingBranch selected
        with ex ->
            BranchSelectionFailed ex

    type BranchExecutionPlan =
        | EvaluateBranch of Node * string
        | EmitBranchFailure of CircuitFailure

    let planBranchExecution runId selectedPath selection =
        match selection with
        | ExactBranch(selected, child) -> EvaluateBranch(child, $"{selectedPath}[{selected}]")
        | DefaultBranch child -> EvaluateBranch(child, selectedPath + "[default]")
        | MissingBranch selected ->
            EmitBranchFailure(
                SchedulerFailure.engine runId selectedPath $"No branch matched key '{selected}'." ValueNone
            )
        | BranchSelectionFailed ex ->
            EmitBranchFailure(SchedulerFailure.engine runId selectedPath "The branch selector failed." (ValueSome ex))

    type ErasedHandlerOutcome =
        | HandlerSucceeded of obj
        | HandlerFailed of CircuitFailure

    let eraseHandlerResponse (typed: obj) =
        let outcome = typed.GetType().GetProperty("Outcome").GetValue(typed)

        let case, fields =
            FSharp.Reflection.FSharpValue.GetUnionFields(outcome, outcome.GetType())

        if case.Name = "Succeeded" then
            HandlerSucceeded fields[0]
        else
            HandlerFailed(fields[0] :?> CircuitFailure)

    type HandlerExecutionResult =
        | HandlerExecutionSucceeded of obj * CircuitFailure voption
        | HandlerExecutionFailed of exn

    let invokeRecoveryHandler invoke failure =
        try
            HandlerExecutionSucceeded(invoke failure, ValueNone)
        with ex ->
            HandlerExecutionFailed ex

    let invokeCodeHandler (handler: ICodeHandler) context input =
        task {
            try
                let! typed = handler.InvokeAsync(context, input)

                if isNull typed then
                    raise (InvalidOperationException("A code node returned null instead of a Response."))

                return
                    match eraseHandlerResponse typed with
                    | HandlerSucceeded value -> HandlerExecutionSucceeded(value, ValueNone)
                    | HandlerFailed failure -> HandlerExecutionSucceeded(null, ValueSome failure)
            with ex ->
                return HandlerExecutionFailed ex
        }

    let invokeAggregateHandler (handler: IAggregateHandler) context values cancellationToken =
        task {
            try
                let! typed = handler.InvokeAsync(context, values, cancellationToken)

                return
                    match eraseHandlerResponse typed with
                    | HandlerSucceeded value -> HandlerExecutionSucceeded(value, ValueNone)
                    | HandlerFailed failure -> HandlerExecutionSucceeded(null, ValueSome failure)
            with ex ->
                return HandlerExecutionFailed ex
        }

    type ErasedResponse =
        { Value: obj
          Failure: CircuitFailure voption
          Metadata: ResponseMetadata
          Typed: obj }

    type LoopBodyDecision =
        | ContinueLoop of ErasedResponse
        | PropagateLoopFailure of ErasedResponse

    let classifyLoopBody (responses: IReadOnlyList<ErasedResponse>) =
        if responses.Count <> 1 then
            raise (InvalidOperationException("A loop body must emit exactly one response per iteration."))

        let response = responses[0]

        match response.Failure with
        | ValueSome _ -> PropagateLoopFailure response
        | ValueNone -> ContinueLoop response

    type LoopTerminalPlan =
        | EmitLoopResourceLimit
        | EmitLoopSuccess of obj
        | EmitLoopFailure of ErasedResponse

    type RecoveryPlan =
        | PassThroughRecovery of ErasedResponse
        | InvokeRecovery of CircuitFailure

    let planRecovery (response: ErasedResponse) =
        match response.Failure with
        | ValueNone -> PassThroughRecovery response
        | ValueSome failure -> InvokeRecovery failure

    type Lane =
        { Key: ItemKey voption
          Ordinal: int64 voption
          Order: int64 list
          Identity: string }

    let selectEffectiveSession restored configured =
        match restored with
        | ValueSome session -> ValueSome session
        | ValueNone -> configured

    type SessionAgentAdmission =
        | RegisterSessionAgent
        | ReuseSessionAgent
        | RejectSessionAgentSharing of string

    let planSessionAgentAdmission existing current =
        match existing with
        | None -> RegisterSessionAgent
        | Some value when StringComparer.Ordinal.Equals(value, current) -> ReuseSessionAgent
        | Some value -> RejectSessionAgentSharing value

    type SerializedSessionAdmission =
        | DeserializeSerializedSession of AgentDefinition
        | RejectUnknownSerializedSession
        | RejectSerializedSessionOwner of string

    let planSerializedSessionAdmission candidate expectedAgentId =
        match candidate with
        | None -> RejectUnknownSerializedSession
        | Some(agent: AgentDefinition) ->
            let actualAgentId = agent.Id.Value + "@" + agent.Version.ToString()

            if StringComparer.Ordinal.Equals(expectedAgentId, actualAgentId) then
                DeserializeSerializedSession agent
            else
                RejectSerializedSessionOwner actualAgentId

    type SessionAliasAdmission =
        | BindSessionAlias of CircuitSession * AgentDefinition
        | RejectSessionAlias

    let planSessionAliasAdmission restoredSession candidate =
        match restoredSession, candidate with
        | Some session, Some(agent: AgentDefinition) -> BindSessionAlias(session, agent)
        | _ -> RejectSessionAlias

    let deserializeSessionCheckedAsync sessionKey deserialize =
        task {
            let! session = deserialize ()

            if isNull (box session) then
                raise (JsonException($"Checkpoint session '{sessionKey}' was restored to null by its agent adapter."))

            return session
        }

    let continueTask (pending: Task<'Value>) (continuation: 'Value -> Task) : Task =
        pending
            .ContinueWith(
                (fun (completed: Task<'Value>) ->
                    let value = completed.GetAwaiter().GetResult()

                    try
                        continuation value
                    with ex ->
                        Task.FromException(ex)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            .Unwrap()

    let continueUnitTask (pending: Task) (continuation: unit -> Task) : Task =
        pending
            .ContinueWith(
                (fun (completed: Task) ->
                    completed.GetAwaiter().GetResult()

                    try
                        continuation ()
                    with ex ->
                        Task.FromException(ex)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            .Unwrap()

    let runWithAsyncDisposal (work: unit -> Task) (dispose: unit -> Task) : Task =
        let workTask =
            try
                work ()
            with ex ->
                Task.FromException(ex)

        workTask
            .ContinueWith(
                (fun (completed: Task) ->
                    let disposal =
                        try
                            dispose ()
                        with ex ->
                            Task.FromException(ex)

                    disposal.ContinueWith(
                        (fun (disposed: Task) ->
                            disposed.GetAwaiter().GetResult()
                            completed.GetAwaiter().GetResult()),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            .Unwrap()

    let withOptionalPermitAsync
        (permit: SemaphoreSlim voption)
        (cancellationToken: CancellationToken)
        (work: unit -> Task)
        =
        task {
            match permit with
            | ValueSome(semaphore: SemaphoreSlim) -> do! semaphore.WaitAsync(cancellationToken)
            | ValueNone -> ()

            try
                do! work ()
            finally
                match permit with
                | ValueSome semaphore -> semaphore.Release() |> ignore
                | ValueNone -> ()
        }

    type ApprovalExecutionResult =
        | ApprovalAccepted of ApprovalResponse
        | ApprovalLimitReached of string

    let requestApprovalHandledAsync requestApproval request =
        task {
            try
                let! response = requestApproval request
                return ApprovalAccepted response
            with :? InvalidOperationException as ex when
                ex.Message.Contains("approval-round limit", StringComparison.Ordinal) ->
                return ApprovalLimitReached ex.Message
        }

    type RunOutcomePlan =
        | EmitFailedRun of CircuitFailure
        | EmitSucceededRun of total: int * succeeded: int

    let planRunOutcome failure total succeeded =
        match failure with
        | ValueSome value -> EmitFailedRun value
        | ValueNone -> EmitSucceededRun(total, succeeded)

    type SessionIdentityComparer() =
        interface IEqualityComparer<CircuitSession> with
            member _.Equals(left, right) = Object.ReferenceEquals(left, right)

            member _.GetHashCode(value) =
                Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value)

    type StoredResponse =
        { Success: bool
          ValueJson: string
          FailureCode: int
          FailureMessage: string
          FailureOperationId: string
          FailureRequestId: string
          InputTokens: int
          OutputTokens: int
          Attempt: int
          StartedAt: DateTimeOffset
          CompletedAt: DateTimeOffset
          IdempotencyKey: string
          SourceOrder: int64 array }

    type StoredApproval = { Approved: bool; Note: string }

    type ResumeState =
        { Responses: Dictionary<string, StoredResponse>
          DynamicFingerprints: Dictionary<string, string>
          DynamicInputs: Dictionary<string, string>
          SourceCounts: Dictionary<string, int64>
          SourceCursors: Dictionary<string, string>
          SourceSnapshots: Dictionary<string, string array>
          SourcePendingCursors: Dictionary<string, string>
          SourcePendingCompleted: HashSet<string>
          SourcePageCounts: Dictionary<string, int>
          PendingApprovalIds: HashSet<string>
          PendingApprovalRequests: Dictionary<string, ApprovalRequest>
          AcceptedApprovals: Dictionary<string, StoredApproval>
          Sessions: Dictionary<string, CircuitSession>
          SessionAgents: Dictionary<string, AgentDefinition>
          SessionAliases: Dictionary<string, string>
          SerializedSessions: Dictionary<string, struct (string * JsonElement)>
          mutable GeneratedNodeCount: int
          mutable ApprovalRoundCount: int
          SnapshotFailures: ResizeArray<exn> }

    let emptyResumeState () =
        { Responses = Dictionary(StringComparer.Ordinal)
          DynamicFingerprints = Dictionary(StringComparer.Ordinal)
          DynamicInputs = Dictionary(StringComparer.Ordinal)
          SourceCounts = Dictionary(StringComparer.Ordinal)
          SourceCursors = Dictionary(StringComparer.Ordinal)
          SourceSnapshots = Dictionary(StringComparer.Ordinal)
          SourcePendingCursors = Dictionary(StringComparer.Ordinal)
          SourcePendingCompleted = HashSet(StringComparer.Ordinal)
          SourcePageCounts = Dictionary(StringComparer.Ordinal)
          PendingApprovalIds = HashSet(StringComparer.Ordinal)
          PendingApprovalRequests = Dictionary(StringComparer.Ordinal)
          AcceptedApprovals = Dictionary(StringComparer.Ordinal)
          Sessions = Dictionary(StringComparer.Ordinal)
          SessionAgents = Dictionary(StringComparer.Ordinal)
          SessionAliases = Dictionary(StringComparer.Ordinal)
          SerializedSessions = Dictionary(StringComparer.Ordinal)
          GeneratedNodeCount = 0
          ApprovalRoundCount = 0
          SnapshotFailures = ResizeArray() }

    type SerializedSessionRestoreStep =
        { SessionKey: string
          Agent: AgentDefinition
          AdapterState: JsonElement }

    let matchingSessionAgent (agents: Dictionary<string, AgentDefinition>) (sessionKey: string) =
        agents
        |> Seq.filter (fun candidate -> sessionKey.StartsWith(candidate.Key + "|", StringComparison.Ordinal))
        |> Seq.sortByDescending (fun candidate -> candidate.Key.Length)
        |> Seq.tryHead
        |> Option.map (fun candidate -> candidate.Value)

    let planSerializedSessionRestores (agents: Dictionary<string, AgentDefinition>) (state: ResumeState) =
        state.SerializedSessions
        |> Seq.map (fun item ->
            let struct (expectedAgentId, adapterState) = item.Value
            let candidate = matchingSessionAgent agents item.Key

            match planSerializedSessionAdmission candidate expectedAgentId with
            | RejectUnknownSerializedSession ->
                raise (JsonException($"Checkpoint session '{item.Key}' does not match an agent node in the Circuit."))
            | RejectSerializedSessionOwner _ ->
                raise (JsonException($"Checkpoint session '{item.Key}' belongs to a different agent definition."))
            | DeserializeSerializedSession agent ->
                { SessionKey = item.Key
                  Agent = agent
                  AdapterState = adapterState })
        |> Seq.toArray

    let restoreSerializedSessionGroupsAsync
        (steps: SerializedSessionRestoreStep array)
        (deserialize: AgentDefinition -> JsonElement -> Task<CircuitSession>)
        =
        let restored = Dictionary<string, CircuitSession>(StringComparer.Ordinal)

        let rec restore index =
            task {
                if index < steps.Length then
                    let step: SerializedSessionRestoreStep = steps[index]

                    let! session =
                        deserializeSessionCheckedAsync step.SessionKey (fun () ->
                            deserialize step.Agent step.AdapterState)

                    restored[step.SessionKey] <- session
                    return! restore (index + 1)
                else
                    return restored
            }

        restore 0

    let bindSerializedSessionAliases
        (agents: Dictionary<string, AgentDefinition>)
        (state: ResumeState)
        (restoredGroups: Dictionary<string, CircuitSession>)
        =
        for alias in state.SessionAliases do
            let restoredSession =
                match restoredGroups.TryGetValue alias.Value with
                | true, session -> Some session
                | _ -> None

            let candidate = matchingSessionAgent agents alias.Key

            match planSessionAliasAdmission restoredSession candidate with
            | BindSessionAlias(session, agent) ->
                state.Sessions[alias.Key] <- session
                state.SessionAgents[alias.Key] <- agent
            | RejectSessionAlias ->
                raise (
                    JsonException($"Checkpoint session binding '{alias.Key}' references an unknown serialized session.")
                )

    let hash (value: string) =
        let bytes: byte[] = Text.Encoding.UTF8.GetBytes(value)
        let digest: byte[] = Security.Cryptography.SHA256.HashData(bytes)
        Convert.ToHexString(digest).ToLowerInvariant()

    let laneToken lane = lane.Identity

    let rootLane =
        { Key = ValueNone
          Ordinal = ValueNone
          Order = []
          Identity = "root" }

    let childLane (parent: Lane) (key: string) (ordinal: int64) : Lane =
        let localKey = ItemKey.Create key
        let segment = $"k{key.Length}:{key};o{ordinal}"

        let composedKey, identity =
            match parent.Key with
            | ValueNone -> localKey, segment
            | ValueSome _ ->
                let value = $"{parent.Identity.Length}:{parent.Identity}/{segment}"
                ItemKey.Create value, value

        { Key = ValueSome composedKey
          Ordinal = ValueSome ordinal
          Order = parent.Order @ [ ordinal ]
          Identity = identity }

    let laneFromMetadata (key: ItemKey voption) (ordinal: int64 voption) (order: IReadOnlyList<int64>) : Lane =
        let identity =
            match key, ordinal with
            | ValueSome itemKey, ValueSome sourceOrdinal -> $"k{itemKey.Value.Length}:{itemKey.Value};o{sourceOrdinal}"
            | ValueSome itemKey, ValueNone -> $"k{itemKey.Value.Length}:{itemKey.Value}"
            | ValueNone, ValueSome sourceOrdinal -> $"o{sourceOrdinal}"
            | ValueNone, ValueNone -> "root"

        { Key = key
          Ordinal = ordinal
          Order = order |> Seq.toList
          Identity = identity }

    let journalKey path lane = path + "|" + laneToken lane

    let nodeId node =
        match node with
        | Agent(id, _, _)
        | Code(id, _, _)
        | Value(id, _, _)
        | Items(id, _, _)
        | AsyncSource(id, _, _)
        | ResumableSource(id, _, _)
        | Dynamic(id, _, _, _, _)
        | Attempt(id, _)
        | Recover(id, _, _, _)
        | Branch(id, _, _)
        | Merge(id, _, _, _)
        | Loop(id, _, _, _)
        | Approval(id, _, _)
        | Aggregate(id, _, _, _)
        | Named(id, _) -> id
        | Then _ -> "then"

    let rec agentIds (node: Node) =
        seq {
            match node with
            | Agent(_, _, handler) -> yield handler.Agent.Id.Value + "@" + handler.Agent.Version.ToString()
            | Then(left, right) ->
                yield! agentIds left
                yield! agentIds right
            | Dynamic(_, _, _, _, previous)
            | Attempt(_, previous)
            | Recover(_, _, _, previous)
            | Aggregate(_, _, _, previous)
            | Named(_, previous) -> yield! agentIds previous
            | Branch(_, _, handler) ->
                for child in handler.Cases.Values do
                    yield! agentIds child

                match handler.Default with
                | ValueSome child -> yield! agentIds child
                | ValueNone -> ()
            | Merge(_, _, _, branches) ->
                for child in branches do
                    yield! agentIds child
            | Loop(_, _, _, handler) -> yield! agentIds handler.Body
            | _ -> ()
        }

    let rec nodeCount (node: Node) =
        match node with
        | Then(left, right) -> nodeCount left + nodeCount right
        | Dynamic(_, _, _, _, previous)
        | Attempt(_, previous)
        | Recover(_, _, _, previous)
        | Aggregate(_, _, _, previous)
        | Named(_, previous) -> 1 + nodeCount previous
        | Branch(_, _, handler) ->
            1
            + (handler.Cases.Values |> Seq.sumBy nodeCount)
            + (handler.Default |> ValueOption.map nodeCount |> ValueOption.defaultValue 0)
        | Merge(_, _, _, branches) -> 1 + (branches |> Array.sumBy nodeCount)
        | Loop(_, _, _, handler) -> 1 + nodeCount handler.Body
        | _ -> 1

    let validateGeneratedNode (root: Node) =
        let identities = HashSet<string>(StringComparer.Ordinal)

        let rec visit path node =
            let identity, children =
                match node with
                | Agent(id, _, _)
                | Code(id, _, _)
                | Value(id, _, _)
                | Items(id, _, _)
                | AsyncSource(id, _, _)
                | ResumableSource(id, _, _)
                | Approval(id, _, _) -> id, Array.empty
                | Then(left, right) -> "then", [| left; right |]
                | Dynamic(id, _, maximum, _, previous) ->
                    if maximum < 1 then
                        invalidOp "A generated dynamic node has an invalid concurrency bound."

                    id, [| previous |]
                | Attempt(id, previous)
                | Recover(id, _, _, previous)
                | Aggregate(id, _, _, previous)
                | Named(id, previous) -> id, [| previous |]
                | Branch(id, _, handler) ->
                    id,
                    [| yield! handler.Cases.Values
                       match handler.Default with
                       | ValueSome child -> yield child
                       | ValueNone -> () |]
                | Merge(id, _, maximum, branches) ->
                    if maximum < 1 then
                        invalidOp "A generated merge node has an invalid concurrency bound."

                    id, branches
                | Loop(id, _, maximum, handler) ->
                    if maximum < 1 then
                        invalidOp "A generated loop node has an invalid iteration bound."

                    id, [| handler.Body |]

            let scoped = path + "/" + identity

            if not (identities.Add scoped) then
                invalidOp $"A generated Circuit contains duplicate node identity '{identity}'."

            children |> Array.iteri (fun index child -> visit ($"{scoped}/{index}") child)

        visit "generated" root

    let validateDynamicResume (root: Node) (rootPath: string) (state: ResumeState) (options: RunOptions) =
        let handlers =
            Dictionary<string, struct (IDynamicHandler * int)>(StringComparer.Ordinal)

        let agents = Dictionary<string, AgentDefinition>(StringComparer.Ordinal)

        let rec collect node path depth =
            match node with
            | Agent(id, _, handler) -> agents[path + "/" + id] <- handler.Agent
            | Then(left, right) ->
                collect left (path + "/previous") depth
                collect right (path + "/next") depth
            | Dynamic(id, _, _, handler, previous) ->
                collect previous (path + "/previous") depth
                handlers[path + "/" + id] <- struct (handler, depth + 1)
            | Attempt(_, previous)
            | Recover(_, _, _, previous)
            | Aggregate(_, _, _, previous) -> collect previous (path + "/previous") depth
            | Named(id, child) -> collect child (path + "/" + id) depth
            | Branch(id, _, handler) ->
                for branch in handler.Cases do
                    collect branch.Value ($"{path}/{id}[{branch.Key}]") depth

                match handler.Default with
                | ValueSome child -> collect child ($"{path}/{id}[default]") depth
                | ValueNone -> ()
            | Merge(id, _, _, branches) ->
                branches
                |> Array.iteri (fun index child -> collect child ($"{path}/{id}/branch-{index}") depth)
            | Loop(id, _, maximum, handler) ->
                for iteration in 0 .. maximum - 1 do
                    collect handler.Body ($"{path}/{id}/iteration-{iteration}") depth
            | _ -> ()

        collect root rootPath 0
        let mutable generatedCount = 0

        if state.DynamicFingerprints.Count <> state.DynamicInputs.Count then
            raise (
                CheckpointFingerprintMismatch
                    "The checkpoint does not contain one saved input for every generated Circuit."
            )

        for entry in state.DynamicInputs |> Seq.sortBy (fun item -> item.Key.Length) do
            let matching =
                handlers
                |> Seq.filter (fun candidate -> entry.Key.StartsWith(candidate.Key + "[", StringComparison.Ordinal))
                |> Seq.sortByDescending (fun candidate -> candidate.Key.Length)
                |> Seq.tryHead

            match matching with
            | None -> raise (CheckpointFingerprintMismatch entry.Key)
            | Some candidate ->
                let struct (handler, depth) = candidate.Value

                if depth > options.MaxDynamicDepth then
                    raise (
                        ResourceLimitExceeded
                            "The generated-graph depth limit was exceeded while validating the checkpoint."
                    )

                let input = JsonSerializer.Deserialize(entry.Value, handler.InputType)
                let child, fingerprint = handler.Build(input)
                validateGeneratedNode child

                let expected =
                    match state.DynamicFingerprints.TryGetValue entry.Key with
                    | true, value -> value
                    | _ -> raise (CheckpointFingerprintMismatch entry.Key)

                if not (StringComparer.Ordinal.Equals(expected, fingerprint)) then
                    raise (CheckpointFingerprintMismatch entry.Key)

                generatedCount <- generatedCount + nodeCount child

                if generatedCount > options.MaxDynamicNodes then
                    raise (
                        ResourceLimitExceeded "The generated-node limit was exceeded while validating the checkpoint."
                    )

                // Materialize nested dynamic handlers now so every saved child is checked before execution starts.
                collect child entry.Key depth

        if generatedCount <> state.GeneratedNodeCount then
            raise (
                CheckpointFingerprintMismatch
                    "The checkpoint generated-node counter does not match its saved dynamic children."
            )

        agents

    let rec outputType (node: Node) : Type =
        match node with
        | Agent(_, _, handler) -> handler.OutputType
        | Code(_, _, handler) -> handler.OutputType
        | Value(_, outputType, _) -> outputType
        | Items(_, _, handler) -> handler.ItemType
        | AsyncSource(_, _, handler) -> handler.ItemType
        | ResumableSource(_, _, handler) -> handler.ItemType
        | Dynamic(_, _, _, handler, _) -> handler.OutputType
        | Attempt(_, previous) -> typedefof<Response<_>>.MakeGenericType(outputType previous)
        | Recover(_, _, handler, _) -> handler.OutputType
        | Branch(_, _, handler) -> handler.OutputType
        | Merge(_, _, _, branches) -> outputType branches[0]
        | Loop(_, _, _, handler) -> handler.ValueType
        | Approval _ -> typeof<ApprovalResponse>
        | Aggregate(_, _, handler, _) -> handler.OutputType
        | Named(_, child) -> outputType child
        | Then(_, right) -> outputType right

    let createMetadata runId lineage path lane options startedAt completedAt usage session =
        let itemToken = laneToken lane
        let idempotency = hash $"{lineage}|{path}|{itemToken}|1"

        ResponseMetadata(
            lane.Key,
            lane.Ordinal,
            lane.Order |> List.toArray,
            runId,
            path,
            usage,
            session,
            1,
            startedAt,
            completedAt,
            idempotency
        )

    let createTypedResponse (outputType: Type) (value: obj) (failure: CircuitFailure voption) metadata =
        let responseType = typedefof<Response<_>>.MakeGenericType(outputType)
        let outcomeType = typedefof<Outcome<_>>.MakeGenericType(outputType)

        let outcome =
            match failure with
            | ValueNone ->
                let case =
                    FSharp.Reflection.FSharpType.GetUnionCases(outcomeType)
                    |> Array.find (fun case -> case.Name = "Succeeded")

                FSharp.Reflection.FSharpValue.MakeUnion(case, [| value |])
            | ValueSome error ->
                let case =
                    FSharp.Reflection.FSharpType.GetUnionCases(outcomeType)
                    |> Array.find (fun case -> case.Name = "Failed")

                FSharp.Reflection.FSharpValue.MakeUnion(case, [| box error |])

        let ctor =
            responseType.GetConstructor(
                BindingFlags.Instance ||| BindingFlags.NonPublic,
                null,
                [| outcomeType; typeof<ResponseMetadata> |],
                null
            )

        ctor.Invoke([| outcome; metadata |])

    let erase outputType value failure metadata =
        { Value = value
          Failure = failure
          Metadata = metadata
          Typed = createTypedResponse outputType value failure metadata }

    let private requiredProperty (owner: JsonElement) (name: string) (kind: JsonValueKind) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if not (owner.TryGetProperty(name, &value)) || value.ValueKind <> kind then
            raise (JsonException($"Checkpoint payload property '{name}' is missing or has the wrong JSON kind."))

        value

    let parseResumeState (payload: JsonElement) =
        if payload.ValueKind <> JsonValueKind.Object then
            raise (JsonException("Checkpoint payload must be an object."))

        requiredProperty payload "inputType" JsonValueKind.String |> ignore

        requiredProperty payload "input" (payload.GetProperty("input").ValueKind)
        |> ignore

        requiredProperty payload "options" JsonValueKind.Object |> ignore
        let state = emptyResumeState ()
        let responses = requiredProperty payload "responses" JsonValueKind.Array

        for item in responses.EnumerateArray() do
            let key = (requiredProperty item "key" JsonValueKind.String).GetString()
            let successElement = item.GetProperty("success")

            if
                successElement.ValueKind <> JsonValueKind.True
                && successElement.ValueKind <> JsonValueKind.False
            then
                raise (JsonException("Checkpoint response success must be a Boolean."))

            let success = successElement.GetBoolean()
            let mutable property = Unchecked.defaultof<JsonElement>

            let stored =
                { Success = success
                  ValueJson =
                    if success then
                        if not (item.TryGetProperty("value", &property)) then
                            raise (JsonException("A successful checkpoint response is missing its value."))

                        property.GetRawText()
                    else
                        null
                  FailureCode =
                    if success then
                        0
                    else
                        (requiredProperty item "failureCode" JsonValueKind.Number).GetInt32()
                  FailureMessage =
                    if success then
                        null
                    else
                        (requiredProperty item "failureMessage" JsonValueKind.String).GetString()
                  FailureOperationId =
                    if
                        item.TryGetProperty("failureOperationId", &property)
                        && property.ValueKind = JsonValueKind.String
                    then
                        property.GetString()
                    else
                        null
                  FailureRequestId =
                    if
                        item.TryGetProperty("failureRequestId", &property)
                        && property.ValueKind = JsonValueKind.String
                    then
                        property.GetString()
                    else
                        null
                  InputTokens = (requiredProperty item "inputTokens" JsonValueKind.Number).GetInt32()
                  OutputTokens = (requiredProperty item "outputTokens" JsonValueKind.Number).GetInt32()
                  Attempt = (requiredProperty item "attempt" JsonValueKind.Number).GetInt32()
                  StartedAt = (requiredProperty item "startedAt" JsonValueKind.String).GetDateTimeOffset()
                  CompletedAt = (requiredProperty item "completedAt" JsonValueKind.String).GetDateTimeOffset()
                  IdempotencyKey = (requiredProperty item "idempotencyKey" JsonValueKind.String).GetString()
                  SourceOrder =
                    (requiredProperty item "sourceOrder" JsonValueKind.Array).EnumerateArray()
                    |> Seq.map (fun value ->
                        if value.ValueKind <> JsonValueKind.Number then
                            raise (JsonException("Response source-order entries must be numbers."))

                        value.GetInt64())
                    |> Seq.toArray }

            if not (state.Responses.TryAdd(key, stored)) then
                raise (JsonException($"Checkpoint response key '{key}' is duplicated."))

        let readMap (name: string) (target: Dictionary<string, string>) =
            let map = requiredProperty payload name JsonValueKind.Object

            for item in map.EnumerateObject() do
                if item.Value.ValueKind <> JsonValueKind.String then
                    raise (JsonException($"Checkpoint map '{name}' contains a non-string value."))

                target[item.Name] <- item.Value.GetString()

        readMap "dynamicFingerprints" state.DynamicFingerprints
        readMap "dynamicInputs" state.DynamicInputs
        readMap "sourceCursors" state.SourceCursors

        let pendingMap = requiredProperty payload "sourcePending" JsonValueKind.Object

        for pending in pendingMap.EnumerateObject() do
            let value = pending.Value

            if value.ValueKind <> JsonValueKind.Object then
                raise (JsonException("A pending source page must be an object."))

            let mutable cursor = Unchecked.defaultof<JsonElement>

            if not (value.TryGetProperty("cursor", &cursor)) then
                raise (JsonException("A pending source page is missing its cursor."))

            match cursor.ValueKind with
            | JsonValueKind.String -> state.SourcePendingCursors[pending.Name] <- cursor.GetString()
            | JsonValueKind.Null -> state.SourcePendingCursors[pending.Name] <- null
            | _ -> raise (JsonException("A pending source cursor must be a string or null."))

            let completedElement = value.GetProperty("completed")

            if
                completedElement.ValueKind <> JsonValueKind.True
                && completedElement.ValueKind <> JsonValueKind.False
            then
                raise (JsonException("A pending source completed flag must be a Boolean."))

            if completedElement.GetBoolean() then
                state.SourcePendingCompleted.Add pending.Name |> ignore

        let snapshots = requiredProperty payload "sourceSnapshots" JsonValueKind.Object

        for source in snapshots.EnumerateObject() do
            if source.Value.ValueKind <> JsonValueKind.Array then
                raise (JsonException("Source snapshots must be arrays."))

            state.SourceSnapshots[source.Name] <- source.Value.EnumerateArray() |> Seq.map _.GetRawText() |> Seq.toArray

        let counts = requiredProperty payload "sourceCounts" JsonValueKind.Object

        for item in counts.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.Number then
                raise (JsonException("Source counts must be numbers."))

            let value = item.Value.GetInt64()

            if value < 0L then
                raise (JsonException("Source counts cannot be negative."))

            state.SourceCounts[item.Name] <- value

        let pageCounts = requiredProperty payload "sourcePageCounts" JsonValueKind.Object

        for item in pageCounts.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.Number then
                raise (JsonException("Source page counts must be numbers."))

            let value = item.Value.GetInt32()

            if value < 0 then
                raise (JsonException("Source page counts cannot be negative."))

            state.SourcePageCounts[item.Name] <- value

        let pendingApprovals =
            requiredProperty payload "pendingApprovalIds" JsonValueKind.Array

        for item in pendingApprovals.EnumerateArray() do
            if item.ValueKind <> JsonValueKind.String then
                raise (JsonException("Pending approval identifiers must be strings."))

            state.PendingApprovalIds.Add(item.GetString()) |> ignore

        let pendingRequests =
            requiredProperty payload "pendingApprovalRequests" JsonValueKind.Object

        for item in pendingRequests.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.Object then
                raise (JsonException("Pending approval requests must be objects."))

            let toolName =
                (requiredProperty item.Value "toolName" JsonValueKind.String).GetString()

            let mutable arguments = Unchecked.defaultof<JsonElement>

            let argumentsJson =
                if item.Value.TryGetProperty("argumentsJson", &arguments) then
                    match arguments.ValueKind with
                    | JsonValueKind.String -> ValueSome(arguments.GetString())
                    | JsonValueKind.Null -> ValueNone
                    | _ -> raise (JsonException("Approval arguments must be a string or null."))
                else
                    raise (JsonException("A pending approval request is missing argumentsJson."))

            let mutable promptElement = Unchecked.defaultof<JsonElement>

            let prompt =
                if not (item.Value.TryGetProperty("prompt", &promptElement)) then
                    raise (JsonException("A pending approval request is missing its prompt."))

                match promptElement.ValueKind with
                | JsonValueKind.Null -> ValueNone
                | JsonValueKind.Object ->
                    let title =
                        (requiredProperty promptElement "title" JsonValueKind.String).GetString()

                    let message =
                        (requiredProperty promptElement "message" JsonValueKind.String).GetString()

                    let metadataElement = requiredProperty promptElement "metadata" JsonValueKind.Object

                    let metadata =
                        metadataElement.EnumerateObject()
                        |> Seq.map (fun property ->
                            if property.Value.ValueKind <> JsonValueKind.String then
                                raise (JsonException("Approval prompt metadata values must be strings."))

                            KeyValuePair(property.Name, property.Value.GetString()))

                    ValueSome(ApprovalPrompt(title, message, metadata))
                | _ -> raise (JsonException("A pending approval prompt must be an object or null."))

            let request = ApprovalRequest(item.Name, toolName, argumentsJson, prompt)
            state.PendingApprovalRequests[item.Name] <- request

        let approvals = requiredProperty payload "acceptedApprovals" JsonValueKind.Object

        for item in approvals.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.Object then
                raise (JsonException("Accepted approval decisions must be objects."))

            let approved = item.Value.GetProperty("approved").GetBoolean()
            let mutable note = Unchecked.defaultof<JsonElement>

            state.AcceptedApprovals[item.Name] <-
                { Approved = approved
                  Note =
                    if
                        item.Value.TryGetProperty("note", &note)
                        && note.ValueKind = JsonValueKind.String
                    then
                        note.GetString()
                    else
                        null }

        state.GeneratedNodeCount <- (requiredProperty payload "generatedNodeCount" JsonValueKind.Number).GetInt32()

        state.ApprovalRoundCount <- (requiredProperty payload "approvalRoundCount" JsonValueKind.Number).GetInt32()

        if state.GeneratedNodeCount < 0 || state.ApprovalRoundCount < 0 then
            raise (JsonException("Lineage counters cannot be negative."))

        let aliases = requiredProperty payload "sessionAliases" JsonValueKind.Object

        for item in aliases.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.String then
                raise (JsonException("Checkpoint session aliases must be strings."))

            state.SessionAliases[item.Name] <- item.Value.GetString()

        let sessions = requiredProperty payload "sessions" JsonValueKind.Object

        for item in sessions.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.Object then
                raise (JsonException("Checkpoint sessions must be objects."))

            let agentId =
                (requiredProperty item.Value "agentId" JsonValueKind.String).GetString()

            let mutable sessionState = Unchecked.defaultof<JsonElement>

            if not (item.Value.TryGetProperty("state", &sessionState)) then
                raise (JsonException("A checkpoint session is missing its adapter state."))

            state.SerializedSessions[item.Name] <- struct (agentId, sessionState.Clone())

        state

    let parseRunOptions (payload: JsonElement) (fallbackServices: IServiceProvider) =
        let value = requiredProperty payload "options" JsonValueKind.Object

        let optionalString (name: string) =
            let mutable property = Unchecked.defaultof<JsonElement>

            if value.TryGetProperty(name, &property) then
                match property.ValueKind with
                | JsonValueKind.String -> ValueSome(property.GetString())
                | JsonValueKind.Null -> ValueNone
                | _ -> raise (JsonException($"Checkpoint option '{name}' must be a string or null."))
            else
                raise (JsonException($"Checkpoint option '{name}' is missing."))

        let tagsElement = requiredProperty value "tags" JsonValueKind.Object
        let tags = Dictionary<string, string>(StringComparer.Ordinal)

        for item in tagsElement.EnumerateObject() do
            if item.Value.ValueKind <> JsonValueKind.String then
                raise (JsonException("Checkpoint tag values must be strings."))

            tags.Add(item.Name, item.Value.GetString())

        RunOptions(
            ValueNone,
            optionalString "tenantId",
            optionalString "userId",
            ReadOnlyDictionary(tags) :> IReadOnlyDictionary<string, string>,
            enum<StructuredOutputPolicy> (
                (requiredProperty value "structuredOutputPolicy" JsonValueKind.Number).GetInt32()
            ),
            enum<SensitiveDataMode> ((requiredProperty value "sensitiveDataMode" JsonValueKind.Number).GetInt32()),
            fallbackServices,
            (requiredProperty value "maxConcurrency" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "eventBufferCapacity" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxDynamicDepth" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxDynamicNodes" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxApprovalRounds" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxSourcePageSize" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxSourcePages" JsonValueKind.Number).GetInt32(),
            (requiredProperty value "maxCheckpointBytes" JsonValueKind.Number).GetInt32(),
            TimeSpan.FromMilliseconds(
                (requiredProperty value "disposalDrainMilliseconds" JsonValueKind.Number).GetDouble()
            )
        )

    let writeResumeState (state: ResumeState) (inputType: Type) (input: obj) (options: RunOptions) =
        for alias in state.SessionAliases do
            if not (state.SerializedSessions.ContainsKey alias.Value) then
                raise (
                    InvalidOperationException(
                        $"Session binding '{alias.Key}' has not reached a checkpoint-safe adapter boundary."
                    )
                )

        let root = JsonObject()
        root["inputType"] <- JsonValue.Create(inputType.AssemblyQualifiedName)
        root["input"] <- JsonNode.Parse(JsonSerializer.Serialize(input, inputType))
        let optionNode = JsonObject()
        optionNode["tenantId"] <- options.TenantId |> ValueOption.map JsonValue.Create |> ValueOption.toObj
        optionNode["userId"] <- options.UserId |> ValueOption.map JsonValue.Create |> ValueOption.toObj
        let tags = JsonObject()

        for item in options.Tags |> Seq.sortBy _.Key do
            tags[item.Key] <- JsonValue.Create(item.Value)

        optionNode["tags"] <- tags
        optionNode["structuredOutputPolicy"] <- JsonValue.Create(int options.StructuredOutputPolicy)
        optionNode["sensitiveDataMode"] <- JsonValue.Create(int options.SensitiveDataMode)
        optionNode["maxConcurrency"] <- JsonValue.Create(options.MaxConcurrency)
        optionNode["eventBufferCapacity"] <- JsonValue.Create(options.EventBufferCapacity)
        optionNode["maxDynamicDepth"] <- JsonValue.Create(options.MaxDynamicDepth)
        optionNode["maxDynamicNodes"] <- JsonValue.Create(options.MaxDynamicNodes)
        optionNode["maxApprovalRounds"] <- JsonValue.Create(options.MaxApprovalRounds)
        optionNode["maxSourcePageSize"] <- JsonValue.Create(options.MaxSourcePageSize)
        optionNode["maxSourcePages"] <- JsonValue.Create(options.MaxSourcePages)
        optionNode["maxCheckpointBytes"] <- JsonValue.Create(options.MaxCheckpointBytes)
        optionNode["disposalDrainMilliseconds"] <- JsonValue.Create(options.DisposalDrainTimeout.TotalMilliseconds)
        root["options"] <- optionNode
        let responses = JsonArray()

        for item in state.Responses |> Seq.sortBy _.Key do
            let node = JsonObject()
            node["key"] <- JsonValue.Create(item.Key)
            node["success"] <- JsonValue.Create(item.Value.Success)

            node["inputTokens"] <- JsonValue.Create(item.Value.InputTokens)
            node["outputTokens"] <- JsonValue.Create(item.Value.OutputTokens)
            node["attempt"] <- JsonValue.Create(item.Value.Attempt)
            node["startedAt"] <- JsonValue.Create(item.Value.StartedAt)
            node["completedAt"] <- JsonValue.Create(item.Value.CompletedAt)
            node["idempotencyKey"] <- JsonValue.Create(item.Value.IdempotencyKey)
            let sourceOrder = JsonArray()

            for ordinal in item.Value.SourceOrder do
                sourceOrder.Add(JsonValue.Create ordinal)

            node["sourceOrder"] <- sourceOrder

            if item.Value.Success then
                node["value"] <- JsonNode.Parse(item.Value.ValueJson)
            else
                node["failureCode"] <- JsonValue.Create(item.Value.FailureCode)
                node["failureMessage"] <- JsonValue.Create(item.Value.FailureMessage)

                if not (isNull item.Value.FailureOperationId) then
                    node["failureOperationId"] <- JsonValue.Create(item.Value.FailureOperationId)

                if not (isNull item.Value.FailureRequestId) then
                    node["failureRequestId"] <- JsonValue.Create(item.Value.FailureRequestId)

            responses.Add(node)

        root["responses"] <- responses

        let writeMap (name: string) (values: seq<KeyValuePair<string, string>>) =
            let node = JsonObject()

            for item in values |> Seq.sortBy _.Key do
                node[item.Key] <- JsonValue.Create(item.Value)

            root[name] <- node

        writeMap "dynamicFingerprints" state.DynamicFingerprints
        writeMap "dynamicInputs" state.DynamicInputs
        writeMap "sourceCursors" state.SourceCursors
        let pending = JsonObject()

        for item in state.SourcePendingCursors |> Seq.sortBy _.Key do
            let value = JsonObject()

            value["cursor"] <-
                if isNull item.Value then
                    null
                else
                    JsonValue.Create(item.Value)

            value["completed"] <- JsonValue.Create(state.SourcePendingCompleted.Contains item.Key)
            pending[item.Key] <- value

        root["sourcePending"] <- pending
        let snapshots = JsonObject()

        for source in state.SourceSnapshots |> Seq.sortBy _.Key do
            let values = JsonArray()

            for value in source.Value do
                values.Add(JsonNode.Parse(value))

            snapshots[source.Key] <- values

        root["sourceSnapshots"] <- snapshots
        let counts = JsonObject()

        for item in state.SourceCounts |> Seq.sortBy _.Key do
            counts[item.Key] <- JsonValue.Create(item.Value)

        root["sourceCounts"] <- counts
        let pageCounts = JsonObject()

        for item in state.SourcePageCounts |> Seq.sortBy _.Key do
            pageCounts[item.Key] <- JsonValue.Create(item.Value)

        root["sourcePageCounts"] <- pageCounts
        let pendingApprovals = JsonArray()

        for requestId in state.PendingApprovalIds |> Seq.sort do
            pendingApprovals.Add(JsonValue.Create(requestId))

        root["pendingApprovalIds"] <- pendingApprovals
        let pendingRequests = JsonObject()

        for item in state.PendingApprovalRequests |> Seq.sortBy _.Key do
            let request = JsonObject()
            request["toolName"] <- JsonValue.Create(item.Value.ToolName)

            request["argumentsJson"] <-
                item.Value.ArgumentsJson
                |> ValueOption.map JsonValue.Create
                |> ValueOption.toObj

            request["prompt"] <-
                match item.Value.Prompt with
                | ValueNone -> null
                | ValueSome prompt ->
                    let value = JsonObject()
                    value["title"] <- JsonValue.Create(prompt.Title)
                    value["message"] <- JsonValue.Create(prompt.Message)
                    let metadata = JsonObject()

                    for entry in prompt.Metadata |> Seq.sortBy _.Key do
                        metadata[entry.Key] <- JsonValue.Create(entry.Value)

                    value["metadata"] <- metadata
                    value

            pendingRequests[item.Key] <- request

        root["pendingApprovalRequests"] <- pendingRequests
        let approvals = JsonObject()

        for item in state.AcceptedApprovals |> Seq.sortBy _.Key do
            let value = JsonObject()
            value["approved"] <- JsonValue.Create(item.Value.Approved)

            if not (isNull item.Value.Note) then
                value["note"] <- JsonValue.Create(item.Value.Note)

            approvals[item.Key] <- value

        root["acceptedApprovals"] <- approvals
        root["generatedNodeCount"] <- JsonValue.Create(state.GeneratedNodeCount)
        root["approvalRoundCount"] <- JsonValue.Create(state.ApprovalRoundCount)
        let aliases = JsonObject()

        for item in state.SessionAliases |> Seq.sortBy _.Key do
            aliases[item.Key] <- JsonValue.Create(item.Value)

        root["sessionAliases"] <- aliases
        let sessions = JsonObject()

        for item in state.SerializedSessions |> Seq.sortBy _.Key do
            let struct (agentId, adapterState) = item.Value
            let value = JsonObject()
            value["agentId"] <- JsonValue.Create(agentId)
            value["state"] <- JsonNode.Parse(adapterState.GetRawText())
            sessions[item.Key] <- value

        root["sessions"] <- sessions
        use document = JsonDocument.Parse(root.ToJsonString())
        document.RootElement.Clone()

    type private AgentDispatch =
        static member Execute<'Input, 'Output>
            (
                executor: IAgentLeafExecutor,
                runId: RunId,
                path: string,
                handler: IAgentHandler,
                input: obj,
                options: RunOptions,
                idempotencyKey: string,
                onDelta: string -> Task,
                onApproval: ApprovalRequest -> Task<ApprovalResponse>,
                onSession: CircuitSession -> Task,
                cancellationToken: CancellationToken
            ) =
            executor.ExecuteAsync(
                runId,
                path,
                handler.Agent,
                unbox<Signature<'Input, 'Output>> handler.Signature,
                unbox<'Input> input,
                options,
                idempotencyKey,
                onDelta,
                onApproval,
                onSession,
                cancellationToken
            )
            :> Task

    let invokeAgent
        executor
        runId
        path
        (handler: IAgentHandler)
        input
        options
        idempotencyKey
        onDelta
        onApproval
        onSession
        cancellationToken
        =
        task {
            let genericMethod =
                typeof<AgentDispatch>.GetMethod("Execute", BindingFlags.Static ||| BindingFlags.NonPublic)

            let genericTypes =
                genericMethod.GetGenericArguments()
                |> Array.map (fun argument ->
                    if argument.Name.Contains("Input", StringComparison.Ordinal) then
                        handler.InputType
                    else
                        handler.OutputType)

            let methodInfo = genericMethod.MakeGenericMethod(genericTypes)

            let taskValue =
                methodInfo.Invoke(
                    null,
                    [| executor
                       box runId
                       path
                       handler
                       input
                       options
                       idempotencyKey
                       box onDelta
                       box onApproval
                       box onSession
                       box cancellationToken |]
                )
                :?> Task

            do! taskValue
            let result = taskValue.GetType().GetProperty("Result").GetValue(taskValue)
            // CircuitResult is invariant; read it through reflection.
            let circuitResult = result.GetType().GetProperty("Result").GetValue(result)

            let isSuccess =
                circuitResult.GetType().GetProperty("IsSuccess").GetValue(circuitResult) :?> bool

            let value =
                if isSuccess then
                    circuitResult.GetType().GetProperty("Value").GetValue(circuitResult)
                else
                    null

            let failure =
                if isSuccess then
                    ValueNone
                else
                    ValueSome(circuitResult.GetType().GetProperty("Failure").GetValue(circuitResult) :?> CircuitFailure)

            let usage = result.GetType().GetProperty("Usage").GetValue(result) :?> RunUsage

            let session =
                result.GetType().GetProperty("Session").GetValue(result) :?> CircuitSession voption

            return value, failure, usage, session
        }

    type SchedulerRun<'Input, 'Output>
        (
            executor: IAgentLeafExecutor,
            circuit: Circuit.Core.Circuit<'Input, 'Output>,
            input: 'Input,
            options: RunOptions,
            cancellationToken: CancellationToken,
            lineageId: string,
            resumeState: ResumeState,
            initialFailure: CircuitFailure voption
        ) =
        let runId = RunId.New()
        let startedAt = DateTimeOffset.UtcNow
        let linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        let channelOptions = BoundedChannelOptions(options.EventBufferCapacity)

        do
            channelOptions.SingleReader <- true
            channelOptions.SingleWriter <- false
            channelOptions.FullMode <- BoundedChannelFullMode.Wait

        let events = Channel.CreateBounded<CircuitEvent<'Output>>(channelOptions)
        let stateGate = obj ()

        let pendingApprovals =
            ConcurrentDictionary<string, TaskCompletionSource<ApprovalResponse>>(StringComparer.Ordinal)

        let usedApprovals = ConcurrentDictionary<string, byte>(StringComparer.Ordinal)

        // Admission is bounded per source stage. A parent lane may therefore hold its own
        // stage permit while expanding a nested source without waiting on that same permit.
        let admissionSemaphores =
            ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)

        let admissionSemaphore path =
            admissionSemaphores.GetOrAdd(
                path,
                fun _ -> new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency)
            )

        let outputs = ResizeArray<Response<'Output>>()
        let usageEntries = Dictionary<string, RunUsage>(StringComparer.Ordinal)

        let sessionSemaphores =
            ConcurrentDictionary<CircuitSession, SemaphoreSlim>(SessionIdentityComparer())

        let sessionGroupKeys =
            ConcurrentDictionary<CircuitSession, string>(SessionIdentityComparer())

        let dynamicAdmissions = ConcurrentDictionary<string, byte>(StringComparer.Ordinal)

        let mutable runFailure =
            initialFailure
            |> ValueOption.map (SchedulerFailure.restamp runId circuit.Id.Value)

        let mutable approvalCount = resumeState.ApprovalRoundCount
        let mutable dynamicCount = resumeState.GeneratedNodeCount
        let mutable terminalWritten = 0
        let mutable background = Task.CompletedTask
        let mutable sessionAgentId: string option = None

        let scopedSourcePath path id lane =
            let stagePath = path + "/" + id

            match lane.Key, lane.Ordinal with
            | ValueNone, ValueNone -> stagePath
            | _ ->
                let parent = laneToken lane

                let identity =
                    hash $"{parent.Length}:{parent}" |> fun value -> value.Substring(0, 24)

                $"{stagePath}{{parent={identity}}}"

        let writeEvent item =
            events.Writer.WriteAsync(item, linkedCts.Token).AsTask()

        // Deltas are observational and may be dropped under pressure. Structural protocol
        // events always use writeEvent and therefore apply bounded backpressure.
        let writeDelta item =
            events.Writer.TryWrite(item) |> ignore
            Task.CompletedTask

        let observe observation =
            task {
                try
                    do! executor.ObserveAsync(observation, options, linkedCts.Token)
                with _ ->
                    ()
            }

        let setRunFailure failure =
            lock stateGate (fun () ->
                if runFailure.IsNone then
                    runFailure <- ValueSome failure
                    linkedCts.Cancel())

        let requestApproval (request: ApprovalRequest) =
            task {
                let accepted =
                    lock stateGate (fun () ->
                        match resumeState.AcceptedApprovals.TryGetValue request.RequestId with
                        | true, decision ->
                            ValueSome(ApprovalResponse(request.RequestId, decision.Approved, decision.Note))
                        | _ -> ValueNone)

                match accepted with
                | ValueSome response -> return response
                | ValueNone ->
                    let resumedPending, eventRequest =
                        lock stateGate (fun () ->
                            let pending = resumeState.PendingApprovalIds.Contains request.RequestId

                            let durableRequest =
                                match resumeState.PendingApprovalRequests.TryGetValue request.RequestId with
                                | true, saved -> saved
                                | _ -> request

                            pending, durableRequest)

                    if not resumedPending then
                        let round = Interlocked.Increment(&approvalCount)

                        if round > options.MaxApprovalRounds then
                            Interlocked.Decrement(&approvalCount) |> ignore
                            raise (InvalidOperationException("The approval-round limit was exceeded."))

                        lock stateGate (fun () ->
                            resumeState.ApprovalRoundCount <- round
                            resumeState.PendingApprovalIds.Add request.RequestId |> ignore
                            resumeState.PendingApprovalRequests[request.RequestId] <- request)

                    let completion =
                        TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously)

                    if not (pendingApprovals.TryAdd(request.RequestId, completion)) then
                        raise (InvalidOperationException("A duplicate approval request was generated."))

                    try
                        do! writeEvent (ApprovalRequested eventRequest)
                        do! observe (CircuitApprovalRequested(runId, eventRequest))
                        return! completion.Task.WaitAsync(linkedCts.Token)
                    finally
                        pendingApprovals.TryRemove request.RequestId |> ignore
            }

        let store (path: string) (lane: Lane) (outputType: Type) (response: ErasedResponse) =
            let key = journalKey path lane

            try
                let stored =
                    match response.Failure with
                    | ValueNone ->
                        { Success = true
                          ValueJson = JsonSerializer.Serialize(response.Value, outputType)
                          FailureCode = 0
                          FailureMessage = null
                          FailureOperationId = null
                          FailureRequestId = null
                          InputTokens = response.Metadata.Usage.InputTokens
                          OutputTokens = response.Metadata.Usage.OutputTokens
                          Attempt = response.Metadata.Attempt
                          StartedAt = response.Metadata.StartedAt
                          CompletedAt = response.Metadata.CompletedAt
                          IdempotencyKey = response.Metadata.IdempotencyKey
                          SourceOrder = response.Metadata.SourceOrder |> Seq.toArray }
                    | ValueSome failure ->
                        { Success = false
                          ValueJson = null
                          FailureCode = int failure.Code
                          FailureMessage = failure.Message
                          FailureOperationId = failure.OperationId |> ValueOption.toObj
                          FailureRequestId = failure.RequestId |> ValueOption.toObj
                          InputTokens = response.Metadata.Usage.InputTokens
                          OutputTokens = response.Metadata.Usage.OutputTokens
                          Attempt = response.Metadata.Attempt
                          StartedAt = response.Metadata.StartedAt
                          CompletedAt = response.Metadata.CompletedAt
                          IdempotencyKey = response.Metadata.IdempotencyKey
                          SourceOrder = response.Metadata.SourceOrder |> Seq.toArray }

                lock stateGate (fun () ->
                    resumeState.Responses[key] <- stored
                    usageEntries[key] <- response.Metadata.Usage)
            with ex ->
                lock stateGate (fun () -> resumeState.SnapshotFailures.Add ex)

        let restore (path: string) (lane: Lane) (outputType: Type) =
            let key = journalKey path lane

            lock stateGate (fun () ->
                match resumeState.Responses.TryGetValue key with
                | true, stored ->
                    // Historical response usage remains attached to replay metadata, but it is
                    // not charged again to this new execution attempt.
                    let usage = RunUsage(stored.InputTokens, stored.OutputTokens)

                    let restoredSession =
                        match resumeState.Sessions.TryGetValue key with
                        | true, session -> ValueSome session
                        | _ -> ValueNone

                    let metadata =
                        ResponseMetadata(
                            lane.Key,
                            lane.Ordinal,
                            stored.SourceOrder,
                            runId,
                            path,
                            usage,
                            restoredSession,
                            stored.Attempt,
                            stored.StartedAt,
                            stored.CompletedAt,
                            stored.IdempotencyKey
                        )

                    if stored.Success then
                        let value = JsonSerializer.Deserialize(stored.ValueJson, outputType)
                        ValueSome(erase outputType value ValueNone metadata)
                    else
                        let failure =
                            CircuitFailure(
                                enum<CircuitFailureCode> stored.FailureCode,
                                stored.FailureMessage,
                                ValueSome runId,
                                (if isNull stored.FailureOperationId then
                                     ValueSome path
                                 else
                                     ValueSome stored.FailureOperationId),
                                (if isNull stored.FailureRequestId then
                                     ValueNone
                                 else
                                     ValueSome stored.FailureRequestId),
                                ValueNone
                            )

                        ValueSome(erase outputType null (ValueSome failure) metadata)
                | _ -> ValueNone)

        let finishNode node path lane started value failure usage session =
            task {
                let completed = DateTimeOffset.UtcNow

                let metadata =
                    createMetadata runId lineageId path lane options started completed usage session

                let response = erase (outputType node) value failure metadata
                store path lane (outputType node) response
                let info = NodeInfo(nodeId node, path, lane.Key, 1, completed)
                do! writeEvent (NodeCompleted(info, UntypedResponse(failure.IsNone, failure, metadata)))
                do! observe (CircuitNodeCompleted(runId, info, failure))
                return response
            }

        let startNode node path lane =
            let timestamp = DateTimeOffset.UtcNow

            task {
                let info = NodeInfo(nodeId node, path, lane.Key, 1, timestamp)
                do! writeEvent (NodeStarted info)
                do! observe (CircuitNodeStarted(runId, info))
                return timestamp
            }

        let rec eval
            (node: Node)
            (path: string)
            (lane: Lane)
            (inputValue: obj)
            (dynamicDepth: int)
            (emit: ErasedResponse -> Task)
            : Task =
            linkedCts.Token.ThrowIfCancellationRequested()

            match node with
            | Then(previous, next) ->
                task {
                    do!
                        eval previous (path + "/previous") lane inputValue dynamicDepth (fun response ->
                            match response.Failure with
                            | ValueSome failure ->
                                let propagated = erase (outputType next) null (ValueSome failure) response.Metadata
                                emit propagated
                            | ValueNone ->
                                let responseLane =
                                    laneFromMetadata
                                        response.Metadata.ItemKey
                                        response.Metadata.SourceOrdinal
                                        response.Metadata.SourceOrder

                                eval next (path + "/next") responseLane response.Value dynamicDepth emit)
                }
            | Dynamic(id, _, maximum, handler, previous) ->
                task {
                    use semaphore = new SemaphoreSlim(maximum, maximum)
                    let work = ResizeArray<Task>()

                    do!
                        eval previous (path + "/previous") lane inputValue dynamicDepth (fun response ->
                            task {
                                match response.Failure with
                                | ValueSome failure ->
                                    do! emit (erase handler.OutputType null (ValueSome failure) response.Metadata)
                                | ValueNone ->
                                    do! semaphore.WaitAsync(linkedCts.Token)

                                    let responseLane =
                                        laneFromMetadata
                                            response.Metadata.ItemKey
                                            response.Metadata.SourceOrdinal
                                            response.Metadata.SourceOrder

                                    let childWork =
                                        task {
                                            try
                                                try
                                                    let dynamicKey = handler.Key(response.Value)
                                                    let upstreamKey = laneToken responseLane

                                                    let childIdentity =
                                                        hash
                                                            $"{upstreamKey.Length}:{upstreamKey}|{dynamicKey.Length}:{dynamicKey}"
                                                        |> fun value -> value.Substring(0, 32)

                                                    let childPath = $"{path}/{id}[{childIdentity}]"

                                                    if not (dynamicAdmissions.TryAdd(childPath, 0uy)) then
                                                        raise (
                                                            InvalidOperationException(
                                                                $"Dynamic node '{id}' produced a duplicate child identity for item '{upstreamKey}' and key '{dynamicKey}'."
                                                            )
                                                        )

                                                    if dynamicDepth + 1 > options.MaxDynamicDepth then
                                                        raise (
                                                            ResourceLimitExceeded
                                                                "The generated-graph depth limit was exceeded."
                                                        )

                                                    let child, fingerprint = handler.Build(response.Value)
                                                    validateGeneratedNode child
                                                    let generatedNodes = nodeCount child

                                                    let dynamicInput =
                                                        try
                                                            ValueSome(
                                                                JsonSerializer.Serialize(
                                                                    response.Value,
                                                                    handler.InputType
                                                                )
                                                            )
                                                        with ex ->
                                                            lock stateGate (fun () ->
                                                                resumeState.SnapshotFailures.Add ex)

                                                            ValueNone

                                                    lock stateGate (fun () ->
                                                        match dynamicInput with
                                                        | ValueSome snapshot ->
                                                            resumeState.DynamicInputs[childPath] <- snapshot
                                                        | ValueNone -> ()

                                                        match
                                                            resumeState.DynamicFingerprints.TryGetValue childPath
                                                        with
                                                        | true, expected when
                                                            not (StringComparer.Ordinal.Equals(expected, fingerprint))
                                                            ->
                                                            raise (CheckpointFingerprintMismatch childPath)
                                                        | false, _ ->
                                                            dynamicCount <- dynamicCount + generatedNodes

                                                            if dynamicCount > options.MaxDynamicNodes then
                                                                raise (
                                                                    ResourceLimitExceeded
                                                                        "The generated-node limit was exceeded."
                                                                )

                                                            resumeState.DynamicFingerprints[childPath] <- fingerprint
                                                            resumeState.GeneratedNodeCount <- dynamicCount
                                                        | _ -> ())

                                                    do!
                                                        eval
                                                            child
                                                            childPath
                                                            responseLane
                                                            response.Value
                                                            (dynamicDepth + 1)
                                                            emit
                                                with
                                                | ResourceLimitExceeded message ->
                                                    setRunFailure (
                                                        SchedulerFailure.create
                                                            CircuitFailureCode.ResourceLimit
                                                            runId
                                                            (ValueSome path)
                                                            ValueNone
                                                            message
                                                            ValueNone
                                                    )
                                                | CheckpointFingerprintMismatch childPath ->
                                                    setRunFailure (
                                                        SchedulerFailure.mismatch
                                                            runId
                                                            $"Generated Circuit fingerprint mismatch at '{childPath}'."
                                                    )
                                                | ex ->
                                                    setRunFailure (
                                                        SchedulerFailure.generated
                                                            runId
                                                            path
                                                            "A dynamic Circuit could not be built and validated."
                                                            (ValueSome ex)
                                                    )
                                            finally
                                                semaphore.Release() |> ignore
                                        }

                                    lock work (fun () -> work.Add childWork)
                                    do! childWork
                            })

                    do! Task.WhenAll(work.ToArray())
                }
            | Attempt(id, previous) ->
                task {
                    do!
                        eval previous (path + "/previous") lane inputValue dynamicDepth (fun response ->
                            task {
                                let started = DateTimeOffset.UtcNow
                                let attemptPath = path + "/" + id

                                let responseLane =
                                    laneFromMetadata
                                        response.Metadata.ItemKey
                                        response.Metadata.SourceOrdinal
                                        response.Metadata.SourceOrder

                                let! wrapped =
                                    finishNode
                                        node
                                        attemptPath
                                        responseLane
                                        started
                                        response.Typed
                                        ValueNone
                                        (RunUsage(0, 0))
                                        ValueNone

                                do! emit wrapped
                            })
                }
            | Recover(id, _, handler, previous) ->
                eval previous (path + "/previous") lane inputValue dynamicDepth (fun response ->
                    match planRecovery response with
                    | PassThroughRecovery successful -> emit successful
                    | InvokeRecovery failure ->
                        let recoveryPath = path + "/" + id

                        let responseLane =
                            laneFromMetadata
                                response.Metadata.ItemKey
                                response.Metadata.SourceOrdinal
                                response.Metadata.SourceOrder

                        match restore recoveryPath responseLane handler.OutputType with
                        | ValueSome cached -> emit cached
                        | ValueNone ->
                            continueTask (startNode node recoveryPath responseLane) (fun started ->
                                let value, recoveryFailure =
                                    match invokeRecoveryHandler handler.Invoke failure with
                                    | HandlerExecutionSucceeded(value, _) -> value, ValueNone
                                    | HandlerExecutionFailed ex ->
                                        null,
                                        ValueSome(
                                            SchedulerFailure.engine
                                                runId
                                                recoveryPath
                                                "A recovery handler failed."
                                                (ValueSome ex)
                                        )

                                let completed =
                                    finishNode
                                        node
                                        recoveryPath
                                        responseLane
                                        started
                                        value
                                        recoveryFailure
                                        (RunUsage(0, 0))
                                        ValueNone

                                continueTask completed emit))
            | Aggregate(id, _, handler, previous) ->
                let captured = ResizeArray<obj>()
                let aggregatePath = path + "/" + id

                let capture response =
                    lock captured (fun () -> captured.Add response.Typed)
                    Task.CompletedTask

                let executeAggregate () =
                    match restore aggregatePath lane handler.OutputType with
                    | ValueSome cached -> emit cached
                    | ValueNone ->
                        continueTask (startNode node aggregatePath lane) (fun started ->
                            let metadata =
                                createMetadata
                                    runId
                                    lineageId
                                    aggregatePath
                                    lane
                                    options
                                    started
                                    started
                                    (RunUsage(0, 0))
                                    ValueNone

                            let context =
                                CircuitContext(
                                    runId,
                                    aggregatePath,
                                    lane.Key,
                                    metadata.IdempotencyKey,
                                    options,
                                    linkedCts.Token
                                )

                            let invocation =
                                invokeAggregateHandler handler context (captured.ToArray()) linkedCts.Token

                            continueTask invocation (fun handlerResult ->
                                let value, failure =
                                    match handlerResult with
                                    | HandlerExecutionSucceeded(value, failure) -> value, failure
                                    | HandlerExecutionFailed ex ->
                                        null,
                                        ValueSome(
                                            SchedulerFailure.engine
                                                runId
                                                aggregatePath
                                                "The aggregate handler failed."
                                                (ValueSome ex)
                                        )

                                let completed =
                                    finishNode
                                        node
                                        aggregatePath
                                        lane
                                        started
                                        value
                                        failure
                                        (RunUsage(0, 0))
                                        ValueNone

                                continueTask completed emit))

                continueUnitTask
                    (eval previous (path + "/previous") lane inputValue dynamicDepth capture)
                    executeAggregate
            | Items(id, _, handler) ->
                task {
                    let stagePath = path + "/" + id
                    let sourcePath = scopedSourcePath path id lane
                    let stageSemaphore = admissionSemaphore stagePath

                    let cachedSnapshots =
                        lock stateGate (fun () ->
                            match resumeState.SourceSnapshots.TryGetValue sourcePath with
                            | true, value -> ValueSome value
                            | _ -> ValueNone)

                    let values =
                        match cachedSnapshots with
                        | ValueSome snapshots ->
                            snapshots
                            |> Array.map (fun json -> JsonSerializer.Deserialize(json, handler.ItemType))
                        | ValueNone ->
                            let materialized = handler.Invoke inputValue

                            try
                                let snapshots =
                                    materialized
                                    |> Array.map (fun value -> JsonSerializer.Serialize(value, handler.ItemType))

                                lock stateGate (fun () -> resumeState.SourceSnapshots[sourcePath] <- snapshots)
                            with ex ->
                                lock stateGate (fun () -> resumeState.SnapshotFailures.Add ex)

                            materialized

                    lock stateGate (fun () -> resumeState.SourceCounts[sourcePath] <- int64 values.Length)
                    let keys = HashSet<string>(StringComparer.Ordinal)
                    let tasks = ResizeArray<Task>()

                    let runItem (itemValue: obj) (key: string) (itemLane: Lane) =
                        task {
                            try
                                let itemPath = $"{sourcePath}[{key}]"

                                let response =
                                    match restore itemPath itemLane handler.ItemType with
                                    | ValueSome cached -> Task.FromResult cached
                                    | ValueNone ->
                                        task {
                                            let! started = startNode node itemPath itemLane

                                            return!
                                                finishNode
                                                    node
                                                    itemPath
                                                    itemLane
                                                    started
                                                    itemValue
                                                    ValueNone
                                                    (RunUsage(0, 0))
                                                    ValueNone
                                        }

                                let! response = response
                                do! emit response
                            finally
                                stageSemaphore.Release() |> ignore
                        }

                    for ordinal in 0 .. values.Length - 1 do
                        let itemValue = values[ordinal]
                        let key = handler.Key(itemValue, int64 ordinal)

                        if not (keys.Add key) then
                            setRunFailure (
                                SchedulerFailure.create
                                    CircuitFailureCode.DuplicateItemKey
                                    runId
                                    (ValueSome sourcePath)
                                    ValueNone
                                    $"Finite source '{id}' produced duplicate item key '{key}'."
                                    ValueNone
                            )

                        let itemLane = childLane lane key (int64 ordinal)

                        do! stageSemaphore.WaitAsync(linkedCts.Token)
                        tasks.Add(runItem itemValue key itemLane)

                    do! Task.WhenAll(tasks.ToArray())
                }
            | AsyncSource(id, _, handler) ->
                let stagePath = path + "/" + id
                let sourcePath = scopedSourcePath path id lane
                let stageSemaphore = admissionSemaphore stagePath
                let enumerator = handler.GetAsyncEnumerator(inputValue, linkedCts.Token)
                let tasks = ResizeArray<Task>()

                let runItem value itemLane itemOrdinal =
                    task {
                        try
                            let itemPath = $"{sourcePath}[{itemOrdinal}]"
                            let! started = startNode node itemPath itemLane

                            let! response =
                                finishNode node itemPath itemLane started value ValueNone (RunUsage(0, 0)) ValueNone

                            do! emit response
                        finally
                            stageSemaphore.Release() |> ignore
                    }

                let rec pull ordinal =
                    task {
                        // Reserve stage capacity before pulling so async sources cannot run
                        // ahead of admitted lanes.
                        do! stageSemaphore.WaitAsync(linkedCts.Token)
                        let! hasValue = enumerator.MoveNextAsync()

                        if hasValue then
                            let value = enumerator.Current
                            let itemLane = childLane lane (string ordinal) ordinal
                            tasks.Add(runItem value itemLane ordinal)
                            return! pull (ordinal + 1L)
                        else
                            stageSemaphore.Release() |> ignore
                            do! Task.WhenAll(tasks.ToArray())
                    }

                runWithAsyncDisposal (fun () -> pull 0L :> Task) enumerator.DisposeAsync
            | ResumableSource(id, _, handler) ->
                task {
                    let stagePath = path + "/" + id
                    let sourcePath = scopedSourcePath path id lane
                    let stageSemaphore = admissionSemaphore stagePath

                    let mutable cursor =
                        lock stateGate (fun () ->
                            match resumeState.SourceCursors.TryGetValue sourcePath with
                            | true, value -> ValueSome value
                            | _ -> ValueNone)

                    let mutable committed =
                        lock stateGate (fun () ->
                            match resumeState.SourceCounts.TryGetValue sourcePath with
                            | true, value -> value
                            | _ -> 0L)

                    let mutable completed = false

                    let mutable pageCount =
                        lock stateGate (fun () ->
                            match resumeState.SourcePageCounts.TryGetValue sourcePath with
                            | true, value -> value
                            | _ -> 0)

                    let processValues (firstOrdinal: int64) (values: obj array) =
                        task {
                            let tasks = ResizeArray<Task>()

                            for index in 0 .. values.Length - 1 do
                                do! stageSemaphore.WaitAsync(linkedCts.Token)
                                let ordinal = firstOrdinal + int64 index
                                let value = values[index]
                                let itemLane = childLane lane (string ordinal) ordinal

                                let work =
                                    task {
                                        try
                                            let itemPath = $"{sourcePath}[{ordinal}]"

                                            let response =
                                                match restore itemPath itemLane handler.ItemType with
                                                | ValueSome cached -> Task.FromResult cached
                                                | ValueNone ->
                                                    task {
                                                        let! started = startNode node itemPath itemLane

                                                        return!
                                                            finishNode
                                                                node
                                                                itemPath
                                                                itemLane
                                                                started
                                                                value
                                                                ValueNone
                                                                (RunUsage(0, 0))
                                                                ValueNone
                                                    }

                                            let! response = response
                                            do! emit response
                                        finally
                                            stageSemaphore.Release() |> ignore
                                    }

                                tasks.Add work

                            do! Task.WhenAll(tasks.ToArray())
                        }

                    // Finish an admitted page captured by a checkpoint before asking the external
                    // source for that page again.
                    let pendingSnapshot =
                        lock stateGate (fun () ->
                            match resumeState.SourceSnapshots.TryGetValue sourcePath with
                            | true, snapshots when int64 snapshots.Length > committed ->
                                snapshots[int committed ..]
                                |> Array.map (fun json -> JsonSerializer.Deserialize(json, handler.ItemType))
                                |> ValueSome
                            | _ -> ValueNone)

                    match pendingSnapshot with
                    | ValueSome values ->
                        do! processValues committed values
                        committed <- committed + int64 values.Length

                        lock stateGate (fun () ->
                            resumeState.SourceCounts[sourcePath] <- committed

                            match resumeState.SourcePendingCursors.TryGetValue sourcePath with
                            | true, pendingCursor when not (isNull pendingCursor) ->
                                cursor <- ValueSome pendingCursor
                                resumeState.SourceCursors[sourcePath] <- pendingCursor
                            | _ ->
                                cursor <- ValueNone
                                resumeState.SourceCursors.Remove sourcePath |> ignore

                            completed <- resumeState.SourcePendingCompleted.Contains sourcePath
                            resumeState.SourcePendingCursors.Remove sourcePath |> ignore
                            resumeState.SourcePendingCompleted.Remove sourcePath |> ignore)
                    | ValueNone -> ()

                    while not completed do
                        if pageCount >= options.MaxSourcePages then
                            raise (
                                ResourceLimitExceeded(
                                    $"Resumable source '{id}' exceeded the page limit of {options.MaxSourcePages}."
                                )
                            )

                        let! values, nextCursor, pageCompleted = handler.ReadAsync(inputValue, cursor, linkedCts.Token)
                        pageCount <- pageCount + 1
                        lock stateGate (fun () -> resumeState.SourcePageCounts[sourcePath] <- pageCount)

                        let cursorProgressed =
                            match cursor, nextCursor with
                            | ValueSome current, ValueSome next -> not (StringComparer.Ordinal.Equals(current, next))
                            | ValueNone, ValueNone -> false
                            | _ -> true

                        if not pageCompleted && not cursorProgressed then
                            raise (
                                ResourceLimitExceeded(
                                    $"Resumable source '{id}' returned a non-completed page without advancing its continuation token."
                                )
                            )

                        if values.Length > options.MaxSourcePageSize then
                            let failure =
                                SchedulerFailure.create
                                    CircuitFailureCode.ResourceLimit
                                    runId
                                    (ValueSome sourcePath)
                                    ValueNone
                                    $"Resumable source '{id}' returned {values.Length} items, exceeding the page limit of {options.MaxSourcePageSize}."
                                    ValueNone

                            setRunFailure failure
                            completed <- true
                        else
                            let snapshots =
                                values
                                |> Array.map (fun value -> JsonSerializer.Serialize(value, handler.ItemType))

                            lock stateGate (fun () ->
                                let existing =
                                    match resumeState.SourceSnapshots.TryGetValue sourcePath with
                                    | true, prior -> prior
                                    | _ -> Array.empty

                                resumeState.SourceSnapshots[sourcePath] <- Array.append existing snapshots
                                resumeState.SourcePendingCursors[sourcePath] <- nextCursor |> ValueOption.toObj

                                if pageCompleted then
                                    resumeState.SourcePendingCompleted.Add sourcePath |> ignore
                                else
                                    resumeState.SourcePendingCompleted.Remove sourcePath |> ignore)

                            do! processValues committed values
                            committed <- committed + int64 values.Length
                            cursor <- nextCursor
                            completed <- pageCompleted

                            lock stateGate (fun () ->
                                resumeState.SourceCounts[sourcePath] <- committed

                                match cursor with
                                | ValueSome value -> resumeState.SourceCursors[sourcePath] <- value
                                | ValueNone -> resumeState.SourceCursors.Remove sourcePath |> ignore

                                resumeState.SourcePendingCursors.Remove sourcePath |> ignore
                                resumeState.SourcePendingCompleted.Remove sourcePath |> ignore)
                }
            | (Branch(id, _, handler) as branchNode) ->
                evalBranch branchNode id handler path lane inputValue dynamicDepth emit
            | Merge(id, _, maximum, branches) ->
                task {
                    use semaphore = new SemaphoreSlim(maximum, maximum)

                    let tasks =
                        branches
                        |> Array.mapi (fun index child ->
                            task {
                                do! semaphore.WaitAsync(linkedCts.Token)

                                try
                                    do! eval child ($"{path}/{id}/branch-{index}") lane inputValue dynamicDepth emit
                                finally
                                    semaphore.Release() |> ignore
                            })

                    let! _ = Task.WhenAll tasks
                    ()
                }
            | Loop(id, _, maximum, handler) ->
                let loopPath = path + "/" + id

                let rec iterate value iteration =
                    task {
                        if iteration >= maximum then
                            if handler.Continue value then
                                return EmitLoopResourceLimit
                            else
                                return EmitLoopSuccess value
                        elif not (handler.Continue value) then
                            return EmitLoopSuccess value
                        else
                            let captured = ResizeArray<ErasedResponse>()

                            do!
                                eval
                                    handler.Body
                                    ($"{loopPath}/iteration-{iteration}")
                                    lane
                                    value
                                    dynamicDepth
                                    (fun item -> task { captured.Add item })

                            match classifyLoopBody captured with
                            | PropagateLoopFailure response -> return EmitLoopFailure response
                            | ContinueLoop response -> return! iterate response.Value (iteration + 1)
                    }

                let finishLoop value failure =
                    continueTask (startNode node loopPath lane) (fun started ->
                        let completed =
                            finishNode node loopPath lane started value failure (RunUsage(0, 0)) ValueNone

                        continueTask completed emit)

                match restore loopPath lane handler.ValueType with
                | ValueSome cached -> emit cached
                | ValueNone ->
                    task {
                        let! terminal = iterate inputValue 0

                        match terminal with
                        | EmitLoopResourceLimit ->
                            let failure =
                                SchedulerFailure.create
                                    CircuitFailureCode.ResourceLimit
                                    runId
                                    (ValueSome loopPath)
                                    ValueNone
                                    $"Loop '{id}' exhausted its maximum of {maximum} iterations."
                                    ValueNone

                            do! finishLoop null (ValueSome failure)
                        | EmitLoopSuccess value -> do! finishLoop value ValueNone
                        | EmitLoopFailure response -> do! finishLoop null response.Failure
                    }
            | Approval(id, _, handler) ->
                let approvalPath = path + "/" + id

                match restore approvalPath lane typeof<ApprovalResponse> with
                | ValueSome cached -> emit cached
                | ValueNone ->
                    task {
                        let! started = startNode node approvalPath lane
                        let prompt = handler.Invoke inputValue

                        let requestId =
                            hash $"{lineageId}|{approvalPath}|{laneToken lane}"
                            |> fun value -> value.Substring(0, 24)

                        let request =
                            ApprovalRequest(requestId, prompt.Title, ValueSome prompt.Message, ValueSome prompt)

                        let! approval = requestApprovalHandledAsync requestApproval request

                        let value, failure =
                            match approval with
                            | ApprovalAccepted response -> box response, ValueNone
                            | ApprovalLimitReached message ->
                                null,
                                ValueSome(
                                    SchedulerFailure.create
                                        CircuitFailureCode.ResourceLimit
                                        runId
                                        (ValueSome approvalPath)
                                        ValueNone
                                        message
                                        ValueNone
                                )

                        let! response =
                            finishNode node approvalPath lane started value failure (RunUsage(0, 0)) ValueNone

                        do! emit response
                    }
            | Named(id, child) -> task { do! eval child (path + "/" + id) lane inputValue dynamicDepth emit }
            | Agent(id, _, handler) ->
                let nodePath = path + "/" + id
                let sessionKey = journalKey nodePath lane

                let effectiveSession =
                    lock stateGate (fun () ->
                        let restored =
                            match resumeState.Sessions.TryGetValue sessionKey with
                            | true, session -> ValueSome session
                            | _ -> ValueNone

                        selectEffectiveSession restored options.Session)

                let sessionGroupKey =
                    match effectiveSession with
                    | ValueSome session -> sessionGroupKeys.GetOrAdd(session, fun _ -> sessionKey)
                    | ValueNone -> sessionKey

                if effectiveSession.IsSome then
                    let currentAgent = handler.Agent.Id.Value + "@" + handler.Agent.Version.ToString()

                    lock stateGate (fun () ->
                        match planSessionAgentAdmission sessionAgentId currentAgent with
                        | RegisterSessionAgent -> sessionAgentId <- Some currentAgent
                        | ReuseSessionAgent -> ()
                        | RejectSessionAgentSharing _ ->
                            raise (
                                InvalidOperationException(
                                    "One Circuit session cannot be shared by different agent definitions in the same run."
                                )
                            )

                        resumeState.Sessions[sessionKey] <- effectiveSession.Value
                        resumeState.SessionAgents[sessionKey] <- handler.Agent
                        resumeState.SessionAliases[sessionKey] <- sessionGroupKey)

                let agentOptions =
                    match effectiveSession with
                    | ValueSome session -> options.WithSession session
                    | ValueNone -> options

                let sessionPermit =
                    effectiveSession
                    |> ValueOption.map (fun session ->
                        sessionSemaphores.GetOrAdd(session, fun _ -> new SemaphoreSlim(1, 1)))

                let publishSession (session: CircuitSession) =
                    task {
                        let! adapterState =
                            executor.SerializeSessionAsync(handler.Agent, session, options, linkedCts.Token).AsTask()

                        sessionGroupKeys[session] <- sessionGroupKey

                        lock stateGate (fun () ->
                            resumeState.Sessions[sessionKey] <- session
                            resumeState.SessionAgents[sessionKey] <- handler.Agent
                            resumeState.SessionAliases[sessionKey] <- sessionGroupKey

                            resumeState.SerializedSessions[sessionGroupKey] <-
                                struct (handler.Agent.Id.Value + "@" + handler.Agent.Version.ToString(),
                                        adapterState.Clone()))
                    }

                let publishOptional session =
                    match session with
                    | ValueSome value -> publishSession value :> Task
                    | ValueNone -> Task.CompletedTask

                let mutable leafApprovalOrdinal = 0

                let approveLeaf (request: ApprovalRequest) =
                    task {
                        let ordinal = Interlocked.Increment(&leafApprovalOrdinal)

                        let publicId =
                            hash $"{lineageId}|{nodePath}|{laneToken lane}|approval|{ordinal}|{request.ToolName}"
                            |> fun value -> value.Substring(0, 24)

                        let publicRequest =
                            ApprovalRequest(publicId, request.ToolName, request.ArgumentsJson, request.Prompt)

                        let! response = requestApproval publicRequest
                        return ApprovalResponse(request.RequestId, response.Approved, response.Note)
                    }

                let executeAgent () =
                    continueTask (startNode node nodePath lane) (fun started ->
                        let metadata =
                            createMetadata
                                runId
                                lineageId
                                nodePath
                                lane
                                options
                                started
                                started
                                (RunUsage(0, 0))
                                ValueNone

                        continueUnitTask (publishOptional effectiveSession) (fun () ->
                            let invocation =
                                invokeAgent
                                    executor
                                    runId
                                    nodePath
                                    handler
                                    inputValue
                                    agentOptions
                                    metadata.IdempotencyKey
                                    (fun delta ->
                                        writeDelta (
                                            OutputDelta(
                                                CircuitOutputDelta(nodePath, lane.Key, delta, DateTimeOffset.UtcNow)
                                            )
                                        ))
                                    approveLeaf
                                    (fun session -> publishSession session :> Task)
                                    linkedCts.Token

                            continueTask invocation (fun (value, failure, usage, session) ->
                                let correlatedFailure =
                                    failure |> ValueOption.map (SchedulerFailure.restamp runId nodePath)

                                continueUnitTask (publishOptional session) (fun () ->
                                    let completed =
                                        finishNode node nodePath lane started value correlatedFailure usage session

                                    continueTask completed emit))))

                let executeOrReplay () : Task =
                    match restore nodePath lane handler.OutputType with
                    | ValueSome cached -> emit cached
                    | ValueNone -> executeAgent ()

                withOptionalPermitAsync sessionPermit linkedCts.Token executeOrReplay
            | Code(id, _, handler) ->
                let nodePath = path + "/" + id

                match restore nodePath lane handler.OutputType with
                | ValueSome cached -> emit cached
                | ValueNone ->
                    continueTask (startNode node nodePath lane) (fun started ->
                        let metadata =
                            createMetadata
                                runId
                                lineageId
                                nodePath
                                lane
                                options
                                started
                                started
                                (RunUsage(0, 0))
                                ValueNone

                        let context =
                            CircuitContext(
                                runId,
                                nodePath,
                                lane.Key,
                                metadata.IdempotencyKey,
                                options,
                                linkedCts.Token
                            )

                        continueTask (invokeCodeHandler handler context inputValue) (fun handlerResult ->
                            let value, failure =
                                match handlerResult with
                                | HandlerExecutionSucceeded(value, failure) -> value, failure
                                | HandlerExecutionFailed ex ->
                                    let failure =
                                        if linkedCts.IsCancellationRequested then
                                            SchedulerFailure.cancelled runId nodePath
                                        else
                                            SchedulerFailure.engine
                                                runId
                                                nodePath
                                                $"Code node '{id}' failed."
                                                (ValueSome ex)

                                    null, ValueSome failure

                            let completed =
                                finishNode node nodePath lane started value failure (RunUsage(0, 0)) ValueNone

                            continueTask completed emit))
            | Value(id, outputType, serializedValue) ->
                let nodePath = path + "/" + id

                match restore nodePath lane outputType with
                | ValueSome cached -> emit cached
                | ValueNone ->
                    continueTask (startNode node nodePath lane) (fun started ->
                        let value = JsonSerializer.Deserialize(serializedValue, outputType)

                        let completed =
                            finishNode node nodePath lane started value ValueNone (RunUsage(0, 0)) ValueNone

                        continueTask completed emit)

        and evalBranch branchNode id handler path lane inputValue dynamicDepth emit =
            let selectedPath = path + "/" + id

            match selectBranch handler inputValue |> planBranchExecution runId selectedPath with
            | EvaluateBranch(child, childPath) -> eval child childPath lane inputValue dynamicDepth emit
            | EmitBranchFailure failure ->
                continueTask (startNode branchNode selectedPath lane) (fun started ->
                    let completed =
                        finishNode
                            branchNode
                            selectedPath
                            lane
                            started
                            null
                            (ValueSome failure)
                            (RunUsage(0, 0))
                            ValueNone

                    continueTask completed emit)

        let writeTerminalIgnoringFailure terminal =
            task {
                try
                    do! events.Writer.WriteAsync(terminal).AsTask()
                with _ ->
                    ()
            }

        let installTerminal terminal =
            if linkedCts.IsCancellationRequested then
                // Disposal must still leave one observable typed terminal even when the
                // bounded buffer is full and no reader has consumed it. Structural writes
                // have already been cancelled, so replace unread pre-terminal events until
                // the reserved terminal can be installed.
                let mutable dropped = Unchecked.defaultof<CircuitEvent<'Output>>
                let mutable written = events.Writer.TryWrite terminal

                while not written && events.Reader.TryRead(&dropped) do
                    written <- events.Writer.TryWrite terminal

                if not written then
                    written <- events.Writer.TryWrite terminal

                if written then
                    Task.CompletedTask
                else
                    writeTerminalIgnoringFailure terminal
            else
                writeTerminalIgnoringFailure terminal

        let writeTerminal () =
            task {
                if Interlocked.CompareExchange(&terminalWritten, 1, 0) = 0 then
                    let completed = DateTimeOffset.UtcNow

                    let usage, failure =
                        lock stateGate (fun () ->
                            RunUsage(
                                usageEntries.Values |> Seq.sumBy _.InputTokens,
                                usageEntries.Values |> Seq.sumBy _.OutputTokens
                            ),
                            runFailure)

                    let metadata =
                        createMetadata
                            runId
                            lineageId
                            circuit.Id.Value
                            rootLane
                            options
                            startedAt
                            completed
                            usage
                            ValueNone

                    let outcome =
                        match failure with
                        | ValueSome failure -> Failed failure
                        | ValueNone ->
                            let total, succeeded =
                                lock outputs (fun () -> outputs.Count, outputs |> Seq.filter _.IsSuccess |> Seq.length)

                            match planRunOutcome ValueNone total succeeded with
                            | EmitFailedRun failure -> Failed failure
                            | EmitSucceededRun(total, successful) ->
                                Succeeded(
                                    RunSummary(total, successful, total - successful, usage, startedAt, completed)
                                )

                    let response = Response<RunSummary>.Create(outcome, metadata)

                    let terminalFailure =
                        match outcome with
                        | Failed failure -> ValueSome failure
                        | Succeeded _ -> ValueNone

                    let terminal = RunCompleted response
                    do! installTerminal terminal
                    events.Writer.TryComplete() |> ignore

                    // External observers cannot control terminal publication or channel closure.
                    // observe isolates both synchronous and asynchronous observer failures, and
                    // this terminal notification is deliberately fire-and-forget so disposal stays bounded.
                    observe (CircuitRunCompleted(runId, terminalFailure, usage, startedAt, completed))
                    |> ignore
            }

        member _.Start() =
            background <-
                task {
                    try
                        let runInfo =
                            RunInfo(runId, lineageId, circuit.Id, circuit.Version, circuit.Fingerprint, startedAt)

                        do! writeEvent (RunStarted runInfo)
                        do! observe (CircuitRunStarted runInfo)

                        match initialFailure with
                        | ValueSome _ -> ()
                        | ValueNone ->
                            let issues = CircuitDefinition.validate circuit

                            let staticAgents =
                                agentIds circuit.Node |> Seq.distinct |> Seq.truncate 2 |> Seq.toArray

                            if options.Session.IsSome && staticAgents.Length > 1 then
                                setRunFailure (
                                    SchedulerFailure.generated
                                        runId
                                        circuit.Id.Value
                                        "A continued Circuit session cannot be shared by multiple agent definitions."
                                        ValueNone
                                )
                            elif issues.Count > 0 then
                                setRunFailure (
                                    SchedulerFailure.generated
                                        runId
                                        circuit.Id.Value
                                        $"Circuit validation failed: {issues[0].Code}: {issues[0].Message}"
                                        ValueNone
                                )
                            else
                                let initialLane = rootLane

                                do!
                                    eval circuit.Node circuit.Id.Value initialLane (box input) 0 (fun erased ->
                                        task {
                                            let response =
                                                match erased.Failure with
                                                | ValueSome failure ->
                                                    Response<'Output>.Create(Failed failure, erased.Metadata)
                                                | ValueNone ->
                                                    Response<'Output>
                                                        .Create(
                                                            Succeeded(unbox<'Output> erased.Value),
                                                            erased.Metadata
                                                        )

                                            lock outputs (fun () -> outputs.Add response)
                                            do! writeEvent (OutputProduced(erased.Metadata.ItemKey, response))
                                        })
                    with
                    | :? OperationCanceledException ->
                        if runFailure.IsNone then
                            runFailure <- ValueSome(SchedulerFailure.cancelled runId circuit.Id.Value)
                    | ResourceLimitExceeded message ->
                        if runFailure.IsNone then
                            runFailure <-
                                ValueSome(
                                    SchedulerFailure.create
                                        CircuitFailureCode.ResourceLimit
                                        runId
                                        ValueNone
                                        ValueNone
                                        message
                                        ValueNone
                                )
                    | ex ->
                        if runFailure.IsNone then
                            runFailure <-
                                ValueSome(
                                    SchedulerFailure.engine
                                        runId
                                        circuit.Id.Value
                                        "The Circuit scheduler failed."
                                        (ValueSome ex)
                                )

                    do! writeTerminal ()
                }

            CircuitRun<'Output>(
                runId,
                events.Reader.ReadAllAsync(),
                (fun (response, cancellationToken) ->
                    ValueTask<Response<unit>>(
                        task {
                            let now = DateTimeOffset.UtcNow

                            let metadata =
                                createMetadata
                                    runId
                                    lineageId
                                    circuit.Id.Value
                                    rootLane
                                    options
                                    now
                                    now
                                    (RunUsage(0, 0))
                                    ValueNone

                            if isNull response then
                                return
                                    Response<unit>
                                        .Create(
                                            Failed(
                                                SchedulerFailure.approval
                                                    runId
                                                    "<null>"
                                                    "The approval response cannot be null."
                                            ),
                                            metadata
                                        )
                            else
                                match pendingApprovals.TryGetValue response.RequestId with
                                | true, completion ->
                                    let accepted =
                                        lock stateGate (fun () ->
                                            if resumeState.AcceptedApprovals.ContainsKey response.RequestId then
                                                false
                                            else
                                                resumeState.AcceptedApprovals[response.RequestId] <-
                                                    { Approved = response.Approved
                                                      Note = response.Note }

                                                resumeState.PendingApprovalIds.Remove response.RequestId |> ignore

                                                resumeState.PendingApprovalRequests.Remove response.RequestId
                                                |> ignore

                                                usedApprovals.TryAdd(response.RequestId, 0uy) |> ignore
                                                true)

                                    if accepted && completion.TrySetResult response then
                                        return Response<unit>.Create(Succeeded(), metadata)
                                    else
                                        return
                                            Response<unit>
                                                .Create(
                                                    Failed(
                                                        SchedulerFailure.approval
                                                            runId
                                                            response.RequestId
                                                            "The approval response is unknown, mismatched, or already used."
                                                    ),
                                                    metadata
                                                )
                                | _ ->
                                    return
                                        Response<unit>
                                            .Create(
                                                Failed(
                                                    SchedulerFailure.approval
                                                        runId
                                                        response.RequestId
                                                        "The approval response is unknown, mismatched, or already used."
                                                ),
                                                metadata
                                            )
                        }
                    )),
                (fun cancellationToken ->
                    ValueTask<Response<CircuitCheckpoint<'Output>>>(
                        task {
                            let now = DateTimeOffset.UtcNow

                            let metadata =
                                createMetadata
                                    runId
                                    lineageId
                                    circuit.Id.Value
                                    rootLane
                                    options
                                    now
                                    now
                                    (RunUsage(0, 0))
                                    ValueNone

                            if circuit.Checkpointability = CircuitCheckpointability.NotCheckpointable then
                                return
                                    Response<CircuitCheckpoint<'Output>>
                                        .Create(
                                            Failed(
                                                SchedulerFailure.checkpoint
                                                    runId
                                                    "This Circuit contains a non-resumable asynchronous source."
                                                    ValueNone
                                            ),
                                            metadata
                                        )
                            elif lock stateGate (fun () -> resumeState.SnapshotFailures.Count > 0) then
                                let failure = lock stateGate (fun () -> resumeState.SnapshotFailures[0])

                                return
                                    Response<CircuitCheckpoint<'Output>>
                                        .Create(
                                            Failed(
                                                SchedulerFailure.checkpoint
                                                    runId
                                                    "An admitted item could not be encoded by the checkpoint codec."
                                                    (ValueSome failure)
                                            ),
                                            metadata
                                        )
                            else
                                try
                                    // Adapter codecs run only at quiescent leaf boundaries through
                                    // publishSession. Checkpoint creation therefore never races a
                                    // codec against provider mutation; the state lock selects one
                                    // atomic journal/session/approval/admission cut from those snapshots.
                                    let payload =
                                        lock stateGate (fun () ->
                                            writeResumeState resumeState typeof<'Input> (box input) options)

                                    let serializedLength = Text.Encoding.UTF8.GetByteCount(payload.GetRawText())

                                    if serializedLength > options.MaxCheckpointBytes then
                                        return
                                            Response<CircuitCheckpoint<'Output>>
                                                .Create(
                                                    Failed(
                                                        SchedulerFailure.create
                                                            CircuitFailureCode.ResourceLimit
                                                            runId
                                                            ValueNone
                                                            ValueNone
                                                            "The checkpoint size limit was exceeded."
                                                            ValueNone
                                                    ),
                                                    metadata
                                                )
                                    else
                                        return
                                            Response<CircuitCheckpoint<'Output>>
                                                .Create(
                                                    Succeeded(
                                                        CircuitCheckpoint<'Output>(
                                                            circuit.Id,
                                                            circuit.Version,
                                                            circuit.Fingerprint,
                                                            lineageId,
                                                            now,
                                                            payload,
                                                            ValueSome options
                                                        )
                                                    ),
                                                    metadata
                                                )
                                with ex ->
                                    return
                                        Response<CircuitCheckpoint<'Output>>
                                            .Create(
                                                Failed(
                                                    SchedulerFailure.checkpoint
                                                        runId
                                                        "The active Circuit state could not be encoded by the checkpoint codec."
                                                        (ValueSome ex)
                                                ),
                                                metadata
                                            )
                        }
                    )),
                (fun () ->
                    lock stateGate (fun () ->
                        if runFailure.IsNone then
                            runFailure <- ValueSome(SchedulerFailure.cancelled runId circuit.Id.Value))

                    linkedCts.Cancel()

                    ValueTask(
                        task {
                            let mutable drained = false

                            try
                                if options.DisposalDrainTimeout = Timeout.InfiniteTimeSpan then
                                    do! background
                                else
                                    do! background.WaitAsync(options.DisposalDrainTimeout)

                                drained <- true
                            with
                            | :? TimeoutException -> ()
                            | _ when background.IsCompleted -> drained <- true
                            | _ -> ()

                            if not drained && not background.IsCompleted then
                                // Cancellation-ignoring provider/host work must not control protocol
                                // completion. Publish the typed terminal directly, replacing unread
                                // buffered events when necessary, before closing the channel.
                                do! writeTerminal ()

                            let disposePrimitives () =
                                for semaphore in admissionSemaphores.Values do
                                    semaphore.Dispose()

                                for semaphore in sessionSemaphores.Values do
                                    semaphore.Dispose()

                                linkedCts.Dispose()

                            if drained || background.IsCompleted then
                                disposePrimitives ()
                            else
                                background.ContinueWith(
                                    (fun (_: Task) -> disposePrimitives ()),
                                    CancellationToken.None,
                                    TaskContinuationOptions.ExecuteSynchronously,
                                    TaskScheduler.Default
                                )
                                |> ignore
                        }
                    ))
            )

/// Base class used by provider adapters so Core remains the sole graph scheduler.
[<AbstractClass>]
type CircuitRuntime() =
    /// Executes one typed provider-agent leaf for the Core scheduler.
    abstract ExecuteAgentAsync<'Input, 'Output> :
        runId: RunId *
        nodePath: string *
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        idempotencyKey: string *
        onDelta: (string -> Task) *
        onApproval: (ApprovalRequest -> Task<ApprovalResponse>) *
        onSession: (CircuitSession -> Task) *
        cancellationToken: CancellationToken ->
            Task<RunResult<'Output>>

    /// Serializes one provider session using the active run's process-local context.
    abstract SerializeSessionCoreAsync:
        AgentDefinition * CircuitSession * RunOptions * CancellationToken -> ValueTask<JsonElement>

    /// Restores one provider session using rebound run options and services.
    abstract DeserializeSessionCoreAsync:
        AgentDefinition * JsonElement * RunOptions * CancellationToken -> ValueTask<CircuitSession>

    /// Observes an internal unified Circuit protocol event.
    abstract ObserveCircuitAsync: obj * RunOptions * CancellationToken -> Task
    default _.ObserveCircuitAsync(_observation, _options, _cancellationToken) = Task.CompletedTask

    interface IAgentLeafExecutor with
        member this.ExecuteAsync
            (
                runId,
                nodePath,
                agent,
                signature,
                input,
                options,
                idempotencyKey,
                onDelta,
                onApproval,
                onSession,
                cancellationToken
            ) =
            this.ExecuteAgentAsync(
                runId,
                nodePath,
                agent,
                signature,
                input,
                options,
                idempotencyKey,
                onDelta,
                onApproval,
                onSession,
                cancellationToken
            )

        member this.SerializeSessionAsync(agent, session, options, cancellationToken) =
            this.SerializeSessionCoreAsync(agent, session, options, cancellationToken)

        member this.DeserializeSessionAsync(agent, state, options, cancellationToken) =
            this.DeserializeSessionCoreAsync(agent, state, options, cancellationToken)

        member this.ObserveAsync(observation, options, cancellationToken) =
            this.ObserveCircuitAsync(box observation, options, cancellationToken)

    interface ICircuitRuntime with
        member this.StartAsync(circuit, input, options, cancellationToken) =
            if isNull (box circuit) then
                nullArg "circuit"

            if isNull (box options) then
                nullArg "options"

            let scheduler =
                SchedulerInternals.SchedulerRun<'Input, 'Output>(
                    this :> IAgentLeafExecutor,
                    circuit,
                    input,
                    options,
                    cancellationToken,
                    Guid.NewGuid().ToString("N"),
                    SchedulerInternals.emptyResumeState (),
                    ValueNone
                )

            Task.FromResult(scheduler.Start())

        member this.ResumeAsync<'Input, 'Output>(circuit, checkpoint, resumeOptions, cancellationToken) =
            task {
                if isNull (box circuit) then
                    nullArg "circuit"

                if isNull (box checkpoint) then
                    nullArg "checkpoint"

                if isNull (box resumeOptions) then
                    nullArg "resumeOptions"

                let mutable failure =
                    if
                        circuit.Id <> checkpoint.DefinitionId
                        || circuit.Version <> checkpoint.DefinitionVersion
                        || not (StringComparer.Ordinal.Equals(circuit.Fingerprint, checkpoint.Fingerprint))
                    then
                        ValueSome(
                            SchedulerFailure.mismatch
                                (RunId.New())
                                "The checkpoint does not match the exact Circuit definition and fingerprint."
                        )
                    else
                        ValueNone

                let mutable input = Unchecked.defaultof<'Input>
                let mutable state = SchedulerInternals.emptyResumeState ()

                let mutable restoredOptions =
                    match checkpoint.Options with
                    | ValueSome saved ->
                        RunOptions(
                            saved.Session,
                            saved.TenantId,
                            saved.UserId,
                            saved.Tags,
                            saved.StructuredOutputPolicy,
                            saved.SensitiveDataMode,
                            resumeOptions.Services,
                            saved.MaxConcurrency,
                            saved.EventBufferCapacity,
                            saved.MaxDynamicDepth,
                            saved.MaxDynamicNodes,
                            saved.MaxApprovalRounds,
                            saved.MaxSourcePageSize,
                            saved.MaxSourcePages,
                            saved.MaxCheckpointBytes,
                            saved.DisposalDrainTimeout
                        )
                    | ValueNone -> RunOptions.Default

                if failure.IsNone then
                    try
                        let payload = checkpoint.Payload
                        let encodedType = payload.GetProperty("inputType").GetString()
                        let expectedType = typeof<'Input>.AssemblyQualifiedName

                        if not (StringComparer.Ordinal.Equals(encodedType, expectedType)) then
                            raise (JsonException("The checkpoint input type does not match the Circuit input type."))

                        input <- payload.GetProperty("input").Deserialize<'Input>()
                        state <- SchedulerInternals.parseResumeState payload

                        if checkpoint.Options.IsNone then
                            restoredOptions <- SchedulerInternals.parseRunOptions payload resumeOptions.Services

                        let agents =
                            SchedulerInternals.validateDynamicResume circuit.Node circuit.Id.Value state restoredOptions

                        let restoreSteps = SchedulerInternals.planSerializedSessionRestores agents state

                        let! restoredGroups =
                            SchedulerInternals.restoreSerializedSessionGroupsAsync
                                restoreSteps
                                (fun agent adapterState ->
                                    this
                                        .DeserializeSessionCoreAsync(
                                            agent,
                                            adapterState,
                                            restoredOptions,
                                            cancellationToken
                                        )
                                        .AsTask())

                        SchedulerInternals.bindSerializedSessionAliases agents state restoredGroups
                    with
                    | SchedulerInternals.ResourceLimitExceeded message ->
                        failure <-
                            ValueSome(
                                SchedulerFailure.create
                                    CircuitFailureCode.ResourceLimit
                                    (RunId.New())
                                    ValueNone
                                    ValueNone
                                    message
                                    ValueNone
                            )
                    | SchedulerInternals.CheckpointFingerprintMismatch path ->
                        failure <-
                            ValueSome(
                                SchedulerFailure.mismatch
                                    (RunId.New())
                                    $"Generated Circuit fingerprint mismatch at '{path}'."
                            )
                    | ex ->
                        failure <-
                            ValueSome(
                                SchedulerFailure.create
                                    CircuitFailureCode.CheckpointMismatch
                                    (RunId.New())
                                    ValueNone
                                    ValueNone
                                    "The checkpoint execution state is malformed and was rejected before resume work began."
                                    (ValueSome ex)
                            )

                let scheduler =
                    SchedulerInternals.SchedulerRun<'Input, 'Output>(
                        this :> IAgentLeafExecutor,
                        circuit,
                        input,
                        restoredOptions,
                        cancellationToken,
                        checkpoint.LineageId,
                        state,
                        failure
                    )

                return scheduler.Start()
            }

        member this.SerializeSessionAsync(agent, session, cancellationToken) =
            this.SerializeSessionCoreAsync(agent, session, RunOptions.Default, cancellationToken)

        member this.DeserializeSessionAsync(agent, state, cancellationToken) =
            this.DeserializeSessionCoreAsync(agent, state, RunOptions.Default, cancellationToken)
