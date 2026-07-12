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

    type private PendingApproval =
        { PublicId: string
          Request: ToolApprovalRequestContent
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

    let start<'Input, 'Output>
        (runtime: MafRuntime)
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        (runOptions: RunOptions)
        (startToken: CancellationToken)
        : Task<AgentRun<'Output>> =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        if isNull (box runOptions) then
            nullArg "options"

        let runId = RunId.New()
        let startedAt = DateTimeOffset.UtcNow
        let runContext = runtime.CreateRunContext(runId, agent, signature, runOptions)
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

        let observerSession =
            MafObserver.createAgentRunSession
                runtime.RuntimeOptions.Observers
                runId
                agent.Name
                signature.Id
                signature.Version
                (runtime.ResolveRequestModel agent)
                runOptions.Services

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

        let emit kind operationId value failure approval =
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

                    let forceCancellationTerminal () =
                        let mutable dropped = Unchecked.defaultof<RunEvent<'Output>>
                        let mutable terminalWritten = channel.Writer.TryWrite(event)

                        while not terminalWritten && channel.Reader.TryRead(&dropped) do
                            terminalWritten <- channel.Writer.TryWrite(event)

                        if not terminalWritten then
                            // A competing consumer can create capacity after TryWrite fails but before TryRead.
                            terminalWritten <- channel.Writer.TryWrite(event)

                        terminalWritten

                    let! written =
                        task {
                            if channel.Writer.TryWrite(event) then
                                return true
                            elif kind = RunEventKind.RunFailed && lifetimeToken.IsCancellationRequested then
                                return forceCancellationTerminal ()
                            else
                                try
                                    do! channel.Writer.WriteAsync(event, lifetimeToken).AsTask()
                                    return true
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

        let fail failure =
            emit RunEventKind.RunFailed ValueNone ValueNone (ValueSome failure) ValueNone

        let mapResponse (response: AgentResponse) =
            task {
                let approvals = ResizeArray<ToolApprovalRequestContent>()

                for content in contents response do
                    match MafStreaming.StreamingMappedEvent.tryMapContent signature.JsonSerializerOptions content with
                    | ValueSome(MafStreaming.StreamingMappedEvent.ToolStarted operationId) ->
                        do! emit RunEventKind.ToolStarted operationId ValueNone ValueNone ValueNone
                    | ValueSome(MafStreaming.StreamingMappedEvent.ToolCompleted operationId) ->
                        do! emit RunEventKind.ToolCompleted operationId ValueNone ValueNone ValueNone
                    | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(_operationId, _request)) ->
                        approvals.Add(content :?> ToolApprovalRequestContent)
                    | ValueNone -> ()

                return approvals.ToArray()
            }

        let awaitResponses round (requests: ToolApprovalRequestContent array) =
            task {
                let completion =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                let pendingRound =
                    requests
                    |> Array.mapi (fun ordinal request ->
                        { PublicId = $"{runId.Value}:approval:{round}:{ordinal}"
                          Request = request
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
                    let providerApproval =
                        MafStreaming.createApprovalRequest signature.JsonSerializerOptions item.Request

                    let approval =
                        ApprovalRequest(item.PublicId, providerApproval.ToolName, providerApproval.ArgumentsJson)

                    do!
                        emit
                            RunEventKind.ApprovalRequested
                            (MafStreaming.tryGetOperationId item.Request.ToolCall)
                            ValueNone
                            ValueNone
                            (ValueSome approval)

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
                                                    repaired <- repaired || MafStructuredOutput.wasRepaired response

                                                    let roundMetadata =
                                                        runtime.CreateDiagnosticMetadata(runOptions, response)

                                                    if roundMetadata.Count > 0 then
                                                        diagnosticMetadata <- roundMetadata

                                                let createResponseMessage (storedRequests: PendingApproval array) =
                                                    let responseContents = ResizeArray<AIContent>()

                                                    lock gate (fun () ->
                                                        for item in storedRequests do
                                                            match responseDecisions.TryGetValue item.PublicId with
                                                            | true, struct (approved, note) ->
                                                                responseContents.Add(
                                                                    item.Request.CreateResponse(approved, note)
                                                                    :> AIContent
                                                                )
                                                            | false, _ ->
                                                                invalidOp "An approval decision was not recorded."

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
                                                    updateRoundMetadata response
                                                    let! requests = mapResponse response

                                                    if requests.Length > 0 then
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
                                                            let! storedRequests = awaitResponses round requests
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
                member _.Start(runtime, agent, signature, input, runOptions, cancellationToken) =
                    MafInteractive.start (runtime :?> MafRuntime) agent signature input runOptions cancellationToken }
