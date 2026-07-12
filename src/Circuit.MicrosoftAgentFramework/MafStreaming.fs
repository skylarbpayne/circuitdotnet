namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

module internal MafStreaming =
    [<Literal>]
    let StreamBufferCapacity = 64

    let internal tryGetCallId (callId: string) =
        if String.IsNullOrWhiteSpace callId then
            ValueNone
        else
            ValueSome callId

    let internal createWrappedResponseFormat<'Input, 'Output> (signature: Signature<'Input, 'Output>) =
        let responseFormat =
            ChatResponseFormat.ForJsonSchema<'Output>(
                signature.JsonSerializerOptions,
                signature.Id.Value,
                signature.Description
            )

        let outputSchema = signature.Output.Schema.RootElement

        if outputSchema.ValueKind = JsonValueKind.Object then
            responseFormat :> ChatResponseFormat, false
        else
            let wrapper = JsonObject()
            wrapper["type"] <- JsonValue.Create("object")
            wrapper["additionalProperties"] <- JsonValue.Create(false)

            let properties = JsonObject()
            properties["data"] <- JsonNode.Parse(outputSchema.GetRawText())
            wrapper["properties"] <- properties
            wrapper["required"] <- JsonArray(JsonValue.Create("data"))

            use document = JsonDocument.Parse(wrapper.ToJsonString())

            ChatResponseFormat.ForJsonSchema(document.RootElement.Clone(), signature.Id.Value, signature.Description)
            :> ChatResponseFormat,
            true

    let internal deserializeOutput<'Input, 'Output>
        (signature: Signature<'Input, 'Output>)
        (wrapped: bool)
        (text: string)
        =
        let json =
            if wrapped then
                use document = JsonDocument.Parse(text)
                let mutable data = Unchecked.defaultof<JsonElement>

                if document.RootElement.TryGetProperty("data", &data) then
                    data.GetRawText()
                else
                    raise (JsonException("The structured output wrapper did not contain a 'data' property."))
            else
                text

        let value =
            JsonSerializer.Deserialize<'Output>(json, signature.JsonSerializerOptions)

        if isNull (box value) then
            raise (JsonException("The deserialized response is null."))

        value

    let internal tryGetOperationId (toolCall: ToolCallContent) =
        if isNull toolCall then
            ValueNone
        else
            tryGetCallId toolCall.CallId

    let internal trySerializeApprovalArguments
        (jsonOptions: JsonSerializerOptions)
        (arguments: IDictionary<string, obj>)
        =
        if isNull arguments then
            ValueNone
        else
            try
                ValueSome(JsonSerializer.Serialize(arguments, jsonOptions))
            with _ ->
                ValueNone

    let internal createApprovalRequest (jsonOptions: JsonSerializerOptions) (approval: ToolApprovalRequestContent) =
        match approval.ToolCall with
        | :? FunctionCallContent as functionCall ->
            let toolName =
                if isNull functionCall || String.IsNullOrWhiteSpace functionCall.Name then
                    "unknown-tool-call"
                else
                    functionCall.Name

            ApprovalRequest(
                approval.RequestId,
                toolName,
                trySerializeApprovalArguments jsonOptions functionCall.Arguments
            )
        | _ -> ApprovalRequest(approval.RequestId, "unknown-tool-call", ValueNone)

    type internal StreamingMappedEvent =
        | ToolStarted of string voption
        | ToolCompleted of string voption
        | ApprovalRequested of string voption * ApprovalRequest

    [<RequireQualifiedAccess>]
    module internal StreamingMappedEvent =
        let tryMapContent (jsonOptions: JsonSerializerOptions) (content: AIContent) =
            match content with
            | :? FunctionCallContent as functionCall ->
                ValueSome(StreamingMappedEvent.ToolStarted(tryGetCallId functionCall.CallId))
            | :? FunctionResultContent as functionResult ->
                ValueSome(StreamingMappedEvent.ToolCompleted(tryGetCallId functionResult.CallId))
            | :? ToolApprovalRequestContent as approval ->
                ValueSome(
                    StreamingMappedEvent.ApprovalRequested(
                        tryGetOperationId approval.ToolCall,
                        createApprovalRequest jsonOptions approval
                    )
                )
            | _ -> ValueNone

        let isTerminal kind =
            kind = RunEventKind.RunCompleted || kind = RunEventKind.RunFailed

        let shouldSuppressTerminal terminalEventWritten kind = terminalEventWritten && isTerminal kind

    let internal tryUpdateUsage
        (usageDetails: byref<UsageDetails>)
        (usage: byref<RunUsage>)
        (update: AgentResponseUpdate)
        =
        for content in update.Contents do
            match content with
            | :? UsageContent as usageContent when not (isNull usageContent.Details) ->
                usageDetails <- usageContent.Details
                usage <- MafErrors.createUsage usageContent.Details
            | _ -> ()

    let internal repairAsync
        (secondaryClient: IChatClient)
        (responseFormat: ChatResponseFormat)
        (text: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let messages =
                [ ChatMessage(ChatRole.System, MafStructuredOutput.RepairSystemMessage)
                  ChatMessage(ChatRole.User, text) ]

            let chatOptions = ChatOptions()
            chatOptions.ResponseFormat <- responseFormat
            let! repaired = secondaryClient.GetResponseAsync(messages, chatOptions, cancellationToken)
            return repaired.Text, repaired.Usage
        }

    let internal decodeFinalOutput<'Input, 'Output>
        (runId: RunId)
        (cancellationToken: CancellationToken)
        (signature: Signature<'Input, 'Output>)
        (wrapped: bool)
        (text: string)
        =
        try
            let output = deserializeOutput signature wrapped text
            let outputIssues = signature.Output.Validate output

            if outputIssues.Count > 0 then
                Error(MafErrors.validationFailure runId (MafErrors.formatValidationIssues outputIssues))
            else
                Ok output
        with
        | ex when MafErrors.isCancellationRequested cancellationToken ex ->
            Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
        | ex when MafErrors.isStructuredOutputUnsupported ex ->
            Error(
                MafErrors.structuredOutputUnsupportedFailure
                    runId
                    "Structured output is not supported for this run."
                    ValueNone
                    (ValueSome ex)
            )
        | ex when MafErrors.isDecodeFailure ex ->
            Error(MafErrors.decodeFailure runId "The provider response could not be decoded." ValueNone (ValueSome ex))
        | ex -> Error(MafErrors.providerFailure runId "The provider request failed." ValueNone (ValueSome ex))

    let private createBoundedChannel<'T> () =
        let options = BoundedChannelOptions(StreamBufferCapacity)
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
            providerCts: CancellationTokenSource
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

                        providerCts.Dispose()
                        abandonmentCts.Dispose()
                    }
                )

    [<Sealed>]
    type private StreamingRunEnumerable<'Input, 'Output>
        (
            runtime: MafRuntime,
            agent: AgentDefinition,
            signature: Signature<'Input, 'Output>,
            input: 'Input,
            runOptions: RunOptions,
            jsonOptions: JsonSerializerOptions,
            cancellationToken: CancellationToken
        ) =
        interface IAsyncEnumerable<RunEvent<'Output>> with
            member _.GetAsyncEnumerator(enumeratorCancellationToken: CancellationToken) =
                let abandonmentCts = new CancellationTokenSource()

                let providerCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        enumeratorCancellationToken,
                        abandonmentCts.Token
                    )

                let deliveryToken = abandonmentCts.Token
                let providerToken = providerCts.Token
                let channel = createBoundedChannel<RunEvent<'Output>> ()
                let runId = RunId.New()
                let startedAt = DateTimeOffset.UtcNow
                let runContext = runtime.CreateRunContext(runId, agent, signature, runOptions)

                let observerSession =
                    MafObserver.createAgentRunSession
                        runtime.RuntimeOptions.Observers
                        runId
                        agent.Name
                        signature.Id
                        signature.Version
                        (runtime.ResolveRequestModel agent)
                        runOptions.Services

                let prompt = ValueSome(runtime.CreatePrompt(agent, signature))
                let inputPayload = runtime.TrySerializeInputPayload signature input

                let background: Task =
                    task {
                        let mutable sequence = -1L
                        let mutable usageDetails: UsageDetails = null
                        let mutable usage = RunUsage(0, 0)
                        let mutable repaired = false
                        let mutable resultSession = runOptions.Session
                        let mutable failureForObservers: CircuitFailure voption = ValueNone
                        let mutable diagnosticMetadata = MafErrors.emptyDiagnosticMetadata
                        let mutable terminalEventWritten = false

                        let emit
                            (kind: RunEventKind)
                            (operationId: string voption)
                            (textDelta: string voption)
                            (value: 'Output voption)
                            (failure: CircuitFailure voption)
                            (approval: ApprovalRequest voption)
                            =
                            task {
                                if StreamingMappedEvent.shouldSuppressTerminal terminalEventWritten kind then
                                    ()
                                else
                                    if StreamingMappedEvent.isTerminal kind then
                                        terminalEventWritten <- true

                                    sequence <- sequence + 1L

                                    let event =
                                        RunEvent(
                                            sequence,
                                            runId,
                                            DateTimeOffset.UtcNow,
                                            kind,
                                            operationId,
                                            textDelta,
                                            value,
                                            failure,
                                            approval
                                        )

                                    do!
                                        match kind with
                                        | RunEventKind.RunStarted -> Task.CompletedTask
                                        | RunEventKind.OutputDelta ->
                                            MafObserver.notifyRootEventAsync
                                                observerSession
                                                kind
                                                textDelta
                                                ValueNone
                                                failure
                                                approval
                                                ValueNone
                                                ValueNone
                                                false
                                                ValueNone
                                                ValueNone
                                                MafErrors.emptyDiagnosticMetadata
                                                providerToken
                                        | RunEventKind.ApprovalRequested ->
                                            match approval with
                                            | ValueSome approvalValue ->
                                                let approvalOperationId =
                                                    match operationId with
                                                    | ValueSome value -> value
                                                    | ValueNone -> approvalValue.RequestId

                                                MafObserver.notifyApprovalRequestedAsync
                                                    observerSession
                                                    approvalOperationId
                                                    approvalValue.ToolName
                                                    approvalValue
                                                    providerToken
                                            | ValueNone -> Task.CompletedTask
                                        | RunEventKind.RunCompleted ->
                                            MafObserver.notifyRootEventAsync
                                                observerSession
                                                kind
                                                ValueNone
                                                (match value with
                                                 | ValueSome output ->
                                                     runtime.TrySerializeOutputPayload signature output
                                                 | ValueNone -> ValueNone)
                                                failure
                                                approval
                                                (ValueSome startedAt)
                                                (ValueSome DateTimeOffset.UtcNow)
                                                repaired
                                                (ValueSome usage)
                                                resultSession
                                                diagnosticMetadata
                                                providerToken
                                        | RunEventKind.RunFailed ->
                                            MafObserver.notifyRootEventAsync
                                                observerSession
                                                kind
                                                ValueNone
                                                ValueNone
                                                failure
                                                approval
                                                (ValueSome startedAt)
                                                (ValueSome DateTimeOffset.UtcNow)
                                                repaired
                                                (ValueSome usage)
                                                resultSession
                                                diagnosticMetadata
                                                providerToken
                                        | RunEventKind.ToolStarted
                                        | RunEventKind.ToolCompleted
                                        | RunEventKind.StepStarted
                                        | RunEventKind.StepCompleted
                                        | RunEventKind.IntermediateOutput
                                        | _ -> Task.CompletedTask

                                    let writeToken =
                                        match kind with
                                        | RunEventKind.RunStarted
                                        | RunEventKind.RunCompleted
                                        | RunEventKind.RunFailed -> deliveryToken
                                        | _ -> providerToken

                                    do! writeToChannelAsync channel.Writer event writeToken
                            }

                        let fail failure =
                            task {
                                failureForObservers <- ValueSome failure

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
                            do!
                                MafObserver.notifyStartedAsync
                                    observerSession
                                    startedAt
                                    prompt
                                    inputPayload
                                    providerToken

                            do! emit RunEventKind.RunStarted ValueNone ValueNone ValueNone ValueNone ValueNone

                            if providerToken.IsCancellationRequested then
                                do!
                                    fail (
                                        MafErrors.cancelledFailure
                                            runId
                                            "The run was cancelled before it started."
                                            ValueNone
                                    )
                            else
                                let inputIssues = signature.Input.Validate input

                                if inputIssues.Count > 0 then
                                    do!
                                        fail (
                                            MafErrors.validationFailure
                                                runId
                                                (MafErrors.formatValidationIssues inputIssues)
                                        )
                                else
                                    let enableSecondaryRepair =
                                        runOptions.StructuredOutputPolicy = StructuredOutputPolicy.AllowSecondaryModelRepair

                                    if
                                        enableSecondaryRepair
                                        && runtime.RuntimeOptions.SecondaryStructuredOutputClient.IsNone
                                    then
                                        do!
                                            fail (
                                                MafErrors.structuredOutputUnsupportedFailure
                                                    runId
                                                    "Structured output repair requires a configured secondary structured output chat client."
                                                    ValueNone
                                                    ValueNone
                                            )
                                    else
                                        let! capabilityResult =
                                            runtime.ResolveCapabilitiesAsync runId runContext agent providerToken

                                        match capabilityResult with
                                        | Error failure -> do! fail failure
                                        | Ok(tools, skills) ->
                                            let sessionBinding =
                                                MafSessionContracts.createSessionBinding
                                                    runContext
                                                    signature
                                                    tools
                                                    skills

                                            let runtimeAgentResult =
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

                                            match runtimeAgentResult with
                                            | Error failure -> do! fail failure
                                            | Ok runtimeAgent ->
                                                use runtimeAgent = runtimeAgent

                                                let! sessionResult =
                                                    runtime.PrepareSessionAsync
                                                        runId
                                                        runtimeAgent.Agent
                                                        agent
                                                        sessionBinding
                                                        runOptions
                                                        providerToken

                                                match sessionResult with
                                                | Error failure -> do! fail failure
                                                | Ok(providerSession, wrappedSession) ->
                                                    resultSession <- wrappedSession
                                                    let responseFormat, wrapped = createWrappedResponseFormat signature

                                                    match
                                                        runtime.TryCreateInputEnvelope
                                                            runId
                                                            signature
                                                            input
                                                            providerToken
                                                    with
                                                    | Error failure -> do! fail failure
                                                    | Ok inputEnvelope ->
                                                        let builder = StringBuilder()
                                                        let agentRunOptions = AgentRunOptions()

                                                        if
                                                            runOptions.StructuredOutputPolicy = StructuredOutputPolicy.NativeOnly
                                                        then
                                                            agentRunOptions.ResponseFormat <- responseFormat

                                                        let! streamResult =
                                                            task {
                                                                try
                                                                    let stream =
                                                                        runtimeAgent.Agent.RunStreamingAsync(
                                                                            inputEnvelope,
                                                                            providerSession,
                                                                            agentRunOptions,
                                                                            providerToken
                                                                        )

                                                                    let streamEnumerator =
                                                                        stream.GetAsyncEnumerator(providerToken)

                                                                    try
                                                                        let mutable hasNext = true

                                                                        while hasNext do
                                                                            providerToken.ThrowIfCancellationRequested()

                                                                            let! movedNext =
                                                                                streamEnumerator
                                                                                    .MoveNextAsync()
                                                                                    .AsTask()

                                                                            hasNext <- movedNext

                                                                            if hasNext then
                                                                                let update = streamEnumerator.Current

                                                                                tryUpdateUsage
                                                                                    (&usageDetails)
                                                                                    (&usage)
                                                                                    update

                                                                                if
                                                                                    not (
                                                                                        String.IsNullOrEmpty update.Text
                                                                                    )
                                                                                then
                                                                                    builder.Append(update.Text)
                                                                                    |> ignore

                                                                                    do!
                                                                                        emit
                                                                                            RunEventKind.OutputDelta
                                                                                            ValueNone
                                                                                            (ValueSome update.Text)
                                                                                            ValueNone
                                                                                            ValueNone
                                                                                            ValueNone

                                                                                for content in update.Contents do
                                                                                    match
                                                                                        StreamingMappedEvent.tryMapContent
                                                                                            jsonOptions
                                                                                            content
                                                                                    with
                                                                                    | ValueSome(StreamingMappedEvent.ToolStarted operationId) ->
                                                                                        do!
                                                                                            emit
                                                                                                RunEventKind.ToolStarted
                                                                                                operationId
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                    | ValueSome(StreamingMappedEvent.ToolCompleted operationId) ->
                                                                                        do!
                                                                                            emit
                                                                                                RunEventKind.ToolCompleted
                                                                                                operationId
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                    | ValueSome(StreamingMappedEvent.ApprovalRequested(operationId,
                                                                                                                                       approvalRequest)) ->
                                                                                        do!
                                                                                            emit
                                                                                                RunEventKind.ApprovalRequested
                                                                                                operationId
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                ValueNone
                                                                                                (ValueSome
                                                                                                    approvalRequest)
                                                                                    | ValueNone -> ()
                                                                    finally
                                                                        streamEnumerator
                                                                            .DisposeAsync()
                                                                            .AsTask()
                                                                            .GetAwaiter()
                                                                            .GetResult()

                                                                    providerToken.ThrowIfCancellationRequested()
                                                                    let mutable finalText = builder.ToString()

                                                                    let! finalTextResult =
                                                                        if
                                                                            runOptions.StructuredOutputPolicy = StructuredOutputPolicy.AllowSecondaryModelRepair
                                                                        then
                                                                            match
                                                                                runtime.RuntimeOptions.SecondaryStructuredOutputClient
                                                                            with
                                                                            | ValueNone ->
                                                                                Task.FromResult(
                                                                                    Error(
                                                                                        MafErrors.structuredOutputUnsupportedFailure
                                                                                            runId
                                                                                            "Structured output repair requires a configured secondary structured output chat client."
                                                                                            ValueNone
                                                                                            ValueNone
                                                                                    )
                                                                                )
                                                                            | ValueSome secondaryClient ->
                                                                                task {
                                                                                    let! repairedText, repairedUsage =
                                                                                        repairAsync
                                                                                            secondaryClient
                                                                                            responseFormat
                                                                                            finalText
                                                                                            providerToken

                                                                                    repaired <- true

                                                                                    usageDetails <-
                                                                                        MafErrors.combineUsageDetails
                                                                                            usageDetails
                                                                                            repairedUsage

                                                                                    usage <-
                                                                                        MafErrors.createUsage
                                                                                            usageDetails

                                                                                    let entries =
                                                                                        Dictionary<string, string>(
                                                                                            StringComparer.Ordinal
                                                                                        )

                                                                                    entries["circuit.repaired"] <-
                                                                                        "true"

                                                                                    if
                                                                                        runOptions.SensitiveDataMode = SensitiveDataMode.Standard
                                                                                    then
                                                                                        entries["circuit.repair.originalResponse"] <-
                                                                                            builder.ToString()

                                                                                    diagnosticMetadata <-
                                                                                        entries
                                                                                        :> IReadOnlyDictionary<
                                                                                            string,
                                                                                            string
                                                                                         >

                                                                                    return Ok repairedText
                                                                                }
                                                                        else
                                                                            Task.FromResult(Ok finalText)

                                                                    return
                                                                        match finalTextResult with
                                                                        | Error failure -> Error failure
                                                                        | Ok repairedText ->
                                                                            finalText <- repairedText

                                                                            decodeFinalOutput
                                                                                runId
                                                                                providerToken
                                                                                signature
                                                                                wrapped
                                                                                finalText
                                                                with
                                                                | ex when
                                                                    MafErrors.isCancellationRequested providerToken ex
                                                                    ->
                                                                    return
                                                                        Error(
                                                                            MafErrors.cancelledFailure
                                                                                runId
                                                                                "The run was cancelled."
                                                                                (ValueSome ex)
                                                                        )
                                                                | ex when MafErrors.isStructuredOutputUnsupported ex ->
                                                                    return
                                                                        Error(
                                                                            MafErrors.structuredOutputUnsupportedFailure
                                                                                runId
                                                                                "Structured output is not supported for this run."
                                                                                ValueNone
                                                                                (ValueSome ex)
                                                                        )
                                                                | ex ->
                                                                    return
                                                                        Error(
                                                                            MafErrors.providerFailure
                                                                                runId
                                                                                "The provider request failed."
                                                                                ValueNone
                                                                                (ValueSome ex)
                                                                        )
                                                            }

                                                        match streamResult with
                                                        | Error failure -> do! fail failure
                                                        | Ok output ->
                                                            do!
                                                                emit
                                                                    RunEventKind.RunCompleted
                                                                    ValueNone
                                                                    ValueNone
                                                                    (ValueSome output)
                                                                    ValueNone
                                                                    ValueNone

                                                            return ()
                        finally
                            MafObserver.unregisterSession observerSession
                            channel.Writer.TryComplete() |> ignore
                    }

                background |> ignore

                ChannelAsyncEnumerator(channel.Reader, background, abandonmentCts, providerCts)
                :> IAsyncEnumerator<RunEvent<'Output>>

    let runStreaming<'Input, 'Output>
        (runtime: MafRuntime)
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        (runOptions: RunOptions)
        (jsonOptions: JsonSerializerOptions)
        ([<EnumeratorCancellation>] cancellationToken: CancellationToken)
        : IAsyncEnumerable<RunEvent<'Output>> =
        StreamingRunEnumerable(runtime, agent, signature, input, runOptions, jsonOptions, cancellationToken)
        :> IAsyncEnumerable<RunEvent<'Output>>

[<AbstractClass; Sealed>]
type internal MafStreamingRegistration =
    static do
        MafRuntimeStreamingDispatch.Dispatcher <-
            { new IMafRuntimeStreamingDispatcher with
                member _.RunStreaming(runtime, agent, signature, input, runOptions, jsonOptions, cancellationToken) =
                    MafStreaming.runStreaming
                        (runtime :?> MafRuntime)
                        agent
                        signature
                        input
                        runOptions
                        jsonOptions
                        cancellationToken }
