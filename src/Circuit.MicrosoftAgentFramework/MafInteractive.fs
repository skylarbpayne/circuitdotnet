namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

/// Non-streaming interactive runs use one compiled MAF agent and one provider session for every
/// approval round. They intentionally do not promise token-level OutputDelta events.
module internal MafInteractive =
    [<Literal>]
    let internal MaxApprovalRounds = 16

    [<Literal>]
    let internal MaxApprovalsPerRound = 16

    let mutable internal ApprovalPreparedForTesting: (string -> unit) voption =
        ValueNone

    let mutable internal EventBackpressureForTesting: (unit -> unit) voption = ValueNone

    type private PendingApproval =
        { PublicId: string
          Request: ToolApprovalRequestContent
          mutable Published: bool
          mutable Responded: bool }

    [<Sealed>]
    type private PersistentChannelEnumerable<'T>(reader: ChannelReader<'T>) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                let mutable current = Unchecked.defaultof<'T>

                { new IAsyncEnumerator<'T> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        let rec moveNext () =
                            task {
                                let! available = reader.WaitToReadAsync(cancellationToken).AsTask()

                                if not available then
                                    return false
                                else
                                    let mutable item = Unchecked.defaultof<'T>

                                    if reader.TryRead(&item) then
                                        current <- item
                                        return true
                                    else
                                        return! moveNext ()
                            }

                        ValueTask<bool>(moveNext ())

                    member _.DisposeAsync() = ValueTask() }

    let private contents (response: AgentResponse) =
        seq {
            if not (isNull response.Messages) then
                for message in response.Messages do
                    if not (isNull message.Contents) then
                        yield! message.Contents
        }

    let internal collectBoundedApprovals (response: AgentResponse) =
        let approvals =
            contents response
            |> Seq.choose (function
                | :? ToolApprovalRequestContent as approval -> Some approval
                | _ -> None)
            |> Seq.truncate (MaxApprovalsPerRound + 1)
            |> Seq.toArray

        struct (approvals, approvals.Length > MaxApprovalsPerRound)

    let start<'Input, 'Output>
        (runtime: MafRuntime)
        (runId: RunId)
        (nodePath: string)
        (idempotencyKey: string)
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        (runOptions: RunOptions)
        (onSession: CircuitSession -> Task)
        (startToken: CancellationToken)
        : Task<AgentRun<'Output>> =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        if isNull (box runOptions) then
            nullArg "options"

        let startedAt = DateTimeOffset.UtcNow

        let runContext =
            runtime.CreateScheduledRunContext(runId, nodePath, idempotencyKey, agent, signature, runOptions)

        let channelOptions = BoundedChannelOptions(MafStreaming.StreamBufferCapacity)
        channelOptions.AllowSynchronousContinuations <- false
        channelOptions.FullMode <- BoundedChannelFullMode.Wait
        channelOptions.SingleReader <- false
        channelOptions.SingleWriter <- true
        let channel = Channel.CreateBounded<RunEvent<'Output>>(channelOptions)
        let lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(startToken)
        let lifetimeToken = lifetimeCts.Token
        let gate = obj ()
        let pending = Dictionary<string, PendingApproval>(StringComparer.Ordinal)

        let responseDecisions =
            Dictionary<string, struct (bool * string)>(StringComparer.Ordinal)

        let mutable pendingCompletion: TaskCompletionSource<unit> = null
        let mutable sequence = -1L
        let mutable terminal = false
        let mutable disposed = false
        let mutable background: Task = Task.CompletedTask

        // The Core scheduler owns the single Circuit root observer session. Tool events
        // correlate through the supplied scheduler run id.
        let observerSession: MafObserver.ObserverSession voption = ValueNone

        let mutable usageDetails: UsageDetails = null
        let mutable usage = RunUsage(0, 0)
        let mutable repaired = false
        let mutable resultSession = runOptions.Session
        let mutable diagnosticMetadata = MafErrors.emptyDiagnosticMetadata

        let updateUsage (response: AgentResponse) =
            let details =
                match MafStructuredOutput.tryGetOriginalUsage response with
                | ValueSome original -> MafErrors.combineUsageDetails original response.Usage
                | ValueNone -> response.Usage

            usageDetails <- MafErrors.combineUsageDetails usageDetails details
            usage <- MafErrors.createUsage usageDetails

        let emitCore kind operationId value failure approval onWrittenUnderGate =
            task {
                let shouldWrite = lock gate (fun () -> not terminal)

                if shouldWrite then
                    if kind = RunEventKind.RunCompleted && lifetimeToken.IsCancellationRequested then
                        lifetimeToken.ThrowIfCancellationRequested()

                    sequence <- sequence + 1L

                    let event =
                        RunEvent(
                            sequence,
                            runId,
                            DateTimeOffset.UtcNow,
                            kind,
                            operationId,
                            ValueNone,
                            value,
                            failure,
                            approval
                        )

                    let tryWrite () =
                        lock gate (fun () ->
                            let written = channel.Writer.TryWrite(event)

                            if written then
                                onWrittenUnderGate ()

                            written)

                    let forceCancellationTerminal () =
                        let mutable dropped = Unchecked.defaultof<RunEvent<'Output>>
                        let mutable terminalWritten = tryWrite ()

                        while not terminalWritten && channel.Reader.TryRead(&dropped) do
                            terminalWritten <- tryWrite ()

                        if not terminalWritten then
                            // A competing consumer can create capacity after TryWrite fails but before TryRead.
                            terminalWritten <- tryWrite ()

                        terminalWritten

                    let! written =
                        task {
                            if tryWrite () then
                                return true
                            elif kind = RunEventKind.RunFailed && lifetimeToken.IsCancellationRequested then
                                return forceCancellationTerminal ()
                            else
                                match EventBackpressureForTesting with
                                | ValueSome backpressured -> backpressured ()
                                | ValueNone -> ()

                                try
                                    let! canWrite = channel.Writer.WaitToWriteAsync(lifetimeToken).AsTask()
                                    return canWrite && tryWrite ()
                                with :? OperationCanceledException when
                                    kind = RunEventKind.RunFailed && lifetimeToken.IsCancellationRequested ->
                                    return forceCancellationTerminal ()
                        }

                    if written then
                        if kind = RunEventKind.RunCompleted || kind = RunEventKind.RunFailed then
                            lock gate (fun () -> terminal <- true)

                        match kind with
                        | RunEventKind.ApprovalRequested ->
                            match approval with
                            | ValueSome request ->
                                let operationId = operationId |> ValueOption.defaultValue request.RequestId

                                do!
                                    MafObserver.notifyApprovalRequestedAsync
                                        observerSession
                                        operationId
                                        request.ToolName
                                        request
                                        lifetimeToken
                            | ValueNone -> ()
                        | RunEventKind.RunCompleted
                        | RunEventKind.RunFailed ->
                            do!
                                MafObserver.notifyRootEventAsync
                                    observerSession
                                    kind
                                    ValueNone
                                    (value |> ValueOption.bind (runtime.TrySerializeOutputPayload signature))
                                    failure
                                    approval
                                    (ValueSome startedAt)
                                    (ValueSome DateTimeOffset.UtcNow)
                                    repaired
                                    (ValueSome usage)
                                    resultSession
                                    diagnosticMetadata
                                    lifetimeToken
                        | _ -> ()
            }

        let emit kind operationId value failure approval =
            emitCore kind operationId value failure approval ignore

        let emitApproval item operationId approval =
            emitCore RunEventKind.ApprovalRequested operationId ValueNone ValueNone (ValueSome approval) (fun () ->
                item.Published <- true)

        let fail failure =
            emit RunEventKind.RunFailed ValueNone ValueNone (ValueSome failure) ValueNone

        let mapResponse (response: AgentResponse) =
            task {
                let struct (approvals, overApprovalLimit) = collectBoundedApprovals response

                for content in contents response do
                    match MafStreaming.StreamingMappedEvent.tryMapContent signature.JsonSerializerOptions content with
                    | ValueSome(MafStreaming.StreamingMappedEvent.ToolStarted operationId) ->
                        do! emit RunEventKind.ToolStarted operationId ValueNone ValueNone ValueNone
                    | ValueSome(MafStreaming.StreamingMappedEvent.ToolCompleted operationId) ->
                        do! emit RunEventKind.ToolCompleted operationId ValueNone ValueNone ValueNone
                    | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested _)
                    | ValueNone -> ()

                return struct (approvals, overApprovalLimit)
            }

        let awaitResponses (requests: ToolApprovalRequestContent array) =
            task {
                let completion =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                let pendingRound =
                    requests
                    |> Array.map (fun request ->
                        { PublicId = Guid.NewGuid().ToString("N")
                          Request = request
                          Published = false
                          Responded = false })

                lock gate (fun () ->
                    pending.Clear()
                    responseDecisions.Clear()

                    for item in pendingRound do
                        if String.IsNullOrWhiteSpace item.Request.RequestId then
                            invalidOp "MAF returned an invalid approval request identifier."

                        pending[item.PublicId] <- item

                    pendingCompletion <- completion)

                for item in pendingRound do
                    match ApprovalPreparedForTesting with
                    | ValueSome prepared -> prepared item.PublicId
                    | ValueNone -> ()

                    let providerApproval =
                        MafStreaming.createApprovalRequest runtime.RuntimeOptions.JsonSerializerOptions item.Request

                    let argumentsJson =
                        if runOptions.SensitiveDataMode = SensitiveDataMode.Standard then
                            providerApproval.ArgumentsJson
                        else
                            ValueNone

                    let approval =
                        ApprovalRequest(item.PublicId, providerApproval.ToolName, argumentsJson)

                    do! emitApproval item (MafStreaming.tryGetOperationId item.Request.ToolCall) approval

                do! completion.Task.WaitAsync(lifetimeToken)

                return
                    lock gate (fun () ->
                        let responses = pending.Values |> Seq.toArray

                        pending.Clear()
                        pendingCompletion <- null
                        responses)
            }

        let pump =
            task {
                try
                    do!
                        MafObserver.notifyStartedAsync
                            observerSession
                            startedAt
                            (ValueSome(runtime.CreatePrompt(agent, signature)))
                            (runtime.TrySerializeInputPayload signature input)
                            lifetimeToken

                    do! emit RunEventKind.RunStarted ValueNone ValueNone ValueNone ValueNone

                    if lifetimeToken.IsCancellationRequested then
                        do! fail (MafErrors.cancelledFailure runId "The run was cancelled before it started." ValueNone)
                    else
                        let inputIssues = signature.Input.Validate input

                        if inputIssues.Count > 0 then
                            do! fail (MafErrors.validationFailure runId (MafErrors.formatValidationIssues inputIssues))
                        else
                            let enableRepair =
                                runOptions.StructuredOutputPolicy = StructuredOutputPolicy.AllowSecondaryModelRepair

                            if enableRepair && runtime.RuntimeOptions.SecondaryStructuredOutputClient.IsNone then
                                do!
                                    fail (
                                        MafErrors.structuredOutputUnsupportedFailure
                                            runId
                                            "Structured output repair requires a configured secondary structured output chat client."
                                            ValueNone
                                            ValueNone
                                    )
                            else
                                let! capabilities =
                                    runtime.ResolveCapabilitiesAsync runId runContext agent lifetimeToken

                                match capabilities with
                                | Error failure -> do! fail failure
                                | Ok(tools, skills) ->
                                    let sessionBinding =
                                        MafSessionContracts.createSessionBinding runContext signature tools skills

                                    let compiledResult =
                                        try
                                            Ok(
                                                MafAgentFactory.createAgent
                                                    runtime.ChatClient
                                                    runtime.RuntimeOptions
                                                    runContext
                                                    agent
                                                    signature
                                                    tools
                                                    skills
                                                    false
                                            )
                                        with ex ->
                                            Error(
                                                MafErrors.skillFailure
                                                    runId
                                                    "Skill initialization failed."
                                                    (ValueSome ex)
                                            )

                                    match compiledResult with
                                    | Error failure -> do! fail failure
                                    | Ok compiled ->
                                        use compiled = compiled

                                        let! sessionResult =
                                            runtime.PrepareSessionAsync
                                                runId
                                                compiled.Agent
                                                agent
                                                sessionBinding
                                                runOptions
                                                lifetimeToken

                                        match sessionResult with
                                        | Error failure -> do! fail failure
                                        | Ok(session, wrappedSession) ->
                                            resultSession <- wrappedSession

                                            match wrappedSession with
                                            | ValueSome activeSession -> do! onSession activeSession
                                            | ValueNone -> ()

                                            match
                                                runtime.TryCreateInputEnvelope runId signature input lifetimeToken
                                            with
                                            | Error failure -> do! fail failure
                                            | Ok envelope ->
                                                let responseFormat, wrapped =
                                                    MafStreaming.createWrappedResponseFormat signature

                                                let rawRunOptions = AgentRunOptions(ResponseFormat = responseFormat)

                                                let updateRoundMetadata (response: AgentResponse) =
                                                    updateUsage response

                                                    diagnosticMetadata <-
                                                        runtime.CreateDiagnosticMetadata(runOptions, response)

                                                let createResponseMessage (storedRequests: PendingApproval array) =
                                                    let responseContents = ResizeArray<AIContent>()

                                                    lock gate (fun () ->
                                                        for item in storedRequests do
                                                            let struct (approved, note) =
                                                                responseDecisions[item.PublicId]

                                                            responseContents.Add(
                                                                item.Request.CreateResponse(approved, note)
                                                                :> AIContent
                                                            )

                                                        responseDecisions.Clear())

                                                    ChatMessage(ChatRole.User, responseContents :> IList<AIContent>)

                                                let! first =
                                                    compiled.Agent.RunAsync(
                                                        envelope,
                                                        session,
                                                        rawRunOptions,
                                                        lifetimeToken
                                                    )

                                                let mutable response: AgentResponse = first
                                                let mutable round = 0

                                                let mutable outputResult =
                                                    Unchecked.defaultof<Result<'Output, CircuitFailure>>

                                                let mutable roundCompleted = false

                                                while not roundCompleted do
                                                    // A provider round has completed and the session is quiescent.
                                                    // Publish a codec-safe snapshot before approvals are exposed.
                                                    match wrappedSession with
                                                    | ValueSome activeSession -> do! onSession activeSession
                                                    | ValueNone -> ()

                                                    updateRoundMetadata response
                                                    let! struct (requests, overApprovalLimit) = mapResponse response

                                                    if overApprovalLimit then
                                                        outputResult <-
                                                            Error(
                                                                MafErrors.toolFailure
                                                                    runId
                                                                    $"The provider returned more than {MaxApprovalsPerRound} approval requests in one round."
                                                                    ValueNone
                                                            )

                                                        roundCompleted <- true
                                                    elif requests.Length > 0 then
                                                        if round >= MaxApprovalRounds then
                                                            outputResult <-
                                                                Error(
                                                                    MafErrors.toolFailure
                                                                        runId
                                                                        $"The interactive run exceeded the maximum of {MaxApprovalRounds} approval rounds."
                                                                        ValueNone
                                                                )

                                                            roundCompleted <- true
                                                        else
                                                            let! storedRequests = awaitResponses requests
                                                            round <- round + 1
                                                            let responseMessage = createResponseMessage storedRequests

                                                            let! next =
                                                                compiled.Agent.RunAsync<'Output>(
                                                                    [ responseMessage ],
                                                                    session,
                                                                    signature.JsonSerializerOptions,
                                                                    null,
                                                                    lifetimeToken
                                                                )

                                                            response <- next
                                                    else
                                                        let decode text =
                                                            MafRuntimeInternals.decodeResponseResult
                                                                runId
                                                                lifetimeToken
                                                                signature
                                                                (fun () ->
                                                                    MafStreaming.deserializeOutput
                                                                        signature
                                                                        wrapped
                                                                        text)

                                                        let nativeResult = decode response.Text

                                                        match nativeResult, runOptions.StructuredOutputPolicy with
                                                        | Error failure,
                                                          StructuredOutputPolicy.AllowSecondaryModelRepair when
                                                            failure.Code = CircuitFailureCode.Decode
                                                            ->
                                                            let secondaryClient =
                                                                runtime.RuntimeOptions.SecondaryStructuredOutputClient.Value

                                                            let! repairedText, repairUsage =
                                                                MafStreaming.repairAsync
                                                                    secondaryClient
                                                                    responseFormat
                                                                    response.Text
                                                                    lifetimeToken

                                                            usageDetails <-
                                                                MafErrors.combineUsageDetails usageDetails repairUsage

                                                            usage <- MafErrors.createUsage usageDetails
                                                            repaired <- true

                                                            let entries =
                                                                Dictionary<string, string>(StringComparer.Ordinal)

                                                            entries["circuit.repaired"] <- "true"

                                                            if
                                                                runOptions.SensitiveDataMode = SensitiveDataMode.Standard
                                                            then
                                                                entries["circuit.repair.originalResponse"] <-
                                                                    response.Text

                                                            diagnosticMetadata <-
                                                                entries :> IReadOnlyDictionary<string, string>

                                                            outputResult <- decode repairedText
                                                        | result, _ -> outputResult <- result

                                                        roundCompleted <- true

                                                match outputResult with
                                                | Error failure -> do! fail failure
                                                | Ok output ->
                                                    do!
                                                        emit
                                                            RunEventKind.RunCompleted
                                                            ValueNone
                                                            (ValueSome output)
                                                            ValueNone
                                                            ValueNone
                with
                | ex when MafErrors.isCancellationRequested lifetimeToken ex ->
                    do! fail (MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
                | ex -> do! fail (MafRuntimeInternals.classifyProviderExecutionFailure runId lifetimeToken ex)

            }

        background <-
            task {
                try
                    do! pump
                finally
                    lock gate (fun () ->
                        pending.Clear()
                        responseDecisions.Clear()
                        pendingCompletion <- null)

                    MafObserver.unregisterSession observerSession
                    channel.Writer.TryComplete() |> ignore
            }

        let respond (response: ApprovalResponse, cancellationToken: CancellationToken) =
            ValueTask(
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    let completion =
                        lock gate (fun () ->
                            if disposed then
                                raise (ObjectDisposedException("AgentRun"))

                            match pending.TryGetValue response.RequestId with
                            | false, _ ->
                                invalidOp "There is no pending approval request with the supplied identifier."
                            | true, request when not request.Published ->
                                invalidOp "The approval request is not accepting responses."
                            | true, request when request.Responded ->
                                invalidOp "The approval request has already been answered."
                            | true, request ->
                                request.Responded <- true
                                responseDecisions[response.RequestId] <- struct (response.Approved, response.Note)

                                if pending.Values |> Seq.forall _.Responded then
                                    pendingCompletion
                                else
                                    null)

                    if not (isNull completion) then
                        completion.TrySetResult() |> ignore
                }
            )

        let dispose () =
            ValueTask(
                task {
                    lock gate (fun () -> disposed <- true)
                    lifetimeCts.Cancel()

                    try
                        do! background
                    with _ ->
                        ()

                    lifetimeCts.Dispose()
                }
            )

        let events =
            PersistentChannelEnumerable<RunEvent<'Output>>(channel.Reader) :> IAsyncEnumerable<_>

        let handle =
            AgentRun<'Output>
                .Create(runId, events, Func<_, _, _>(fun response ct -> respond (response, ct)), Func<_>(dispose))

        Task.FromResult handle

[<AbstractClass; Sealed>]
type internal MafInteractiveRegistration =
    static do
        MafRuntimeInteractiveDispatch.Dispatcher <-
            { new IMafRuntimeInteractiveDispatcher with
                member _.Start
                    (
                        runtime,
                        runId,
                        nodePath,
                        idempotencyKey,
                        agent,
                        signature,
                        input,
                        runOptions,
                        onSession,
                        cancellationToken
                    ) =
                    MafInteractive.start
                        (runtime :?> MafRuntime)
                        runId
                        nodePath
                        idempotencyKey
                        agent
                        signature
                        input
                        runOptions
                        onSession
                        cancellationToken }
