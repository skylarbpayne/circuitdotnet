module internal Circuit.MicrosoftAgentFramework.MafWorkflows

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI.Workflows
open Microsoft.Agents.AI.Workflows.Checkpointing
open Microsoft.Agents.AI.Workflows.InProc

let private workflowStreamBufferCapacity = 64

let private createBoundedChannel<'T> () =
    let options = BoundedChannelOptions(workflowStreamBufferCapacity)
    options.AllowSynchronousContinuations <- false
    options.FullMode <- BoundedChannelFullMode.Wait
    options.SingleReader <- true
    options.SingleWriter <- true
    Channel.CreateBounded<'T>(options)

let private writeToChannelAsync<'T> (writer: ChannelWriter<'T>) (item: 'T) (cancellationToken: CancellationToken) =
    task {
        try
            do! writer.WriteAsync(item, cancellationToken).AsTask()
        with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
            ()
    }

[<Sealed>]
type private ChannelAsyncEnumerator<'T>
    (
        reader: ChannelReader<'T>,
        background: Task,
        abandonmentCts: CancellationTokenSource,
        watcherCts: CancellationTokenSource
    ) =
    let mutable current = Unchecked.defaultof<'T>

    interface IAsyncEnumerator<'T> with
        member _.Current = current

        member _.MoveNextAsync() =
            let rec moveNextAsync () =
                task {
                    let! canRead = reader.WaitToReadAsync().AsTask()

                    if not canRead then
                        do! background
                        return false
                    else
                        let mutable item = Unchecked.defaultof<'T>

                        if reader.TryRead(&item) then
                            current <- item
                            return true
                        else
                            return! moveNextAsync ()
                }

            ValueTask<bool>(moveNextAsync ())

        member _.DisposeAsync() =
            ValueTask(
                task {
                    abandonmentCts.Cancel()

                    try
                        do! background
                    with _ ->
                        ()

                    watcherCts.Dispose()
                    abandonmentCts.Dispose()
                }
            )

[<Sealed>]
type internal WorkflowStepFailureException(failure: CircuitFailure) =
    inherit InvalidOperationException(failure.Message, failure.Exception |> ValueOption.defaultValue null)

    member _.Failure = failure

[<AllowNullLiteral; Sealed>]
type internal ParallelAggregateState<'T>() =
    member val Received = Array.empty<bool> with get, set
    member val Values = Array.empty<'T> with get, set
    member val ReceivedCount = 0 with get, set

    [<JsonIgnore>]
    member val SyncRoot = obj () with get

type internal ParallelAggregateCapture<'T> =
    | Pending
    | Complete of 'T list
    | DuplicateBranch of int
    | InvalidBranchIndex of int
    | AlreadyCompleted

[<RequireQualifiedAccess>]
module internal ParallelAggregateState =
    let create<'T> branchCount =
        let state = ParallelAggregateState<'T>()
        state.Received <- Array.zeroCreate branchCount
        state.Values <- Array.zeroCreate branchCount
        state

    let private ensureShape branchCount (state: ParallelAggregateState<'T>) =
        if state.Received.Length = 0 && state.Values.Length = 0 && state.ReceivedCount = 0 then
            state.Received <- Array.zeroCreate branchCount
            state.Values <- Array.zeroCreate branchCount
        elif state.Received.Length <> branchCount || state.Values.Length <> branchCount then
            invalidOp "The workflow parallel aggregate state is malformed."
        elif state.ReceivedCount < 0 || state.ReceivedCount > branchCount then
            invalidOp "The workflow parallel aggregate state is malformed."

    let capture branchCount (value: WorkflowGraph.ParallelBranchResult<'T>) (state: ParallelAggregateState<'T>) =
        lock state.SyncRoot (fun () ->
            ensureShape branchCount state

            if state.ReceivedCount = branchCount then
                AlreadyCompleted
            elif value.BranchIndex < 0 || value.BranchIndex >= branchCount then
                InvalidBranchIndex value.BranchIndex
            elif state.Received[value.BranchIndex] then
                DuplicateBranch value.BranchIndex
            else
                state.Values[value.BranchIndex] <- value.Value
                state.Received[value.BranchIndex] <- true
                state.ReceivedCount <- state.ReceivedCount + 1

                if state.ReceivedCount = branchCount then
                    Complete([ for index in 0 .. branchCount - 1 -> state.Values[index] ])
                else
                    Pending)

[<AllowNullLiteral; Sealed>]
type internal LoopState() =
    member val Iteration = 0 with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveEnvelope<'Input, 'BranchOutput>() =
    member val Input = Unchecked.defaultof<'Input> with get, set
    member val Completed = Array.empty<WorkflowGraph.ParallelBranchResult<'BranchOutput>> with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveItem<'Input, 'BranchOutput>() =
    member val IsSeed = false with get, set
    member val Envelope = Unchecked.defaultof<ParallelWaveEnvelope<'Input, 'BranchOutput>> with get, set
    member val BranchIndex = -1 with get, set
    member val BranchValue = Unchecked.defaultof<'BranchOutput> with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveDispatch<'Input, 'BranchOutput>() =
    member val IsReady = false with get, set
    member val Envelope = Unchecked.defaultof<ParallelWaveEnvelope<'Input, 'BranchOutput>> with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveBranchDispatch<'Input>() =
    member val IsActive = false with get, set
    member val Input = Unchecked.defaultof<'Input> with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveBranchStartState() =
    member val Started = false with get, set

[<AllowNullLiteral; Sealed>]
type private ParallelWaveCollectorState<'Input, 'BranchOutput>() =
    member val HasSeed = false with get, set
    member val Seed = Unchecked.defaultof<ParallelWaveEnvelope<'Input, 'BranchOutput>> with get, set
    member val Received = Array.empty<bool> with get, set
    member val Values = Array.empty<'BranchOutput> with get, set
    member val ReceivedCount = 0 with get, set
    member val Completed = false with get, set

    [<JsonIgnore>]
    member val SyncRoot = obj () with get

type private ParallelWaveCapture<'Input, 'BranchOutput> =
    | Pending
    | Ready of ParallelWaveEnvelope<'Input, 'BranchOutput>
    | DuplicateSeed
    | DuplicateBranch of int
    | InvalidBranchIndex of int
    | AlreadyCompleted

[<RequireQualifiedAccess>]
module private ParallelWaveCollectorState =
    let create<'Input, 'BranchOutput> branchCount =
        let state = ParallelWaveCollectorState<'Input, 'BranchOutput>()
        state.Received <- Array.zeroCreate branchCount
        state.Values <- Array.zeroCreate branchCount
        state

    let private ensureShape branchCount (state: ParallelWaveCollectorState<'Input, 'BranchOutput>) =
        if state.Received.Length = 0 && state.Values.Length = 0 && state.ReceivedCount = 0 then
            state.Received <- Array.zeroCreate branchCount
            state.Values <- Array.zeroCreate branchCount
        elif state.Received.Length <> branchCount || state.Values.Length <> branchCount then
            invalidOp "The workflow parallel wave state is malformed."
        elif state.ReceivedCount < 0 || state.ReceivedCount > branchCount then
            invalidOp "The workflow parallel wave state is malformed."

    let private normalizeCompleted (envelope: ParallelWaveEnvelope<'Input, 'BranchOutput>) =
        let completed =
            if isNull (box envelope.Completed) then
                Array.empty
            else
                envelope.Completed

        envelope.Completed <- completed
        completed

    let private buildEnvelope
        (expectedBranchIndices: int[])
        (state: ParallelWaveCollectorState<'Input, 'BranchOutput>)
        =
        let completed = normalizeCompleted state.Seed

        let merged =
            Array.zeroCreate<WorkflowGraph.ParallelBranchResult<'BranchOutput>> (
                completed.Length + expectedBranchIndices.Length
            )

        Array.Copy(completed, merged, completed.Length)

        for offset in 0 .. expectedBranchIndices.Length - 1 do
            merged[completed.Length + offset] <-
                { BranchIndex = expectedBranchIndices[offset]
                  Value = state.Values[offset] }

        let envelope = ParallelWaveEnvelope<'Input, 'BranchOutput>()
        envelope.Input <- state.Seed.Input
        envelope.Completed <- merged
        envelope

    let private validateSeed (expectedBranchIndices: int[]) (envelope: ParallelWaveEnvelope<'Input, 'BranchOutput>) =
        if isNull envelope then
            invalidOp "The workflow parallel wave state is malformed."

        let completed = normalizeCompleted envelope
        let expectedCompletedCount = expectedBranchIndices[0]

        if completed.Length <> expectedCompletedCount then
            invalidOp "The workflow parallel wave state is malformed."

        for index in 0 .. completed.Length - 1 do
            if completed[index].BranchIndex <> index then
                invalidOp "The workflow parallel wave state is malformed."

    let capture
        (expectedBranchIndices: int[])
        (item: ParallelWaveItem<'Input, 'BranchOutput>)
        (state: ParallelWaveCollectorState<'Input, 'BranchOutput>)
        =
        lock state.SyncRoot (fun () ->
            ensureShape expectedBranchIndices.Length state

            if state.Completed then
                AlreadyCompleted
            elif isNull item then
                invalidOp "The workflow parallel wave state is malformed."
            elif item.IsSeed then
                if state.HasSeed then
                    DuplicateSeed
                else
                    validateSeed expectedBranchIndices item.Envelope
                    state.Seed <- item.Envelope
                    state.HasSeed <- true

                    if state.ReceivedCount = expectedBranchIndices.Length then
                        state.Completed <- true
                        Ready(buildEnvelope expectedBranchIndices state)
                    else
                        Pending
            else
                match
                    expectedBranchIndices
                    |> Array.tryFindIndex (fun branchIndex -> branchIndex = item.BranchIndex)
                with
                | None -> InvalidBranchIndex item.BranchIndex
                | Some slot when state.Received[slot] -> DuplicateBranch item.BranchIndex
                | Some slot ->
                    state.Values[slot] <- item.BranchValue
                    state.Received[slot] <- true
                    state.ReceivedCount <- state.ReceivedCount + 1

                    if state.HasSeed && state.ReceivedCount = expectedBranchIndices.Length then
                        state.Completed <- true
                        Ready(buildEnvelope expectedBranchIndices state)
                    else
                        Pending)

type private ParallelBranchPlan =
    { BranchIndex: int
      AdapterId: string
      CollectorId: string }

type private ParallelWaveBranchPlan =
    { BranchIndex: int
      AdapterId: string
      CollectorId: string
      StartDispatchId: string
      StartReadyAdapterId: string
      StartPendingId: string
      IndexedEnvelopeId: string }

type private ParallelWavePlan =
    { WaveIndex: int
      Branches: ParallelWaveBranchPlan[]
      SeedId: string
      CollectorId: string
      PendingId: string option
      ReadyAdapterId: string option }

type private ParallelPlan =
    { ParallelId: string
      StartNodeId: string
      AggregateNodeId: string
      PendingNodeId: string
      CompleteNodeId: string
      BranchCount: int
      BranchOutputType: Type
      AggregateOutputType: Type
      AggregateHandler: WorkflowGraph.IAggregateHandler
      Waves: ParallelWavePlan[] }

type private PendingApprovalEntry =
    { Request: ExternalRequest
      Reserved: bool }

[<Sealed>]
type internal PendingApprovalRegistry() =
    let gate = obj ()
    let pending = Dictionary<string, PendingApprovalEntry>(StringComparer.Ordinal)

    member _.Register(request: ExternalRequest) =
        lock gate (fun () -> pending[request.RequestId] <- { Request = request; Reserved = false })

    member _.TryReserve(requestId: string) =
        lock gate (fun () ->
            match pending.TryGetValue requestId with
            | true, entry when not entry.Reserved ->
                pending[requestId] <- { entry with Reserved = true }
                ValueSome entry.Request
            | _ -> ValueNone)

    member _.Complete(requestId: string) =
        lock gate (fun () -> pending.Remove requestId |> ignore)

    member _.Release(requestId: string) =
        lock gate (fun () ->
            match pending.TryGetValue requestId with
            | true, entry when entry.Reserved -> pending[requestId] <- { entry with Reserved = false }
            | _ -> ())

    member _.Clear() = lock gate (fun () -> pending.Clear())
    member _.Count = lock gate (fun () -> pending.Count)

[<RequireQualifiedAccess>]
module internal ApprovalResponseDispatch =
    let sendAsync
        (pending: PendingApprovalRegistry)
        (response: ApprovalResponse)
        (cancellationToken: CancellationToken)
        (sendAsync: ExternalResponse -> CancellationToken -> Task)
        =
        task {
            match pending.TryReserve response.RequestId with
            | ValueSome request ->
                try
                    let externalResponse = request.CreateResponse(response)
                    do! sendAsync externalResponse cancellationToken
                    pending.Complete response.RequestId
                with ex ->
                    pending.Release response.RequestId
                    return raise ex
            | ValueNone ->
                return
                    raise (
                        InvalidOperationException(
                            "The supplied approval response token is unknown or has already been used."
                        )
                    )
        }


[<Sealed>]
type internal MafJsonCheckpointStore() =
    let gate = obj ()

    let checkpoints =
        Dictionary<string, ResizeArray<CheckpointInfo * JsonElement>>(StringComparer.Ordinal)

    let cloneElement (value: JsonElement) =
        use document = JsonDocument.Parse(value.GetRawText())
        document.RootElement.Clone()

    let getEntries sessionId =
        match checkpoints.TryGetValue sessionId with
        | true, entries -> entries
        | false, _ ->
            let entries = ResizeArray<CheckpointInfo * JsonElement>()
            checkpoints[sessionId] <- entries
            entries

    member _.Import(sessionId: string, checkpointInfo: CheckpointInfo, payload: JsonElement) =
        lock gate (fun () ->
            let entries = getEntries sessionId
            entries.Add(checkpointInfo, cloneElement payload))

    member _.Export(sessionId: string, checkpointInfo: CheckpointInfo) =
        lock gate (fun () ->
            match checkpoints.TryGetValue sessionId with
            | true, entries ->
                entries
                |> Seq.tryPick (fun (info, payload) ->
                    if info.CheckpointId = checkpointInfo.CheckpointId then
                        Some(cloneElement payload)
                    else
                        None)
                |> Option.defaultWith (fun () -> invalidOp "The requested workflow checkpoint payload was not found.")
            | false, _ -> invalidOp "The requested workflow checkpoint payload was not found.")

    interface ICheckpointStore<JsonElement> with
        member _.CreateCheckpointAsync(sessionId, state, _parentCheckpoint) =
            let checkpointInfo = CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"))

            lock gate (fun () ->
                let entries = getEntries sessionId
                entries.Add(checkpointInfo, cloneElement state))

            ValueTask<CheckpointInfo>(checkpointInfo)

        member this.RetrieveCheckpointAsync(sessionId, checkpointInfo) =
            ValueTask<JsonElement>(this.Export(sessionId, checkpointInfo))

        member _.RetrieveIndexAsync(sessionId, _parentCheckpoint) =
            let snapshot =
                lock gate (fun () ->
                    match checkpoints.TryGetValue sessionId with
                    | true, entries -> entries |> Seq.map fst |> Seq.toArray
                    | false, _ -> Array.empty)

            ValueTask<IEnumerable<CheckpointInfo>>(snapshot :> IEnumerable<CheckpointInfo>)

module private FailureFactory =
    let create code runId operationId requestId message innerException =
        CircuitFailure(code, message, ValueSome runId, operationId, requestId, innerException)

    let cancelled runId operationId ex =
        create CircuitFailureCode.Cancelled runId operationId ValueNone "The workflow run was cancelled." (ValueSome ex)

    let workflow runId operationId message ex =
        create CircuitFailureCode.Workflow runId operationId ValueNone message ex

    let approvalRequired runId requestId =
        create
            CircuitFailureCode.ApprovalRequired
            runId
            ValueNone
            (ValueSome requestId)
            "The workflow requires approval before it can continue."
            ValueNone

type private BindingFactory =
    static member Code<'Input, 'Output>
        (
            runtimeRunId: RunId,
            definitionId: DefinitionId,
            definitionVersion: SemanticVersion,
            nodeId: string,
            handler: WorkflowGraph.ICodeHandler
        ) =
        let callback =
            Func<'Input, IWorkflowContext, CancellationToken, ValueTask<'Output>>
                (fun input _workflowContext cancellationToken ->
                    ValueTask<'Output>(
                        task {
                            try
                                let context =
                                    WorkflowContext(
                                        runtimeRunId,
                                        definitionId,
                                        definitionVersion,
                                        nodeId,
                                        cancellationToken
                                    )

                                let! output = handler.InvokeAsync(context, box input, cancellationToken)
                                return unbox<'Output> output
                            with
                            | :? WorkflowStepFailureException as ex -> return raise ex
                            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.cancelled runtimeRunId (ValueSome nodeId) ex
                                        )
                                    )
                            | ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow step '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<'Input, 'Output>(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member Agent<'Input, 'Output>
        (runtime: MafRuntime, nodeId: string, agent: AgentDefinition, signature: Signature<'Input, 'Output>)
        =
        let callback =
            Func<'Input, IWorkflowContext, CancellationToken, ValueTask<'Output>>
                (fun input _workflowContext cancellationToken ->
                    ValueTask<'Output>(
                        task {
                            let! result =
                                (runtime :> ICircuitRuntime)
                                    .RunAsync(agent, signature, input, RunOptions.Default, cancellationToken)

                            if result.Result.IsSuccess then
                                return result.Result.Value
                            else
                                return raise (WorkflowStepFailureException(result.Result.Failure))
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<'Input, 'Output>(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ChoiceSelector<'Input>
        (runtimeRunId: RunId, nodeId: string, selector: WorkflowGraph.ISelectorHandler)
        =
        let callback =
            Func<'Input, IWorkflowContext, CancellationToken, ValueTask<WorkflowGraph.BranchSelection<'Input>>>
                (fun input _workflowContext cancellationToken ->
                    ValueTask<WorkflowGraph.BranchSelection<'Input>>(
                        task {
                            try
                                return
                                    ({ Key = selector.Invoke(box input)
                                       Value = input }
                                    : WorkflowGraph.BranchSelection<'Input>)
                            with ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow choice step '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<'Input, WorkflowGraph.BranchSelection<'Input>>(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ChoiceCaseAdapter<'Input>(nodeId: string, caseKey: string) =
        let callback =
            Func<WorkflowGraph.BranchSelection<'Input>, 'Input>(fun selection ->
                if StringComparer.Ordinal.Equals(selection.Key, caseKey) then
                    selection.Value
                else
                    raise (InvalidOperationException($"Branch '{caseKey}' was routed an unexpected value.")))

        ExecutorBindingExtensions.BindAsExecutor<WorkflowGraph.BranchSelection<'Input>, 'Input>(
            messageHandler = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ChoiceDefaultAdapter<'Input>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<WorkflowGraph.BranchSelection<'Input>, 'Input>(
            messageHandler =
                Func<WorkflowGraph.BranchSelection<'Input>, 'Input>
                    (fun (selection: WorkflowGraph.BranchSelection<'Input>) -> selection.Value),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member Identity<'T>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<'T, 'T>(
            messageHandler = Func<'T, 'T>(fun value -> value),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelCollector<'T>(nodeId: string, branchIndex: int) =
        let callback =
            Func<'T, WorkflowGraph.ParallelBranchResult<'T>>(fun value ->
                ({ BranchIndex = branchIndex
                   Value = value }
                : WorkflowGraph.ParallelBranchResult<'T>))

        ExecutorBindingExtensions.BindAsExecutor<'T, WorkflowGraph.ParallelBranchResult<'T>>(
            messageHandler = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelAggregate<'Input, 'Output>
        (
            runtimeRunId: RunId,
            nodeId: string,
            parallelId: string,
            branchCount: int,
            handler: WorkflowGraph.IAggregateHandler
        ) =
        let callback =
            Func<
                WorkflowGraph.ParallelBranchResult<'Input>,
                IWorkflowContext,
                CancellationToken,
                ValueTask<WorkflowGraph.ParallelAggregateDispatch<'Output>>
             >
                (fun value workflowContext cancellationToken ->
                    ValueTask<WorkflowGraph.ParallelAggregateDispatch<'Output>>(
                        task {
                            try
                                let scope = $"parallel:{parallelId}"

                                let! state =
                                    workflowContext.ReadOrInitStateAsync(
                                        "aggregate",
                                        Func<_>(fun () -> ParallelAggregateState.create<'Input> branchCount),
                                        scope,
                                        cancellationToken
                                    )

                                match ParallelAggregateState.capture branchCount value state with
                                | ParallelAggregateCapture.Pending ->
                                    do!
                                        workflowContext.QueueStateUpdateAsync(
                                            "aggregate",
                                            state,
                                            scope,
                                            cancellationToken
                                        )

                                    return
                                        ({ IsComplete = false
                                           Value = Unchecked.defaultof<'Output> }
                                        : WorkflowGraph.ParallelAggregateDispatch<'Output>)
                                | ParallelAggregateCapture.Complete ordered ->
                                    do!
                                        workflowContext.QueueStateUpdateAsync(
                                            "aggregate",
                                            state,
                                            scope,
                                            cancellationToken
                                        )

                                    let! output = handler.InvokeAsync(ordered |> List.map box, cancellationToken)

                                    return
                                        ({ IsComplete = true
                                           Value = unbox<'Output> output }
                                        : WorkflowGraph.ParallelAggregateDispatch<'Output>)
                                | ParallelAggregateCapture.DuplicateBranch branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel step '{nodeId}' received duplicate branch envelope {branchIndex}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelAggregateCapture.InvalidBranchIndex branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel step '{nodeId}' received branch envelope {branchIndex}, but expected 0..{branchCount - 1}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelAggregateCapture.AlreadyCompleted ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel step '{nodeId}' received an unexpected branch envelope after completion."
                                                    ValueNone
                                            )
                                        )
                            with
                            | :? WorkflowStepFailureException as ex -> return raise ex
                            | ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow parallel step '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<
            WorkflowGraph.ParallelBranchResult<'Input>,
            WorkflowGraph.ParallelAggregateDispatch<'Output>
         >(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member Prompt<'Input>
        (
            runtimeRunId: RunId,
            definitionId: DefinitionId,
            definitionVersion: SemanticVersion,
            nodeId: string,
            handler: WorkflowGraph.IPromptHandler
        ) =
        let callback =
            Func<'Input, IWorkflowContext, CancellationToken, ValueTask<ApprovalPrompt>>
                (fun input _workflowContext cancellationToken ->
                    ValueTask<ApprovalPrompt>(
                        task {
                            try
                                let _ =
                                    WorkflowContext(
                                        runtimeRunId,
                                        definitionId,
                                        definitionVersion,
                                        nodeId,
                                        cancellationToken
                                    )

                                return handler.Invoke(box input)
                            with ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow request step '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<'Input, ApprovalPrompt>(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member LoopGuard<'Input>
        (
            runtimeRunId: RunId,
            nodeId: string,
            loopId: string,
            maxIterations: int,
            predicate: WorkflowGraph.ILoopConditionHandler
        ) =
        let callback =
            Func<'Input, IWorkflowContext, CancellationToken, ValueTask<WorkflowGraph.LoopDecision<'Input>>>
                (fun input workflowContext cancellationToken ->
                    ValueTask<WorkflowGraph.LoopDecision<'Input>>(
                        task {
                            try
                                let scope = $"loop:{loopId}"

                                let! state =
                                    workflowContext.ReadOrInitStateAsync(
                                        "state",
                                        Func<_>(fun () -> LoopState()),
                                        scope,
                                        cancellationToken
                                    )

                                let shouldContinue = state.Iteration < maxIterations && predicate.Invoke(box input)

                                if shouldContinue then
                                    state.Iteration <- state.Iteration + 1
                                    do! workflowContext.QueueStateUpdateAsync("state", state, scope, cancellationToken)
                                else
                                    do! workflowContext.QueueClearScopeAsync(scope, cancellationToken)

                                return
                                    ({ Continue = shouldContinue
                                       Value = input }
                                    : WorkflowGraph.LoopDecision<'Input>)
                            with ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow loop step '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<'Input, WorkflowGraph.LoopDecision<'Input>>(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member LoopAdapter<'Input>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<WorkflowGraph.LoopDecision<'Input>, 'Input>(
            messageHandler =
                Func<WorkflowGraph.LoopDecision<'Input>, 'Input>(fun (decision: WorkflowGraph.LoopDecision<'Input>) ->
                    decision.Value),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveStart<'Input, 'BranchOutput>(nodeId: string) =
        let callback =
            Func<'Input, ParallelWaveEnvelope<'Input, 'BranchOutput>>(fun input ->
                let envelope = ParallelWaveEnvelope<'Input, 'BranchOutput>()
                envelope.Input <- input
                envelope.Completed <- Array.empty
                envelope)

        ExecutorBindingExtensions.BindAsExecutor<'Input, ParallelWaveEnvelope<'Input, 'BranchOutput>>(
            messageHandler = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveBranchStart<'Input, 'BranchOutput>
        (runtimeRunId: RunId, nodeId: string, parallelId: string, waveIndex: int, branchIndex: int)
        =
        let callback =
            Func<
                ParallelWaveEnvelope<'Input, 'BranchOutput>,
                IWorkflowContext,
                CancellationToken,
                ValueTask<ParallelWaveBranchDispatch<'Input>>
             >
                (fun envelope workflowContext cancellationToken ->
                    ValueTask<ParallelWaveBranchDispatch<'Input>>(
                        task {
                            try
                                let scope = $"parallel:{parallelId}:wave:{waveIndex}:branch:{branchIndex}"

                                let! state =
                                    workflowContext.ReadOrInitStateAsync(
                                        "start",
                                        Func<_>(fun () -> ParallelWaveBranchStartState()),
                                        scope,
                                        cancellationToken
                                    )

                                let dispatch = ParallelWaveBranchDispatch<'Input>()

                                if state.Started then
                                    dispatch.IsActive <- false
                                else
                                    state.Started <- true
                                    do! workflowContext.QueueStateUpdateAsync("start", state, scope, cancellationToken)
                                    dispatch.IsActive <- true
                                    dispatch.Input <- envelope.Input

                                return dispatch
                            with
                            | :? WorkflowStepFailureException as ex -> return raise ex
                            | ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow parallel wave branch '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<
            ParallelWaveEnvelope<'Input, 'BranchOutput>,
            ParallelWaveBranchDispatch<'Input>
         >(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveBranchReadyAdapter<'Input>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<ParallelWaveBranchDispatch<'Input>, 'Input>(
            messageHandler =
                Func<ParallelWaveBranchDispatch<'Input>, 'Input>(fun dispatch ->
                    if dispatch.IsActive then
                        dispatch.Input
                    else
                        raise (
                            InvalidOperationException(
                                $"Parallel wave branch '{nodeId}' was routed an inactive dispatch."
                            )
                        )),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveBranchPending<'Input>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<ParallelWaveBranchDispatch<'Input>, unit>(
            messageHandler = Func<ParallelWaveBranchDispatch<'Input>, unit>(fun _ -> ()),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveSeed<'Input, 'BranchOutput>(nodeId: string) =
        let callback =
            Func<ParallelWaveEnvelope<'Input, 'BranchOutput>, ParallelWaveItem<'Input, 'BranchOutput>>(fun envelope ->
                let item = ParallelWaveItem<'Input, 'BranchOutput>()
                item.IsSeed <- true
                item.Envelope <- envelope
                item)

        ExecutorBindingExtensions.BindAsExecutor<
            ParallelWaveEnvelope<'Input, 'BranchOutput>,
            ParallelWaveItem<'Input, 'BranchOutput>
         >(
            messageHandler = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveIndexedEnvelope<'Input, 'BranchOutput>(nodeId: string) =
        let callback =
            Func<WorkflowGraph.ParallelBranchResult<'BranchOutput>, ParallelWaveItem<'Input, 'BranchOutput>>
                (fun result ->
                    let item = ParallelWaveItem<'Input, 'BranchOutput>()
                    item.IsSeed <- false
                    item.BranchIndex <- result.BranchIndex
                    item.BranchValue <- result.Value
                    item)

        ExecutorBindingExtensions.BindAsExecutor<
            WorkflowGraph.ParallelBranchResult<'BranchOutput>,
            ParallelWaveItem<'Input, 'BranchOutput>
         >(
            messageHandler = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveCollector<'Input, 'BranchOutput>
        (runtimeRunId: RunId, nodeId: string, parallelId: string, waveIndex: int, branchIndices: int[])
        =
        let callback =
            Func<
                ParallelWaveItem<'Input, 'BranchOutput>,
                IWorkflowContext,
                CancellationToken,
                ValueTask<ParallelWaveDispatch<'Input, 'BranchOutput>>
             >
                (fun item workflowContext cancellationToken ->
                    ValueTask<ParallelWaveDispatch<'Input, 'BranchOutput>>(
                        task {
                            try
                                let scope = $"parallel:{parallelId}:wave:{waveIndex}"

                                let! state =
                                    workflowContext.ReadOrInitStateAsync(
                                        "collector",
                                        Func<_>(fun () ->
                                            ParallelWaveCollectorState.create<'Input, 'BranchOutput>
                                                branchIndices.Length),
                                        scope,
                                        cancellationToken
                                    )

                                match ParallelWaveCollectorState.capture branchIndices item state with
                                | ParallelWaveCapture.Pending ->
                                    do!
                                        workflowContext.QueueStateUpdateAsync(
                                            "collector",
                                            state,
                                            scope,
                                            cancellationToken
                                        )

                                    let dispatch = ParallelWaveDispatch<'Input, 'BranchOutput>()
                                    dispatch.IsReady <- false
                                    return dispatch
                                | ParallelWaveCapture.Ready envelope ->
                                    do! workflowContext.QueueClearScopeAsync(scope, cancellationToken)
                                    let dispatch = ParallelWaveDispatch<'Input, 'BranchOutput>()
                                    dispatch.IsReady <- true
                                    dispatch.Envelope <- envelope
                                    return dispatch
                                | ParallelWaveCapture.DuplicateSeed ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received a duplicate seed envelope."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.DuplicateBranch branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received duplicate branch envelope {branchIndex}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.InvalidBranchIndex branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received branch envelope {branchIndex}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.AlreadyCompleted ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received an unexpected envelope after completion."
                                                    ValueNone
                                            )
                                        )
                            with
                            | :? WorkflowStepFailureException as ex -> return raise ex
                            | ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow parallel wave '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<
            ParallelWaveItem<'Input, 'BranchOutput>,
            ParallelWaveDispatch<'Input, 'BranchOutput>
         >(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWaveReadyAdapter<'Input, 'BranchOutput>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<
            ParallelWaveDispatch<'Input, 'BranchOutput>,
            ParallelWaveEnvelope<'Input, 'BranchOutput>
         >(
            messageHandler =
                Func<ParallelWaveDispatch<'Input, 'BranchOutput>, ParallelWaveEnvelope<'Input, 'BranchOutput>>
                    (fun dispatch ->
                        if dispatch.IsReady then
                            dispatch.Envelope
                        else
                            raise (
                                InvalidOperationException(
                                    $"Parallel wave '{nodeId}' was routed an unexpected pending dispatch."
                                )
                            )),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelWavePending<'Input, 'BranchOutput>(nodeId: string) =
        ExecutorBindingExtensions.BindAsExecutor<ParallelWaveDispatch<'Input, 'BranchOutput>, unit>(
            messageHandler = Func<ParallelWaveDispatch<'Input, 'BranchOutput>, unit>(fun _ -> ()),
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

    static member ParallelFinalCollector<'Input, 'BranchOutput, 'Output>
        (
            runtimeRunId: RunId,
            nodeId: string,
            parallelId: string,
            waveIndex: int,
            branchIndices: int[],
            branchCount: int,
            handler: WorkflowGraph.IAggregateHandler
        ) =
        let callback =
            Func<
                ParallelWaveItem<'Input, 'BranchOutput>,
                IWorkflowContext,
                CancellationToken,
                ValueTask<WorkflowGraph.ParallelAggregateDispatch<'Output>>
             >
                (fun item workflowContext cancellationToken ->
                    ValueTask<WorkflowGraph.ParallelAggregateDispatch<'Output>>(
                        task {
                            try
                                let scope = $"parallel:{parallelId}:wave:{waveIndex}"

                                let! state =
                                    workflowContext.ReadOrInitStateAsync(
                                        "collector",
                                        Func<_>(fun () ->
                                            ParallelWaveCollectorState.create<'Input, 'BranchOutput>
                                                branchIndices.Length),
                                        scope,
                                        cancellationToken
                                    )

                                match ParallelWaveCollectorState.capture branchIndices item state with
                                | ParallelWaveCapture.Pending ->
                                    do!
                                        workflowContext.QueueStateUpdateAsync(
                                            "collector",
                                            state,
                                            scope,
                                            cancellationToken
                                        )

                                    return
                                        ({ IsComplete = false
                                           Value = Unchecked.defaultof<'Output> }
                                        : WorkflowGraph.ParallelAggregateDispatch<'Output>)
                                | ParallelWaveCapture.Ready envelope ->
                                    do! workflowContext.QueueClearScopeAsync(scope, cancellationToken)

                                    let completed =
                                        if isNull (box envelope.Completed) then
                                            Array.empty
                                        else
                                            envelope.Completed

                                    if completed.Length <> branchCount then
                                        invalidOp "The workflow parallel wave state is malformed."

                                    for branchIndex in 0 .. branchCount - 1 do
                                        if completed[branchIndex].BranchIndex <> branchIndex then
                                            invalidOp "The workflow parallel wave state is malformed."

                                    let! output =
                                        handler.InvokeAsync(
                                            completed |> Array.map (fun result -> box result.Value) |> Array.toList,
                                            cancellationToken
                                        )

                                    return
                                        ({ IsComplete = true
                                           Value = unbox<'Output> output }
                                        : WorkflowGraph.ParallelAggregateDispatch<'Output>)
                                | ParallelWaveCapture.DuplicateSeed ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received a duplicate seed envelope."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.DuplicateBranch branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received duplicate branch envelope {branchIndex}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.InvalidBranchIndex branchIndex ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received branch envelope {branchIndex}."
                                                    ValueNone
                                            )
                                        )
                                | ParallelWaveCapture.AlreadyCompleted ->
                                    return
                                        raise (
                                            WorkflowStepFailureException(
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    (ValueSome nodeId)
                                                    $"Workflow parallel wave '{nodeId}' received an unexpected envelope after completion."
                                                    ValueNone
                                            )
                                        )
                            with
                            | :? WorkflowStepFailureException as ex -> return raise ex
                            | ex ->
                                return
                                    raise (
                                        WorkflowStepFailureException(
                                            FailureFactory.workflow
                                                runtimeRunId
                                                (ValueSome nodeId)
                                                $"Workflow parallel wave '{nodeId}' failed."
                                                (ValueSome ex)
                                        )
                                    )
                        }
                    ))

        ExecutorBindingExtensions.BindAsExecutor<
            ParallelWaveItem<'Input, 'BranchOutput>,
            WorkflowGraph.ParallelAggregateDispatch<'Output>
         >(
            messageHandlerAsync = callback,
            id = nodeId,
            options = ExecutorOptions.Default,
            threadsafe = true
        )

type private SwitchFactory =
    static member Choice<'Input>
        (
            builder: WorkflowBuilder,
            source: ExecutorBinding,
            caseTargets: (string * ExecutorBinding)[],
            defaultTarget: ExecutorBinding option
        ) =
        WorkflowBuilderExtensions.AddSwitch(
            builder,
            source,
            Action<SwitchBuilder>(fun switchBuilder ->
                for caseKey, target in caseTargets do
                    switchBuilder.AddCase(
                        Func<WorkflowGraph.BranchSelection<'Input>, bool>(fun selection ->
                            StringComparer.Ordinal.Equals(selection.Key, caseKey)),
                        [| target |]
                    )
                    |> ignore

                match defaultTarget with
                | Some target -> switchBuilder.WithDefault([| target |]) |> ignore
                | None -> ())
        )
        |> ignore

    static member Start<'Input>
        (builder: WorkflowBuilder, source: ExecutorBinding, readyTarget: ExecutorBinding, pendingTarget: ExecutorBinding) =
        WorkflowBuilderExtensions.AddSwitch(
            builder,
            source,
            Action<SwitchBuilder>(fun switchBuilder ->
                switchBuilder.AddCase(
                    Func<ParallelWaveBranchDispatch<'Input>, bool>(fun dispatch -> dispatch.IsActive),
                    [| readyTarget |]
                )
                |> ignore

                switchBuilder.WithDefault([| pendingTarget |]) |> ignore)
        )
        |> ignore

    static member Wave<'Input, 'BranchOutput>
        (builder: WorkflowBuilder, source: ExecutorBinding, readyTarget: ExecutorBinding, pendingTarget: ExecutorBinding) =
        WorkflowBuilderExtensions.AddSwitch(
            builder,
            source,
            Action<SwitchBuilder>(fun switchBuilder ->
                switchBuilder.AddCase(
                    Func<ParallelWaveDispatch<'Input, 'BranchOutput>, bool>(fun dispatch -> dispatch.IsReady),
                    [| readyTarget |]
                )
                |> ignore

                switchBuilder.WithDefault([| pendingTarget |]) |> ignore)
        )
        |> ignore

    static member Aggregate<'Output>
        (
            builder: WorkflowBuilder,
            source: ExecutorBinding,
            pendingTarget: ExecutorBinding,
            completeTarget: ExecutorBinding
        ) =
        WorkflowBuilderExtensions.AddSwitch(
            builder,
            source,
            Action<SwitchBuilder>(fun switchBuilder ->
                switchBuilder.AddCase(
                    Func<WorkflowGraph.ParallelAggregateDispatch<'Output>, bool>(fun value -> value.IsComplete),
                    [| completeTarget |]
                )
                |> ignore

                switchBuilder.WithDefault([| pendingTarget |]) |> ignore)
        )
        |> ignore

    static member Loop<'Input>
        (builder: WorkflowBuilder, source: ExecutorBinding, continueTarget: ExecutorBinding, exitTarget: ExecutorBinding) =
        WorkflowBuilderExtensions.AddSwitch(
            builder,
            source,
            Action<SwitchBuilder>(fun switchBuilder ->
                switchBuilder.AddCase(
                    Func<WorkflowGraph.LoopDecision<'Input>, bool>(fun value -> value.Continue),
                    [| continueTarget |]
                )
                |> ignore

                switchBuilder.WithDefault([| exitTarget |]) |> ignore)
        )
        |> ignore

let private bindingMethod name genericArity =
    typeof<BindingFactory>.GetMethods(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
    |> Array.find (fun methodInfo ->
        methodInfo.Name = name
        && methodInfo.IsGenericMethodDefinition
        && methodInfo.GetGenericArguments().Length = genericArity)

let private switchMethod name genericArity =
    typeof<SwitchFactory>.GetMethods(BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
    |> Array.find (fun methodInfo ->
        methodInfo.Name = name
        && methodInfo.IsGenericMethodDefinition
        && methodInfo.GetGenericArguments().Length = genericArity)

let private codeMethod = bindingMethod "Code" 2
let private agentMethod = bindingMethod "Agent" 2
let private selectorMethod = bindingMethod "ChoiceSelector" 1
let private choiceCaseMethod = bindingMethod "ChoiceCaseAdapter" 1
let private choiceDefaultMethod = bindingMethod "ChoiceDefaultAdapter" 1
let private identityMethod = bindingMethod "Identity" 1
let private parallelCollectorMethod = bindingMethod "ParallelCollector" 1
let private parallelAggregateMethod = bindingMethod "ParallelAggregate" 2
let private parallelWaveStartMethod = bindingMethod "ParallelWaveStart" 2

let private parallelWaveBranchStartMethod =
    bindingMethod "ParallelWaveBranchStart" 2

let private parallelWaveBranchReadyAdapterMethod =
    bindingMethod "ParallelWaveBranchReadyAdapter" 1

let private parallelWaveBranchPendingMethod =
    bindingMethod "ParallelWaveBranchPending" 1

let private parallelWaveSeedMethod = bindingMethod "ParallelWaveSeed" 2

let private parallelWaveIndexedEnvelopeMethod =
    bindingMethod "ParallelWaveIndexedEnvelope" 2

let private parallelWaveCollectorMethod = bindingMethod "ParallelWaveCollector" 2

let private parallelWaveReadyAdapterMethod =
    bindingMethod "ParallelWaveReadyAdapter" 2

let private parallelWavePendingMethod = bindingMethod "ParallelWavePending" 2
let private parallelFinalCollectorMethod = bindingMethod "ParallelFinalCollector" 3
let private promptMethod = bindingMethod "Prompt" 1
let private loopGuardMethod = bindingMethod "LoopGuard" 1
let private loopAdapterMethod = bindingMethod "LoopAdapter" 1

let private choiceSwitchMethod = switchMethod "Choice" 1
let private startSwitchMethod = switchMethod "Start" 1
let private waveSwitchMethod = switchMethod "Wave" 2
let private aggregateSwitchMethod = switchMethod "Aggregate" 1
let private loopSwitchMethod = switchMethod "Loop" 1

let private invokeBinding (methodInfo: MethodInfo) (genericTypes: Type[]) (args: obj[]) =
    methodInfo.MakeGenericMethod(genericTypes).Invoke(null, args) :?> ExecutorBinding

let private invokeSwitch (methodInfo: MethodInfo) (genericTypes: Type[]) (args: obj[]) =
    methodInfo.MakeGenericMethod(genericTypes).Invoke(null, args) |> ignore

let private buildBinding
    (runtime: MafRuntime)
    (definitionId: DefinitionId)
    (definitionVersion: SemanticVersion)
    (runtimeRunId: RunId)
    (node: WorkflowGraph.Node)
    =
    match node.Kind with
    | WorkflowGraph.Code handler ->
        invokeBinding
            codeMethod
            [| node.InputType; node.OutputType |]
            [| box runtimeRunId
               box definitionId
               box definitionVersion
               box node.Id
               box handler |]
    | WorkflowGraph.Agent(agent, signature) ->
        invokeBinding
            agentMethod
            [| node.InputType; node.OutputType |]
            [| box runtime; box node.Id; box agent; signature.Value |]
    | WorkflowGraph.ChoiceSelector(selector, _, _) ->
        invokeBinding selectorMethod [| node.InputType |] [| box runtimeRunId; box node.Id; box selector |]
    | WorkflowGraph.ChoiceCaseAdapter caseKey ->
        invokeBinding choiceCaseMethod [| node.OutputType |] [| box node.Id; box caseKey |]
    | WorkflowGraph.ChoiceDefaultAdapter -> invokeBinding choiceDefaultMethod [| node.OutputType |] [| box node.Id |]
    | WorkflowGraph.ParallelFanOut _
    | WorkflowGraph.ParallelBranchAdapter _ -> invokeBinding identityMethod [| node.InputType |] [| box node.Id |]
    | WorkflowGraph.ParallelCollector(_, branchIndex, _) ->
        invokeBinding parallelCollectorMethod [| node.InputType |] [| box node.Id; box branchIndex |]
    | WorkflowGraph.ParallelAggregate(_, _, _, handler) ->
        invokeBinding
            parallelAggregateMethod
            [| node.InputType.GenericTypeArguments[0]
               node.OutputType.GenericTypeArguments[0] |]
            [| box runtimeRunId
               box node.Id
               box (
                   match node.Kind with
                   | WorkflowGraph.ParallelAggregate(parallelId, _, _, _) -> parallelId
                   | _ -> invalidOp "unreachable"
               )
               box (
                   match node.Kind with
                   | WorkflowGraph.ParallelAggregate(_, branchCount, _, _) -> branchCount
                   | _ -> invalidOp "unreachable"
               )
               box handler |]
    | WorkflowGraph.RequestPrompt handler ->
        invokeBinding
            promptMethod
            [| node.InputType |]
            [| box runtimeRunId
               box definitionId
               box definitionVersion
               box node.Id
               box handler |]
    | WorkflowGraph.RequestPort ->
        RequestPort.Create<ApprovalPrompt, ApprovalResponse>(node.Id)
        |> ExecutorBinding.op_Implicit
    | WorkflowGraph.LoopGuard(loopId, maxIterations, predicate) ->
        invokeBinding
            loopGuardMethod
            [| node.InputType |]
            [| box runtimeRunId
               box node.Id
               box loopId
               box maxIterations
               box predicate |]
    | WorkflowGraph.LoopContinueAdapter
    | WorkflowGraph.LoopExit -> invokeBinding loopAdapterMethod [| node.OutputType |] [| box node.Id |]

let private buildParallelPlans
    (definition: WorkflowDefinition<'Input, 'Output>)
    (nodesById: IDictionary<string, WorkflowGraph.Node>)
    (outgoing: IDictionary<string, seq<WorkflowGraph.Edge>>)
    =
    let collectorsByParallelAndIndex = Dictionary<string * int, string>()

    let aggregatesByParallelId =
        Dictionary<string, WorkflowGraph.Node>(StringComparer.Ordinal)

    let plans = Dictionary<string, ParallelPlan>(StringComparer.Ordinal)

    for node in definition.Nodes do
        match node.Kind with
        | WorkflowGraph.ParallelCollector(parallelId, branchIndex, _) ->
            collectorsByParallelAndIndex[(parallelId, branchIndex)] <- node.Id
        | WorkflowGraph.ParallelAggregate(parallelId, _, _, _) -> aggregatesByParallelId[parallelId] <- node
        | _ -> ()

    for node in definition.Nodes do
        match node.Kind with
        | WorkflowGraph.ParallelFanOut(parallelId, branchCount, maxConcurrency) ->
            let aggregateNode =
                match aggregatesByParallelId.TryGetValue parallelId with
                | true, aggregateNode -> aggregateNode
                | _ -> invalidOp $"Parallel step '{parallelId}' is missing its aggregate node."

            let aggregateHandler =
                match aggregateNode.Kind with
                | WorkflowGraph.ParallelAggregate(_, _, _, handler) -> handler
                | _ -> invalidOp "unreachable"

            let aggregatePendingNodeId, aggregateCompleteNodeId =
                match outgoing.TryGetValue aggregateNode.Id with
                | true, edges ->
                    let pending =
                        edges
                        |> Seq.find (fun edge -> edge.TargetId.EndsWith(".pending", StringComparison.Ordinal))

                    let complete =
                        edges
                        |> Seq.find (fun edge -> edge.TargetId.EndsWith(".complete", StringComparison.Ordinal))

                    pending.TargetId, complete.TargetId
                | _ -> invalidOp "Parallel aggregate routing is incomplete."

            let branches =
                [| for branchIndex in 0 .. branchCount - 1 do
                       let adapterId =
                           match outgoing.TryGetValue node.Id with
                           | true, edges ->
                               edges
                               |> Seq.tryPick (fun edge ->
                                   match nodesById[edge.TargetId].Kind with
                                   | WorkflowGraph.ParallelBranchAdapter currentIndex when currentIndex = branchIndex ->
                                       Some edge.TargetId
                                   | _ -> None)
                               |> Option.defaultWith (fun () ->
                                   invalidOp
                                       $"Parallel branch {branchIndex} for '{parallelId}' is missing its adapter.")
                           | _ -> invalidOp $"Parallel branch {branchIndex} for '{parallelId}' is missing its adapter."

                       let collectorId =
                           match collectorsByParallelAndIndex.TryGetValue((parallelId, branchIndex)) with
                           | true, collectorId -> collectorId
                           | _ ->
                               invalidOp $"Parallel branch {branchIndex} for '{parallelId}' is missing its collector."

                       yield
                           { BranchIndex = branchIndex
                             AdapterId = adapterId
                             CollectorId = collectorId } |]

            if branches.Length = 0 then
                invalidOp $"Parallel step '{parallelId}' must declare at least one branch."

            let waveGroups = branches |> Array.chunkBySize maxConcurrency

            let waves =
                waveGroups
                |> Array.mapi (fun waveIndex waveBranches ->
                    let wavePrefix = $"{parallelId}.__maf_wave.{waveIndex}"
                    let isFinal = waveIndex = waveGroups.Length - 1

                    let pendingId, readyAdapterId =
                        if isFinal then
                            None, None
                        else
                            Some $"{wavePrefix}.pending", Some $"{wavePrefix}.ready"

                    { WaveIndex = waveIndex
                      Branches =
                        waveBranches
                        |> Array.map (fun branch ->
                            { BranchIndex = branch.BranchIndex
                              AdapterId = branch.AdapterId
                              CollectorId = branch.CollectorId
                              StartDispatchId =
                                $"{parallelId}.branch.{branch.BranchIndex}.__maf_wave.{waveIndex}.start"
                              StartReadyAdapterId =
                                $"{parallelId}.branch.{branch.BranchIndex}.__maf_wave.{waveIndex}.ready"
                              StartPendingId =
                                $"{parallelId}.branch.{branch.BranchIndex}.__maf_wave.{waveIndex}.pending"
                              IndexedEnvelopeId =
                                $"{parallelId}.branch.{branch.BranchIndex}.__maf_wave.{waveIndex}.indexed" })
                      SeedId = $"{wavePrefix}.seed"
                      CollectorId = $"{wavePrefix}.collect"
                      PendingId = pendingId
                      ReadyAdapterId = readyAdapterId })

            plans[parallelId] <-
                { ParallelId = parallelId
                  StartNodeId = node.Id
                  AggregateNodeId = aggregateNode.Id
                  PendingNodeId = aggregatePendingNodeId
                  CompleteNodeId = aggregateCompleteNodeId
                  BranchCount = branchCount
                  BranchOutputType = nodesById[branches[0].CollectorId].InputType
                  AggregateOutputType = nodesById[aggregateCompleteNodeId].OutputType
                  AggregateHandler = aggregateHandler
                  Waves = waves }
        | _ -> ()

    plans

let private buildWorkflow
    (runtime: MafRuntime)
    (definition: WorkflowDefinition<'Input, 'Output>)
    (runtimeRunId: RunId)
    =
    let issues = Workflow.validate definition

    if issues.Count > 0 then
        let message =
            issues
            |> Seq.map (fun issue ->
                if String.IsNullOrWhiteSpace issue.NodeId then
                    issue.Message
                else
                    $"{issue.NodeId}: {issue.Message}")
            |> String.concat "; "

        invalidOp $"Workflow definition is invalid: {message}"

    let nodesById = definition.Nodes |> Seq.map (fun node -> node.Id, node) |> dict
    let outgoing = definition.Edges |> Seq.groupBy _.SourceId |> dict
    let parallelPlans = buildParallelPlans definition nodesById outgoing
    let bindings = Dictionary<string, ExecutorBinding>(StringComparer.Ordinal)

    for node in definition.Nodes do
        match node.Kind with
        | WorkflowGraph.ParallelFanOut(parallelId, _, _) ->
            let plan = parallelPlans[parallelId]

            bindings[node.Id] <-
                invokeBinding parallelWaveStartMethod [| node.InputType; plan.BranchOutputType |] [| box node.Id |]
        | _ -> bindings[node.Id] <- buildBinding runtime definition.Id definition.Version runtimeRunId node

    for KeyValue(_, plan) in parallelPlans do
        let inputType = nodesById[plan.StartNodeId].InputType

        for wave in plan.Waves do
            bindings[wave.SeedId] <-
                invokeBinding parallelWaveSeedMethod [| inputType; plan.BranchOutputType |] [| box wave.SeedId |]

            match wave.PendingId, wave.ReadyAdapterId with
            | Some pendingId, Some readyAdapterId ->
                bindings[wave.CollectorId] <-
                    invokeBinding
                        parallelWaveCollectorMethod
                        [| inputType; plan.BranchOutputType |]
                        [| box runtimeRunId
                           box wave.CollectorId
                           box plan.ParallelId
                           box wave.WaveIndex
                           box (wave.Branches |> Array.map _.BranchIndex) |]

                bindings[pendingId] <-
                    invokeBinding parallelWavePendingMethod [| inputType; plan.BranchOutputType |] [| box pendingId |]

                bindings[readyAdapterId] <-
                    invokeBinding
                        parallelWaveReadyAdapterMethod
                        [| inputType; plan.BranchOutputType |]
                        [| box readyAdapterId |]
            | None, None ->
                bindings[wave.CollectorId] <-
                    invokeBinding
                        parallelFinalCollectorMethod
                        [| inputType; plan.BranchOutputType; plan.AggregateOutputType |]
                        [| box runtimeRunId
                           box wave.CollectorId
                           box plan.ParallelId
                           box wave.WaveIndex
                           box (wave.Branches |> Array.map _.BranchIndex)
                           box plan.BranchCount
                           box plan.AggregateHandler |]
            | _ -> invalidOp "Parallel wave routing is malformed."

            for branch in wave.Branches do
                bindings[branch.StartDispatchId] <-
                    invokeBinding
                        parallelWaveBranchStartMethod
                        [| inputType; plan.BranchOutputType |]
                        [| box runtimeRunId
                           box branch.StartDispatchId
                           box plan.ParallelId
                           box wave.WaveIndex
                           box branch.BranchIndex |]

                bindings[branch.StartReadyAdapterId] <-
                    invokeBinding
                        parallelWaveBranchReadyAdapterMethod
                        [| inputType |]
                        [| box branch.StartReadyAdapterId |]

                bindings[branch.StartPendingId] <-
                    invokeBinding parallelWaveBranchPendingMethod [| inputType |] [| box branch.StartPendingId |]

                bindings[branch.IndexedEnvelopeId] <-
                    invokeBinding
                        parallelWaveIndexedEnvelopeMethod
                        [| inputType; plan.BranchOutputType |]
                        [| box branch.IndexedEnvelopeId |]

    let builder = WorkflowBuilder(bindings[definition.EntryId])

    OpenTelemetryWorkflowBuilderExtensions.WithOpenTelemetry(builder, null, TelemetryContracts.ActivitySource)
    |> ignore

    for node in definition.Nodes do
        match node.Kind with
        | WorkflowGraph.ChoiceSelector(_, _, _) ->
            let caseTargets =
                match outgoing.TryGetValue node.Id with
                | true, edges ->
                    edges
                    |> Seq.choose (fun edge ->
                        match nodesById[edge.TargetId].Kind with
                        | WorkflowGraph.ChoiceCaseAdapter caseKey -> Some(caseKey, bindings[edge.TargetId])
                        | _ -> None)
                    |> Seq.toArray
                | _ -> Array.empty

            let defaultTarget =
                match outgoing.TryGetValue node.Id with
                | true, edges ->
                    edges
                    |> Seq.tryPick (fun edge ->
                        match nodesById[edge.TargetId].Kind with
                        | WorkflowGraph.ChoiceDefaultAdapter -> Some bindings[edge.TargetId]
                        | _ -> None)
                | _ -> None

            invokeSwitch
                choiceSwitchMethod
                [| node.InputType |]
                [| box builder; box bindings[node.Id]; box caseTargets; box defaultTarget |]
        | WorkflowGraph.ParallelFanOut(parallelId, _, _) ->
            let plan = parallelPlans[parallelId]
            let inputType = nodesById[plan.StartNodeId].InputType
            let mutable waveSource = bindings[plan.StartNodeId]

            for wave in plan.Waves do
                let fanOutTargets =
                    Array.append
                        [| bindings[wave.SeedId] |]
                        (wave.Branches |> Array.map (fun branch -> bindings[branch.StartDispatchId]))

                builder.AddFanOutEdge(waveSource, fanOutTargets) |> ignore

                for branch in wave.Branches do
                    invokeSwitch
                        startSwitchMethod
                        [| inputType |]
                        [| box builder
                           box bindings[branch.StartDispatchId]
                           box bindings[branch.StartReadyAdapterId]
                           box bindings[branch.StartPendingId] |]

                let fanInSources =
                    Array.append
                        [| bindings[wave.SeedId] |]
                        (wave.Branches |> Array.map (fun branch -> bindings[branch.IndexedEnvelopeId]))

                builder.AddFanInBarrierEdge(fanInSources, bindings[wave.CollectorId]) |> ignore

                match wave.PendingId, wave.ReadyAdapterId with
                | Some pendingId, Some readyAdapterId ->
                    invokeSwitch
                        waveSwitchMethod
                        [| inputType; plan.BranchOutputType |]
                        [| box builder
                           box bindings[wave.CollectorId]
                           box bindings[readyAdapterId]
                           box bindings[pendingId] |]

                    waveSource <- bindings[readyAdapterId]
                | None, None ->
                    invokeSwitch
                        aggregateSwitchMethod
                        [| plan.AggregateOutputType |]
                        [| box builder
                           box bindings[wave.CollectorId]
                           box bindings[plan.PendingNodeId]
                           box bindings[plan.CompleteNodeId] |]
                | _ -> invalidOp "Parallel wave routing is malformed."
        | WorkflowGraph.LoopGuard _ ->
            let continueTarget, exitTarget =
                match outgoing.TryGetValue node.Id with
                | true, edges ->
                    let continueEdge =
                        edges
                        |> Seq.find (fun edge -> edge.TargetId.EndsWith(".continue", StringComparison.Ordinal))

                    let exitEdge =
                        edges
                        |> Seq.find (fun edge -> edge.TargetId.EndsWith(".exit", StringComparison.Ordinal))

                    bindings[continueEdge.TargetId], bindings[exitEdge.TargetId]
                | _ -> invalidOp "Loop routing is incomplete."

            invokeSwitch
                loopSwitchMethod
                [| node.InputType |]
                [| box builder; box bindings[node.Id]; box continueTarget; box exitTarget |]
        | _ -> ()

    for KeyValue(_, plan) in parallelPlans do
        for wave in plan.Waves do
            for branch in wave.Branches do
                builder.AddEdge(bindings[branch.StartReadyAdapterId], bindings[branch.AdapterId])
                |> ignore

                builder.AddEdge(bindings[branch.CollectorId], bindings[branch.IndexedEnvelopeId])
                |> ignore

    for edge in definition.Edges do
        let sourceNode = nodesById[edge.SourceId]
        let targetNode = nodesById[edge.TargetId]

        let skipDirectEdge =
            match sourceNode.Kind, targetNode.Kind with
            | WorkflowGraph.ChoiceSelector _, _ -> true
            | WorkflowGraph.ParallelFanOut _, _ -> true
            | WorkflowGraph.ParallelCollector _, WorkflowGraph.ParallelAggregate _ -> true
            | WorkflowGraph.ParallelAggregate _, _ -> true
            | WorkflowGraph.LoopGuard _, _ -> true
            | _ -> false

        if not skipDirectEdge then
            builder.AddEdge(bindings[edge.SourceId], bindings[edge.TargetId]) |> ignore

    let intermediateBindings =
        definition.Nodes
        |> Seq.filter (fun node ->
            node.OutputType = definition.OutputType
            && not (definition.TerminalIds |> List.contains node.Id)
            && match node.Kind with
               | WorkflowGraph.ParallelFanOut _ -> false
               | _ -> true)
        |> Seq.map (fun node -> bindings[node.Id])
        |> Seq.toArray

    if intermediateBindings.Length > 0 then
        WorkflowBuilderExtensions.WithIntermediateOutputFrom(builder, intermediateBindings)
        |> ignore

    builder.WithOutputFrom(
        definition.TerminalIds
        |> Seq.map (fun nodeId -> bindings[nodeId])
        |> Seq.toArray
    )
    |> ignore

    builder.Build(true)

type internal CompiledMafWorkflow<'Input, 'Output>
    (definition: WorkflowDefinition<'Input, 'Output>, runtime: MafRuntime, fingerprint: string) =
    let createEnvironment store =
        InProcessExecution.Concurrent.WithCheckpointing(
            CheckpointManager.CreateJson(store, runtime.RuntimeOptions.JsonSerializerOptions)
        )

    let cloneElement (value: JsonElement) =
        use document = JsonDocument.Parse(value.GetRawText())
        document.RootElement.Clone()

    let createFailureFromEvent runId operationId (event: WorkflowEvent) =
        match event with
        | :? ExecutorFailedEvent as executorFailed ->
            match executorFailed.Data with
            | :? WorkflowStepFailureException as workflowFailure -> workflowFailure.Failure
            | :? OperationCanceledException as ex -> FailureFactory.cancelled runId operationId ex
            | ex -> FailureFactory.workflow runId operationId "The workflow step failed." (ValueSome ex)
        | :? WorkflowErrorEvent as workflowError ->
            match workflowError.Exception with
            | :? WorkflowStepFailureException as workflowFailure -> workflowFailure.Failure
            | :? OperationCanceledException as ex -> FailureFactory.cancelled runId operationId ex
            | null -> FailureFactory.workflow runId operationId "The workflow failed." ValueNone
            | ex -> FailureFactory.workflow runId operationId "The workflow failed." (ValueSome ex)
        | _ -> FailureFactory.workflow runId operationId "The workflow failed." ValueNone

    let createApprovalRequest (request: ExternalRequest) =
        let mutable prompt = Unchecked.defaultof<ApprovalPrompt>

        if request.TryGetDataAs<ApprovalPrompt>(&prompt) then
            let payload =
                JsonSerializer.Serialize(prompt, runtime.RuntimeOptions.JsonSerializerOptions)

            ApprovalRequest(request.RequestId, prompt.Title, ValueSome payload)
        else
            let fallback = ApprovalPrompt.Create("workflow-approval", request.Data.ToString())

            let payload =
                JsonSerializer.Serialize(fallback, runtime.RuntimeOptions.JsonSerializerOptions)

            ApprovalRequest(request.RequestId, fallback.Title, ValueSome payload)

    let createCheckpoint (store: MafJsonCheckpointStore) (streamingRun: StreamingRun) =
        task {
            let checkpointInfo = streamingRun.LastCheckpoint

            if isNull checkpointInfo then
                return raise (InvalidOperationException("No workflow checkpoint is available yet."))
            else
                let payload = store.Export(streamingRun.SessionId, checkpointInfo)

                return
                    WorkflowCheckpoint<'Output>(
                        definition.Id,
                        definition.Version,
                        fingerprint,
                        streamingRun.SessionId,
                        checkpointInfo.CheckpointId,
                        DateTimeOffset.UtcNow,
                        cloneElement payload
                    )
        }

    let createRunWrapper
        (runtimeRunId: RunId)
        (store: MafJsonCheckpointStore)
        (streamingRun: StreamingRun)
        (runCancellationToken: CancellationToken)
        =
        let pending = PendingApprovalRegistry()

        let eventStream =
            { new IAsyncEnumerable<RunEvent<'Output>> with
                member _.GetAsyncEnumerator(enumeratorCancellationToken: CancellationToken) =
                    let abandonmentCts = new CancellationTokenSource()

                    let watcherCts =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            runCancellationToken,
                            enumeratorCancellationToken,
                            abandonmentCts.Token
                        )

                    let deliveryToken = abandonmentCts.Token
                    let watcherToken = watcherCts.Token
                    let channel = createBoundedChannel<RunEvent<'Output>> ()

                    let background: Task =
                        task {
                            let inner =
                                streamingRun.WatchStreamAsync(watcherToken).GetAsyncEnumerator(watcherToken)

                            let mutable sequence = -1L
                            let mutable terminalEventWritten = false

                            let emit kind operationId textDelta value failure approval =
                                task {
                                    if
                                        terminalEventWritten
                                        && (kind = RunEventKind.RunCompleted || kind = RunEventKind.RunFailed)
                                    then
                                        ()
                                    else
                                        if kind = RunEventKind.RunCompleted || kind = RunEventKind.RunFailed then
                                            terminalEventWritten <- true
                                            pending.Clear()

                                        sequence <- sequence + 1L

                                        let event =
                                            RunEvent(
                                                sequence,
                                                runtimeRunId,
                                                DateTimeOffset.UtcNow,
                                                kind,
                                                operationId,
                                                textDelta,
                                                value,
                                                failure,
                                                approval
                                            )

                                        let writeToken =
                                            match kind with
                                            | RunEventKind.RunStarted
                                            | RunEventKind.RunCompleted
                                            | RunEventKind.RunFailed -> deliveryToken
                                            | _ -> watcherToken

                                        do! writeToChannelAsync channel.Writer event writeToken
                                }

                            let emitRunCancelled () =
                                task {
                                    if not terminalEventWritten then
                                        let failure =
                                            FailureFactory.cancelled
                                                runtimeRunId
                                                ValueNone
                                                (OperationCanceledException(runCancellationToken))

                                        do!
                                            emit
                                                RunEventKind.RunFailed
                                                ValueNone
                                                ValueNone
                                                ValueNone
                                                (ValueSome failure)
                                                ValueNone
                                }

                            try
                                try
                                    let mutable keepWatching = true

                                    while keepWatching && not terminalEventWritten do
                                        let! moved = inner.MoveNextAsync().AsTask()

                                        if not moved then
                                            keepWatching <- false
                                        else
                                            let workflowEvent = inner.Current

                                            let mapped =
                                                match workflowEvent with
                                                | :? WorkflowStartedEvent ->
                                                    Some(
                                                        RunEventKind.RunStarted,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone
                                                    )
                                                | :? ExecutorInvokedEvent as invoked ->
                                                    Some(
                                                        RunEventKind.StepStarted,
                                                        ValueSome invoked.ExecutorId,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone
                                                    )
                                                | :? ExecutorCompletedEvent as completed ->
                                                    Some(
                                                        RunEventKind.StepCompleted,
                                                        ValueSome completed.ExecutorId,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone
                                                    )
                                                | :? WorkflowOutputEvent as outputEvent when
                                                    WorkflowOutputEventExtensions.IsIntermediate(outputEvent)
                                                    ->
                                                    match outputEvent.Data with
                                                    | :? 'Output as value ->
                                                        Some(
                                                            RunEventKind.IntermediateOutput,
                                                            ValueSome outputEvent.ExecutorId,
                                                            ValueNone,
                                                            ValueSome value,
                                                            ValueNone,
                                                            ValueNone
                                                        )
                                                    | _ ->
                                                        let failure =
                                                            FailureFactory.workflow
                                                                runtimeRunId
                                                                (ValueSome outputEvent.ExecutorId)
                                                                "The workflow emitted an invalid intermediate output type."
                                                                ValueNone

                                                        Some(
                                                            RunEventKind.RunFailed,
                                                            ValueSome outputEvent.ExecutorId,
                                                            ValueNone,
                                                            ValueNone,
                                                            ValueSome failure,
                                                            ValueNone
                                                        )
                                                | :? WorkflowOutputEvent as outputEvent ->
                                                    match outputEvent.Data with
                                                    | :? 'Output as value ->
                                                        Some(
                                                            RunEventKind.RunCompleted,
                                                            ValueSome outputEvent.ExecutorId,
                                                            ValueNone,
                                                            ValueSome value,
                                                            ValueNone,
                                                            ValueNone
                                                        )
                                                    | _ ->
                                                        let failure =
                                                            FailureFactory.workflow
                                                                runtimeRunId
                                                                (ValueSome outputEvent.ExecutorId)
                                                                "The workflow emitted an invalid final output type."
                                                                ValueNone

                                                        Some(
                                                            RunEventKind.RunFailed,
                                                            ValueSome outputEvent.ExecutorId,
                                                            ValueNone,
                                                            ValueNone,
                                                            ValueSome failure,
                                                            ValueNone
                                                        )
                                                | :? RequestInfoEvent as requestInfo ->
                                                    pending.Register requestInfo.Request

                                                    Some(
                                                        RunEventKind.ApprovalRequested,
                                                        ValueSome requestInfo.Request.PortInfo.PortId,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueSome(createApprovalRequest requestInfo.Request)
                                                    )
                                                | :? ExecutorFailedEvent as failed ->
                                                    let failure =
                                                        createFailureFromEvent
                                                            runtimeRunId
                                                            (ValueSome failed.ExecutorId)
                                                            failed

                                                    Some(
                                                        RunEventKind.RunFailed,
                                                        ValueSome failed.ExecutorId,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueSome failure,
                                                        ValueNone
                                                    )
                                                | :? WorkflowErrorEvent as workflowError ->
                                                    let failure =
                                                        createFailureFromEvent runtimeRunId ValueNone workflowError

                                                    Some(
                                                        RunEventKind.RunFailed,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueNone,
                                                        ValueSome failure,
                                                        ValueNone
                                                    )
                                                | _ -> None

                                            match mapped with
                                            | Some(kind, operationId, textDelta, value, failure, approval) ->
                                                do! emit kind operationId textDelta value failure approval
                                            | None -> ()

                                    if not terminalEventWritten then
                                        if
                                            runCancellationToken.IsCancellationRequested
                                            && not abandonmentCts.Token.IsCancellationRequested
                                        then
                                            do! emitRunCancelled ()
                                        elif not watcherToken.IsCancellationRequested then
                                            let failure =
                                                FailureFactory.workflow
                                                    runtimeRunId
                                                    ValueNone
                                                    "The workflow completed without producing a terminal output."
                                                    ValueNone

                                            do!
                                                emit
                                                    RunEventKind.RunFailed
                                                    ValueNone
                                                    ValueNone
                                                    ValueNone
                                                    (ValueSome failure)
                                                    ValueNone
                                with
                                | :? OperationCanceledException when
                                    runCancellationToken.IsCancellationRequested
                                    && not abandonmentCts.Token.IsCancellationRequested
                                    && not terminalEventWritten
                                    ->
                                    do! emitRunCancelled ()
                                | ex when not watcherToken.IsCancellationRequested && not terminalEventWritten ->
                                    let failure =
                                        FailureFactory.workflow
                                            runtimeRunId
                                            ValueNone
                                            "The workflow event stream failed."
                                            (ValueSome ex)

                                    do!
                                        emit
                                            RunEventKind.RunFailed
                                            ValueNone
                                            ValueNone
                                            ValueNone
                                            (ValueSome failure)
                                            ValueNone
                            finally
                                try
                                    inner.DisposeAsync().AsTask().GetAwaiter().GetResult()
                                with _ ->
                                    ()

                                channel.Writer.TryComplete() |> ignore
                        }

                    background |> ignore

                    ChannelAsyncEnumerator(channel.Reader, background, abandonmentCts, watcherCts)
                    :> IAsyncEnumerator<RunEvent<'Output>> }

        let respondAsync (response: ApprovalResponse, cancellationToken: CancellationToken) =
            ApprovalResponseDispatch.sendAsync pending response cancellationToken (fun externalResponse ct ->
                streamingRun.SendResponseAsync(externalResponse).AsTask().WaitAsync(ct))
            |> ValueTask

        WorkflowRun<'Output>(
            runtimeRunId,
            eventStream,
            (fun (response, cancellationToken) -> respondAsync (response, cancellationToken)),
            (fun _cancellationToken -> ValueTask<WorkflowCheckpoint<'Output>>(createCheckpoint store streamingRun)),
            (fun () ->
                pending.Clear()
                streamingRun.DisposeAsync())
        )

    member _.Fingerprint = fingerprint

    member this.RunStreamingAsync(input: 'Input, options: WorkflowRunOptions, cancellationToken: CancellationToken) =
        task {
            let runtimeRunId = RunId.New()
            let workflow = buildWorkflow runtime definition runtimeRunId
            let store = MafJsonCheckpointStore()
            let environment = createEnvironment store
            let sessionId = options.SessionId |> ValueOption.toObj

            try
                let! streamingRun =
                    environment.RunStreamingAsync(workflow, input, sessionId, cancellationToken).AsTask()

                return createRunWrapper runtimeRunId store streamingRun cancellationToken
            with ex ->
                return raise (InvalidOperationException(ex.ToString(), ex))
        }

    member this.StartAsync(input: 'Input, options: WorkflowRunOptions, cancellationToken: CancellationToken) =
        this.RunStreamingAsync(input, options, cancellationToken)

    member this.ResumeAsync(checkpoint: WorkflowCheckpoint<'Output>, cancellationToken: CancellationToken) =
        task {
            if checkpoint.DefinitionId <> definition.Id then
                invalidOp "The supplied workflow checkpoint does not match this workflow definition ID."

            if checkpoint.DefinitionVersion <> definition.Version then
                invalidOp "The supplied workflow checkpoint does not match this workflow definition version."

            if not (StringComparer.Ordinal.Equals(checkpoint.Fingerprint, fingerprint)) then
                invalidOp "The supplied workflow checkpoint does not match this workflow definition fingerprint."

            let runtimeRunId = RunId.New()
            let workflow = buildWorkflow runtime definition runtimeRunId
            let store = MafJsonCheckpointStore()
            let checkpointInfo = CheckpointInfo(checkpoint.SessionId, checkpoint.CheckpointId)
            store.Import(checkpoint.SessionId, checkpointInfo, checkpoint.Payload)
            let environment = createEnvironment store

            try
                let! streamingRun =
                    environment.ResumeStreamingAsync(workflow, checkpointInfo, cancellationToken).AsTask()

                return createRunWrapper runtimeRunId store streamingRun cancellationToken
            with ex ->
                return raise (InvalidOperationException(ex.ToString(), ex))
        }

    member this.RunAsync(input: 'Input, options: WorkflowRunOptions, cancellationToken: CancellationToken) =
        task {
            let startedAt = DateTimeOffset.UtcNow
            let disposeTimeout = TimeSpan.FromSeconds 5.0
            let! workflowRun = this.RunStreamingAsync(input, options, cancellationToken)
            let enumerator = workflowRun.Events.GetAsyncEnumerator(CancellationToken.None)

            try
                let! result =
                    task {
                        let mutable finalResult: CircuitResult<'Output> option = None
                        let mutable keepGoing = true

                        while keepGoing && finalResult.IsNone do
                            let! moved = enumerator.MoveNextAsync().AsTask()

                            if not moved then
                                keepGoing <- false
                            else
                                let event = enumerator.Current

                                match event.Kind with
                                | RunEventKind.RunCompleted ->
                                    finalResult <- Some(CircuitResult<'Output>.Success event.Value.Value)
                                | RunEventKind.RunFailed ->
                                    finalResult <- Some(CircuitResult<'Output>.Error event.Failure.Value)
                                | RunEventKind.ApprovalRequested ->
                                    finalResult <-
                                        Some(
                                            CircuitResult<'Output>
                                                .Error(
                                                    FailureFactory.approvalRequired
                                                        workflowRun.RunId
                                                        event.Approval.Value.RequestId
                                                )
                                        )
                                | _ -> ()

                        return
                            finalResult
                            |> Option.defaultWith (fun () ->
                                CircuitResult<'Output>
                                    .Error(
                                        FailureFactory.workflow
                                            workflowRun.RunId
                                            ValueNone
                                            "The workflow completed without producing a terminal output."
                                            ValueNone
                                    ))
                    }

                return RunResult(workflowRun.RunId, result, RunUsage(0, 0), ValueNone, startedAt, DateTimeOffset.UtcNow)
            finally
                try
                    enumerator.DisposeAsync().AsTask().WaitAsync(disposeTimeout).GetAwaiter().GetResult()
                with _ ->
                    ()

                try
                    (workflowRun :> IAsyncDisposable)
                        .DisposeAsync()
                        .AsTask()
                        .WaitAsync(disposeTimeout)
                        .GetAwaiter()
                        .GetResult()
                with _ ->
                    ()
        }

type internal MafWorkflowCompiler() =
    member _.Compile<'Input, 'Output>(definition: WorkflowDefinition<'Input, 'Output>, runtime: MafRuntime) =
        CompiledMafWorkflow<'Input, 'Output>(definition, runtime, definition.Fingerprint)

type internal WorkflowRuntimeDispatch =
    static member Run<'Input, 'Output>
        (
            runtime: MafRuntime,
            definition: WorkflowDefinition<'Input, 'Output>,
            input: 'Input,
            options: WorkflowRunOptions,
            cancellationToken: CancellationToken
        ) =
        MafWorkflowCompiler().Compile(definition, runtime).RunAsync(input, options, cancellationToken)

    static member Start<'Input, 'Output>
        (
            runtime: MafRuntime,
            definition: WorkflowDefinition<'Input, 'Output>,
            input: 'Input,
            options: WorkflowRunOptions,
            cancellationToken: CancellationToken
        ) =
        MafWorkflowCompiler().Compile(definition, runtime).StartAsync(input, options, cancellationToken)

    static member Resume<'Input, 'Output>
        (
            runtime: MafRuntime,
            definition: WorkflowDefinition<'Input, 'Output>,
            checkpoint: WorkflowCheckpoint<'Output>,
            cancellationToken: CancellationToken
        ) =
        MafWorkflowCompiler().Compile(definition, runtime).ResumeAsync(checkpoint, cancellationToken)
