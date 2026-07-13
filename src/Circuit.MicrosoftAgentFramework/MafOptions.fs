namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions

[<Sealed>]
type internal ResolvedMafTool(tool: Circuit.Core.ResolvedTool, modelName: string, mafFunction: AIFunction) =
    member _.Tool = tool
    member _.ModelName = modelName
    member _.MafFunction = mafFunction
    member _.Tags = tool.Tags

    member _.RequiresApproval =
        tool.Approval = ApprovalMode.Always || tool.Approval = ApprovalMode.ByPolicy

/// Describes the context for a named tool-approval policy decision.
/// <remarks>
/// Tool arguments may contain sensitive or high-impact payloads. Approval policies should treat them as untrusted input.
/// </remarks>
[<Sealed>]
type ToolApprovalContext
    internal (runContext: RunContext, tool: Circuit.Core.ResolvedTool, arguments: IReadOnlyDictionary<string, obj>) =
    let toolView = Circuit.ResolvedTool.FromCore tool

    /// Gets the owning run context.
    member _.RunContext = runContext

    /// Gets the candidate tool.
    member _.Tool = toolView

    /// Gets the decoded tool arguments.
    member _.Arguments = arguments

/// Decides whether a tool configured with <see cref="F:Circuit.Core.ApprovalMode.ByPolicy" /> may run without pausing for human approval.
type IToolApprovalPolicy =
    /// Evaluates a named approval policy.
    /// <returns><see langword="true" /> to allow the tool call immediately; otherwise <see langword="false" />.</returns>
    abstract IsApprovedAsync: policyName: string * context: ToolApprovalContext -> ValueTask<bool>

/// Identifies an adapter observer event without exposing the internal leaf execution protocol.
type MafObservedEventKind =
    /// The observed run started.
    | RunStarted = 0
    /// Provider text was emitted.
    | OutputDelta = 1
    /// A tool invocation started.
    | ToolStarted = 2
    /// A tool invocation completed.
    | ToolCompleted = 3
    /// Human approval was requested.
    | ApprovalRequested = 4
    /// An observed step started.
    | StepStarted = 5
    /// An observed step completed.
    | StepCompleted = 6
    /// An intermediate value was observed.
    | IntermediateOutput = 7
    /// The observed run completed successfully.
    | RunCompleted = 8
    /// The observed run completed with failure.
    | RunFailed = 9

/// Represents one observed runtime event surfaced to <see cref="T:Circuit.MicrosoftAgentFramework.IRunObserver" /> implementations.
[<Sealed>]
type MafObservedEvent
    internal
    (
        runId: RunId,
        timestamp: DateTimeOffset,
        kind: RunEventKind,
        operationId: string voption,
        textDelta: string voption,
        failure: CircuitFailure voption,
        approval: ApprovalRequest voption
    ) =
    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the event timestamp in UTC.
    member _.Timestamp = timestamp

    /// Gets the adapter observer event kind.
    member _.Kind = enum<MafObservedEventKind> (int kind)

    /// Gets the associated operation identifier when one exists.
    member _.OperationId = operationId

    /// Gets the streamed text delta when the event carries one.
    member _.TextDelta = textDelta

    /// Gets the failure payload for failed events.
    member _.Failure = failure

    /// Gets the approval payload for approval-requested events.
    member _.Approval = approval

/// Represents the final observation delivered to run observers.
[<Sealed>]
type MafRunObservation
    internal
    (
        context: RunContext,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset,
        repaired: bool,
        usage: RunUsage,
        session: CircuitSession voption,
        failure: CircuitFailure voption,
        diagnosticMetadata: IReadOnlyDictionary<string, string>
    ) =
    /// Gets the owning run context.
    member _.Context = context

    /// Gets the run start time in UTC.
    member _.StartedAt = startedAt

    /// Gets the run completion time in UTC.
    member _.CompletedAt = completedAt

    /// Gets whether a secondary structured-output repair pass was used.
    member _.Repaired = repaired

    /// Gets provider-reported token usage.
    member _.Usage = usage

    /// Gets the resulting session when one was produced.
    member _.Session = session

    /// Gets the terminal failure when the run did not succeed.
    member _.Failure = failure

    /// Gets implementation-defined diagnostics such as provider request identifiers.
    member _.DiagnosticMetadata = diagnosticMetadata

/// Observes run lifecycle events emitted by the Microsoft Agent Framework adapter.
type IRunObserver =
    /// Called after a run is created but before any streaming events are dispatched.
    abstract OnRunStartedAsync:
        context: RunContext * startedAt: DateTimeOffset * cancellationToken: CancellationToken -> ValueTask

    /// Called for each observed event during the run.
    abstract OnRunEventAsync: event: MafObservedEvent * cancellationToken: CancellationToken -> ValueTask

    /// Called exactly once when the run completes or fails.
    abstract OnRunCompletedAsync: observation: MafRunObservation * cancellationToken: CancellationToken -> ValueTask

module internal MafObserver =
    [<Literal>]
    let private LoggerCategoryName = "Circuit.MicrosoftAgentFramework.RunObservers"

    let private ObserverDispatchFailedEventId =
        EventId(17001, "RunObserverDispatchFailed")

    let private emptyDiagnosticMetadata =
        Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

    type internal ObserverRunInfo =
        { RunId: string
          DefinitionId: string
          DefinitionVersion: string
          AgentName: string
          RequestModel: string voption
          RootOperationName: string }

    type internal ObserverSession(observers: Circuit.IRunObserver[], logger: ILogger, runInfo: ObserverRunInfo) =
        let createEnvelope
            kind
            timestamp
            operationId
            operationName
            operationKind
            textDelta
            prompt
            input
            output
            toolArguments
            failure
            approval
            startedAt
            completedAt
            repaired
            usage
            session
            diagnosticMetadata
            =
            Circuit.RunEventEnvelope.Create(
                runInfo.RunId,
                timestamp,
                enum<Circuit.AgentRunEventKind> (int kind),
                runInfo.DefinitionId,
                runInfo.DefinitionVersion,
                runInfo.AgentName,
                operationId,
                operationName,
                operationKind,
                (runInfo.RequestModel |> ValueOption.toObj),
                (textDelta |> ValueOption.toObj),
                (prompt |> ValueOption.toObj),
                (input |> ValueOption.toObj),
                (output |> ValueOption.toObj),
                (toolArguments |> ValueOption.toObj),
                (failure |> ValueOption.map Circuit.AgentFailure.FromCore |> ValueOption.toObj),
                (approval
                 |> ValueOption.map Circuit.ApprovalRequest.FromCore
                 |> ValueOption.toObj),
                (match startedAt with
                 | ValueSome value -> Nullable value
                 | ValueNone -> Nullable()),
                (match completedAt with
                 | ValueSome value -> Nullable value
                 | ValueNone -> Nullable()),
                repaired,
                (usage |> ValueOption.map Circuit.RunUsage.FromCore |> ValueOption.toObj),
                (session |> ValueOption.map Circuit.CircuitSession.FromCore |> ValueOption.toObj),
                diagnosticMetadata
            )

        member _.RunId = runInfo.RunId
        member _.RootOperationName = runInfo.RootOperationName

        member private _.DispatchAsync(envelope: Circuit.RunEventEnvelope, cancellationToken: CancellationToken) =
            task {
                for observer in observers do
                    try
                        do! observer.OnEventAsync(envelope, cancellationToken).AsTask()
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
                    | ex ->
                        logger.LogWarning(
                            ObserverDispatchFailedEventId,
                            ex,
                            $"Run observer '{observer.GetType().FullName}' failed for run '{runInfo.RunId}' while processing '{envelope.Kind}'."
                        )
            }

        member this.NotifyAsync
            kind
            operationId
            operationName
            operationKind
            textDelta
            prompt
            input
            output
            toolArguments
            failure
            approval
            startedAt
            completedAt
            repaired
            usage
            session
            diagnosticMetadata
            cancellationToken
            =
            this.DispatchAsync(
                createEnvelope
                    kind
                    DateTimeOffset.UtcNow
                    operationId
                    operationName
                    operationKind
                    textDelta
                    prompt
                    input
                    output
                    toolArguments
                    failure
                    approval
                    startedAt
                    completedAt
                    repaired
                    usage
                    session
                    diagnosticMetadata,
                cancellationToken
            )

    let private activeSessions = ConcurrentDictionary<string, ObserverSession>()

    let private createLogger (services: IServiceProvider) =
        if isNull services then
            NullLoggerFactory.Instance.CreateLogger(LoggerCategoryName)
        else
            match services.GetService(typeof<ILoggerFactory>) with
            | :? ILoggerFactory as loggerFactory -> loggerFactory.CreateLogger(LoggerCategoryName)
            | _ -> NullLoggerFactory.Instance.CreateLogger(LoggerCategoryName)

    let private snapshotObservers (observers: IReadOnlyList<Circuit.IRunObserver>) =
        if isNull observers || observers.Count = 0 then
            Array.empty<Circuit.IRunObserver>
        else
            observers |> Seq.toArray

    let private tryRegisterSession observers runInfo services =
        let snapshot = snapshotObservers observers

        if snapshot.Length = 0 then
            ValueNone
        else
            let session = ObserverSession(snapshot, createLogger services, runInfo)
            activeSessions[runInfo.RunId] <- session
            ValueSome session

    let createAgentRunSession
        (observers: IReadOnlyList<Circuit.IRunObserver>)
        (runId: RunId)
        (agentName: string)
        (definitionId: DefinitionId)
        (definitionVersion: SemanticVersion)
        (requestModel: string voption)
        (services: IServiceProvider)
        =
        // A scheduler-owned Circuit session already publishes root and node events for
        // agent leaves. Do not replace it with a second leaf-local root session.
        match activeSessions.TryGetValue(runId.Value) with
        | true, existing when existing.RootOperationName = "circuit.run" -> ValueNone
        | _ ->
            tryRegisterSession
                observers
                { RunId = runId.Value
                  DefinitionId = definitionId.Value
                  DefinitionVersion = definitionVersion.ToString()
                  AgentName = agentName
                  RequestModel = requestModel
                  RootOperationName = "agent.run" }
                services

    let createCircuitRunSession
        (observers: IReadOnlyList<Circuit.IRunObserver>)
        (runId: RunId)
        (definitionId: DefinitionId)
        (definitionVersion: SemanticVersion)
        (services: IServiceProvider)
        =
        tryRegisterSession
            observers
            { RunId = runId.Value
              DefinitionId = definitionId.Value
              DefinitionVersion = definitionVersion.ToString()
              AgentName = definitionId.Value
              RequestModel = ValueNone
              RootOperationName = "circuit.run" }
            services

    let unregisterSession (session: ObserverSession voption) =
        match session with
        | ValueSome value -> activeSessions.TryRemove(value.RunId) |> ignore
        | ValueNone -> ()

    let tryGetSession (runId: RunId) =
        match activeSessions.TryGetValue(runId.Value) with
        | true, session -> ValueSome session
        | _ -> ValueNone

    let notifyStartedAsync
        (session: ObserverSession voption)
        (startedAt: DateTimeOffset)
        (prompt: string voption)
        (input: string voption)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.RunStarted
                        value.RunId
                        value.RootOperationName
                        Circuit.RunOperationKind.Run
                        ValueNone
                        prompt
                        input
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        (ValueSome startedAt)
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyRootEventAsync
        (session: ObserverSession voption)
        (kind: RunEventKind)
        (textDelta: string voption)
        (output: string voption)
        (failure: CircuitFailure voption)
        (approval: ApprovalRequest voption)
        (startedAt: DateTimeOffset voption)
        (completedAt: DateTimeOffset voption)
        (repaired: bool)
        (usage: RunUsage voption)
        (resultSession: CircuitSession voption)
        (diagnosticMetadata: IReadOnlyDictionary<string, string>)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        kind
                        value.RunId
                        value.RootOperationName
                        Circuit.RunOperationKind.Run
                        textDelta
                        ValueNone
                        ValueNone
                        output
                        ValueNone
                        failure
                        approval
                        startedAt
                        completedAt
                        repaired
                        usage
                        resultSession
                        diagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyCircuitRootEventAsync
        (session: ObserverSession voption)
        (kind: RunEventKind)
        (output: string voption)
        (failure: CircuitFailure voption)
        (startedAt: DateTimeOffset voption)
        (completedAt: DateTimeOffset voption)
        (usage: RunUsage voption)
        (diagnosticMetadata: IReadOnlyDictionary<string, string>)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        kind
                        value.RunId
                        value.RootOperationName
                        Circuit.RunOperationKind.Run
                        ValueNone
                        ValueNone
                        ValueNone
                        output
                        ValueNone
                        failure
                        ValueNone
                        startedAt
                        completedAt
                        false
                        usage
                        ValueNone
                        diagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyApprovalRequestedAsync
        (session: ObserverSession voption)
        (operationId: string)
        (toolName: string)
        (approval: ApprovalRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.ApprovalRequested
                        operationId
                        toolName
                        Circuit.RunOperationKind.Approval
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        (ValueSome approval)
                        ValueNone
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyToolStartedAsync
        (runId: RunId)
        (operationId: string)
        (toolName: string)
        (toolArguments: string voption)
        (cancellationToken: CancellationToken)
        =
        task {
            match tryGetSession runId with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.ToolStarted
                        operationId
                        toolName
                        Circuit.RunOperationKind.Tool
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        toolArguments
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyToolCompletedAsync
        (runId: RunId)
        (operationId: string)
        (toolName: string)
        (failure: CircuitFailure voption)
        (cancellationToken: CancellationToken)
        =
        task {
            match tryGetSession runId with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.ToolCompleted
                        operationId
                        toolName
                        Circuit.RunOperationKind.Tool
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        failure
                        ValueNone
                        ValueNone
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyNodeStartedAsync
        (session: ObserverSession voption)
        (stepId: string)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.StepStarted
                        stepId
                        stepId
                        Circuit.RunOperationKind.Node
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

    let notifyNodeCompletedAsync
        (session: ObserverSession voption)
        (stepId: string)
        (failure: CircuitFailure voption)
        (cancellationToken: CancellationToken)
        =
        task {
            match session with
            | ValueSome value ->
                do!
                    value.NotifyAsync
                        RunEventKind.StepCompleted
                        stepId
                        stepId
                        Circuit.RunOperationKind.Node
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        ValueNone
                        failure
                        ValueNone
                        ValueNone
                        ValueNone
                        false
                        ValueNone
                        ValueNone
                        emptyDiagnosticMetadata
                        cancellationToken
            | ValueNone -> ()
        }

/// Configures <see cref="T:Circuit.MicrosoftAgentFramework.MafRuntime" /> behavior.
/// <remarks>
/// Options remain mutable until the runtime snapshots them. Skill resolvers and script runners can execute local code and file-backed skills, so they must be treated as trusted extensions.
/// </remarks>
[<Sealed>]
type MafRuntimeOptions() =
    let createDefaultSerializerOptions () =
        let options = CircuitJson.createOptions ()
        options.MakeReadOnly()
        options

    let snapshotJsonOptions (options: JsonSerializerOptions) =
        if isNull options then
            null
        else
            let snapshot = JsonSerializerOptions(options)
            snapshot.MakeReadOnly()
            snapshot

    let snapshotResolvers (resolvers: IReadOnlyList<'T>) =
        if isNull resolvers then
            null
        else
            resolvers |> Seq.toArray :> IReadOnlyList<'T>

    let emptyToolResolvers =
        Array.empty<Circuit.Core.IToolResolver> :> IReadOnlyList<Circuit.Core.IToolResolver>

    let emptySkillResolvers =
        Array.empty<Circuit.Core.ISkillResolver> :> IReadOnlyList<Circuit.Core.ISkillResolver>

    let emptyObservers =
        Array.empty<Circuit.IRunObserver> :> IReadOnlyList<Circuit.IRunObserver>

    let mutable isFrozen = false
    let mutable defaultModelId: string voption = ValueNone
    let mutable jsonSerializerOptions = createDefaultSerializerOptions ()
    let mutable secondaryStructuredOutputClient: IChatClient voption = ValueNone

    let mutable toolResolvers: IReadOnlyList<Circuit.Core.IToolResolver> =
        emptyToolResolvers

    let mutable toolApprovalPolicy: IToolApprovalPolicy voption = ValueNone

    let mutable skillResolvers: IReadOnlyList<Circuit.Core.ISkillResolver> =
        emptySkillResolvers

    let mutable skillScriptRunner: Circuit.Core.ISkillScriptRunner voption = ValueNone
    let mutable observers: IReadOnlyList<Circuit.IRunObserver> = emptyObservers

    let throwIfFrozen () =
        if isFrozen then
            invalidOp "Options are frozen."

    /// Gets or sets the default provider model identifier used when an agent does not supply its own model hint.
    member _.DefaultModelId
        with get () = defaultModelId
        and set value =
            throwIfFrozen ()
            defaultModelId <- value

    /// Gets or sets the JSON serializer options used for contract serialization, validation, sessions, approvals, and checkpoints.
    member _.JsonSerializerOptions
        with get () = jsonSerializerOptions
        and set value =
            throwIfFrozen ()
            jsonSerializerOptions <- value

    /// Gets or sets the secondary chat client used for structured-output repair when allowed.
    member _.SecondaryStructuredOutputClient
        with get () = secondaryStructuredOutputClient
        and set value =
            throwIfFrozen ()
            secondaryStructuredOutputClient <- value

    /// Gets or sets the tool resolvers consulted for each run.
    member _.ToolResolvers
        with get () = toolResolvers
        and set value =
            throwIfFrozen ()
            toolResolvers <- value

    /// Gets or sets the named tool-approval policy implementation.
    member _.ToolApprovalPolicy
        with get () = toolApprovalPolicy
        and set value =
            throwIfFrozen ()
            toolApprovalPolicy <- value

    /// Gets or sets the skill resolvers consulted for each run.
    member _.SkillResolvers
        with get () = skillResolvers
        and set value =
            throwIfFrozen ()
            skillResolvers <- value

    /// Gets or sets the component that executes resolved skill scripts.
    /// <remarks>When unset, file skill scripts are unavailable even if the skill root exposes them.</remarks>
    member _.SkillScriptRunner
        with get () = skillScriptRunner
        and set value =
            throwIfFrozen ()
            skillScriptRunner <- value

    /// Gets or sets the run observers that receive lifecycle callbacks.
    member _.Observers
        with get () = observers
        and set value =
            throwIfFrozen ()
            observers <- value

    member internal this.Freeze() =
        if not isFrozen then
            jsonSerializerOptions <- snapshotJsonOptions jsonSerializerOptions
            toolResolvers <- snapshotResolvers toolResolvers
            skillResolvers <- snapshotResolvers skillResolvers
            observers <- snapshotResolvers observers
            isFrozen <- true

    member internal this.Snapshot() =
        let snapshot = MafRuntimeOptions()
        snapshot.DefaultModelId <- defaultModelId
        snapshot.JsonSerializerOptions <- snapshotJsonOptions jsonSerializerOptions
        snapshot.SecondaryStructuredOutputClient <- secondaryStructuredOutputClient
        snapshot.ToolResolvers <- snapshotResolvers toolResolvers
        snapshot.ToolApprovalPolicy <- toolApprovalPolicy
        snapshot.SkillResolvers <- snapshotResolvers skillResolvers
        snapshot.SkillScriptRunner <- skillScriptRunner
        snapshot.Observers <- snapshotResolvers observers
        snapshot.Freeze()
        snapshot
