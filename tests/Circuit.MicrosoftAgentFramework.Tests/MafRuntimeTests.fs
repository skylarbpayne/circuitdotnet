#nowarn "57"

namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Runtime.Loader
open Circuit
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace
open Circuit.Testing
open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

[<AllowNullLiteral>]
type TestInput() =
    [<property: Required>]
    member val Token: string = null with get, set

[<AllowNullLiteral>]
type TestOutput() =
    [<property: Required>]
    member val Text: string = null with get, set

[<AllowNullLiteral>]
type ArrayOutput() =
    member val Items = Array.empty<string> with get, set

[<AllowNullLiteral>]
type WriteToolInput() =
    [<property: Required>]
    member val Path: string = null with get, set

    [<property: Required>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type WriteToolOutput() =
    [<property: Required>]
    member val Status: string = null with get, set

[<AllowNullLiteral>]
type ApprovalPayload() =
    [<property: Required>]
    member val StreetName: string = null with get, set

[<AllowNullLiteral>]
type ApprovalInput() =
    [<property: Required>]
    member val Title: string = null with get, set

    [<property: Required>]
    member val Payload: ApprovalPayload = null with get, set

type ApprovalNamingPolicy() =
    inherit JsonNamingPolicy()

    override _.ConvertName(name) =
        if String.IsNullOrWhiteSpace name then
            name
        else
            $"{name}_runtime"

type UnknownToolCallContent(callId: string) =
    inherit ToolCallContent(callId)

    member val Secret = "top-secret" with get, set

type ThrowingArgumentValue() =
    member val Secret = "converter-secret" with get, set

type ThrowingArgumentValueConverter() =
    inherit JsonConverter<ThrowingArgumentValue>()

    override _.Read(_reader, _typeToConvert, _options) =
        raise (InvalidOperationException("converter-secret: read should not be exposed"))

    override _.Write(_writer, _value, _options) =
        raise (InvalidOperationException("converter-secret: write should not be exposed"))

type ThrowingTestInputConverter() =
    inherit JsonConverter<TestInput>()

    override _.Read(_reader, _typeToConvert, _options) =
        raise (InvalidOperationException("converter-secret: read should not be exposed"))

    override _.Write(writer, value, options) =
        JsonSerializer.Serialize(writer, value.Token, options)

type ThrowingInputEnvelopeConverter() =
    inherit JsonConverter<TestInput>()

    override _.Read(_reader, _typeToConvert, _options) =
        raise (InvalidOperationException("converter-secret: read should not be exposed"))

    override _.Write(_writer, _value, _options) =
        raise (InvalidOperationException("converter-secret: write should not be exposed"))

type ThrowingTestOutputConverter(secret: string) =
    inherit JsonConverter<TestOutput>()

    override _.Read(_reader, _typeToConvert, _options) = raise (JsonException(secret))

    override _.Write(writer, value, options) =
        JsonSerializer.Serialize(writer, value.Text, options)

type CancellingInputEnvelopeConverter(cancellationSource: CancellationTokenSource) =
    inherit JsonConverter<TestInput>()

    override _.Read(_reader, _typeToConvert, _options) =
        raise (OperationCanceledException(cancellationSource.Token))

    override _.Write(_writer, _value, _options) =
        cancellationSource.Cancel()
        raise (OperationCanceledException(cancellationSource.Token))

type StubToolApprovalPolicy(result: bool) =
    interface IToolApprovalPolicy with
        member _.IsApprovedAsync(_policyName, _context) = ValueTask<bool>(result)

type NullServiceProvider() =
    interface IServiceProvider with
        member _.GetService(_serviceType) = null

type DummyAgentSession() =
    inherit AgentSession()

type DummyAIAgent() =
    inherit AIAgent()

    override _.CreateSessionCoreAsync(_cancellationToken) =
        raise (InvalidOperationException("This test agent does not create sessions."))

    override _.SerializeSessionCoreAsync(_session, _options, _cancellationToken) =
        raise (InvalidOperationException("This test agent does not serialize sessions."))

    override _.DeserializeSessionCoreAsync(_element, _options, _cancellationToken) =
        raise (InvalidOperationException("This test agent does not deserialize sessions."))

    override _.RunCoreAsync(_messages, _session, _options, _cancellationToken) =
        raise (InvalidOperationException("This test agent does not execute runs."))

    override _.RunCoreStreamingAsync(_messages, _session, _options, _cancellationToken) =
        raise (InvalidOperationException("This test agent does not execute streaming runs."))

type ArrayAsyncEnumerable<'T>(items: 'T[]) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
            let mutable index = -1
            let mutable current = Unchecked.defaultof<'T>

            { new IAsyncEnumerator<'T> with
                member _.Current = current

                member _.MoveNextAsync() =
                    if cancellationToken.IsCancellationRequested then
                        ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                    else
                        index <- index + 1

                        if index < items.Length then
                            current <- items[index]
                            ValueTask<bool>(true)
                        else
                            ValueTask<bool>(false)

                member _.DisposeAsync() = ValueTask() }

type FakeChatClient
    (
        responseHandler: IReadOnlyList<ChatMessage> -> ChatOptions -> CancellationToken -> Task<ChatResponse>,
        streamingHandler:
            IReadOnlyList<ChatMessage> -> ChatOptions -> CancellationToken -> IAsyncEnumerable<ChatResponseUpdate>
    ) =
    let responseMessages = ResizeArray<IReadOnlyList<ChatMessage>>()
    let streamingMessages = ResizeArray<IReadOnlyList<ChatMessage>>()
    let mutable responseCalls = 0
    let mutable streamingCalls = 0
    let mutable sawCancellation = false

    member _.ResponseCalls = responseCalls
    member _.StreamingCalls = streamingCalls
    member _.ResponseMessages = responseMessages
    member _.StreamingMessages = streamingMessages
    member _.SawCancellation = sawCancellation

    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(messages, options, cancellationToken) =
            responseCalls <- responseCalls + 1

            let snapshot =
                messages |> Seq.map _.Clone() |> Seq.toArray :> IReadOnlyList<ChatMessage>

            responseMessages.Add snapshot
            cancellationToken.Register(fun () -> sawCancellation <- true) |> ignore
            responseHandler snapshot options cancellationToken

        member _.GetStreamingResponseAsync(messages, options, cancellationToken) =
            streamingCalls <- streamingCalls + 1

            let snapshot =
                messages |> Seq.map _.Clone() |> Seq.toArray :> IReadOnlyList<ChatMessage>

            streamingMessages.Add snapshot
            cancellationToken.Register(fun () -> sawCancellation <- true) |> ignore
            streamingHandler snapshot options cancellationToken

        member _.GetService(_serviceType, _serviceKey) = null

type CancellationBlockingObserver() =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable cancellations = 0

    member _.Entered = entered.Task
    member _.Cancellations = Volatile.Read(&cancellations)

    interface Circuit.IRunObserver with
        member _.OnEventAsync(event, cancellationToken) =
            if event.Kind <> AgentRunEventKind.RunStarted then
                ValueTask()
            else
                ValueTask(
                    task {
                        entered.TrySetResult() |> ignore

                        try
                            do! Task.Delay(Timeout.Infinite, cancellationToken)
                        finally
                            if cancellationToken.IsCancellationRequested then
                                Interlocked.Increment(&cancellations) |> ignore
                    }
                )

type RecordingObserver() =
    let events = ResizeArray<RunEventEnvelope>()

    member _.Events = events

    member _.Observations =
        events
        |> Seq.filter (fun event ->
            event.Kind = AgentRunEventKind.RunCompleted
            || event.Kind = AgentRunEventKind.RunFailed)
        |> Seq.toArray

    interface Circuit.IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            events.Add event
            ValueTask()

module internal Helpers =
    let private runOptionsCtor =
        typeof<RunOptions>
            .GetConstructor(
                BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic,
                null,
                [| typeof<CircuitSession voption>
                   typeof<string voption>
                   typeof<string voption>
                   typeof<IReadOnlyDictionary<string, string>>
                   typeof<StructuredOutputPolicy>
                   typeof<SensitiveDataMode>
                   typeof<IServiceProvider> |],
                null
            )

    let createRunOptionsWithSensitiveDataMode
        (session: CircuitSession option)
        (tenantId: string option)
        (userId: string option)
        (policy: StructuredOutputPolicy)
        (sensitiveDataMode: SensitiveDataMode)
        =
        let tags =
            Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let sessionValue: CircuitSession voption =
            match session with
            | Some value -> ValueSome value
            | None -> ValueNone

        let tenantValue = tenantId |> Option.toValueOption
        let userValue = userId |> Option.toValueOption

        runOptionsCtor.Invoke(
            [| box sessionValue
               box tenantValue
               box userValue
               box tags
               box policy
               box sensitiveDataMode
               box (NullServiceProvider() :> IServiceProvider) |]
        )
        :?> RunOptions

    let createRunOptionsWith session tenantId userId policy =
        createRunOptionsWithSensitiveDataMode session tenantId userId policy SensitiveDataMode.Standard

    let createRunOptions (session: CircuitSession option) (policy: StructuredOutputPolicy) =
        createRunOptionsWith session None None policy

    let createAgent instructions =
        AgentDefinition.Create(
            "agent.test",
            "1.0.0",
            "Agent Test",
            instructions,
            ValueNone,
            Seq.empty,
            Seq.empty,
            Seq.empty
        )

    let createSignatureWith<'Output> id version (jsonOptions: JsonSerializerOptions) =
        Signature<TestInput, 'Output>
            .Create(id, version, "Test signature", "Return structured JSON.", jsonOptions, Seq.empty, Seq.empty)

    let createSignature<'Output> () =
        createSignatureWith<'Output> "signature.test" "1.0.0" (CircuitJson.createOptions ())

    let createMafRuntimeWith configure primaryClient secondaryClient observers =
        let options = MafRuntimeOptions()
        options.Observers <- observers

        match secondaryClient with
        | Some client -> options.SecondaryStructuredOutputClient <- ValueSome(client :> IChatClient)
        | None -> ()

        configure options
        MafRuntime(primaryClient :> IChatClient, options)

    let createRuntimeWith configure primaryClient secondaryClient observers =
        createMafRuntimeWith configure primaryClient secondaryClient observers :> ICircuitRuntime

    let createRuntime primaryClient secondaryClient observers =
        createRuntimeWith ignore primaryClient secondaryClient observers

    let jsonResponse (text: string) =
        ChatResponse(ChatMessage(ChatRole.Assistant, text))

    let functionCallResponse callId name (arguments: IDictionary<string, obj>) =
        ChatResponse(
            ChatMessage(
                ChatRole.Assistant,
                ResizeArray<AIContent>([ FunctionCallContent(callId, name, arguments) :> AIContent ])
                :> IList<AIContent>
            )
        )

    let jsonResponseWithUsage (text: string) (usage: UsageDetails) =
        let response = jsonResponse text
        response.Usage <- usage
        response

    let usageDetails inputTokens outputTokens =
        let details = UsageDetails()
        details.InputTokenCount <- Nullable(int64 inputTokens)
        details.OutputTokenCount <- Nullable(int64 outputTokens)
        details.TotalTokenCount <- Nullable(int64 (inputTokens + outputTokens))
        details

    let createToolDefinition<'Input, 'Output>
        id
        version
        description
        approval
        approvalPolicy
        inputContract
        outputContract
        invokeAsync
        =
        ToolDefinition<'Input, 'Output>
            .Create(
                id,
                version,
                description,
                inputContract,
                outputContract,
                approval,
                approvalPolicy,
                Func<ToolContext, 'Input, Task<'Output>>(invokeAsync)
            )

    let createResolvedTool definition tags = ResolvedTool.Create(definition, tags)

    let createTestTool id approval approvalPolicy invokeAsync =
        createToolDefinition<TestInput, TestOutput>
            id
            "1.0.0"
            $"{id} description"
            approval
            approvalPolicy
            (Contract<TestInput>.Create(CircuitJson.createOptions (), Seq.empty))
            (Contract<TestOutput>.Create(CircuitJson.createOptions (), Seq.empty))
            invokeAsync

    let createWriteTool approval approvalPolicy invokeAsync =
        createToolDefinition<WriteToolInput, WriteToolOutput>
            "tool.write"
            "1.0.0"
            "Write tool"
            approval
            approvalPolicy
            (Contract<WriteToolInput>.Create(CircuitJson.createOptions (), Seq.empty))
            (Contract<WriteToolOutput>.Create(CircuitJson.createOptions (), Seq.empty))
            invokeAsync

    let tryGetFunctionResult (messages: seq<ChatMessage>) =
        messages
        |> Seq.tryPick (fun message ->
            if message.Role <> ChatRole.Tool || isNull message.Contents then
                None
            else
                message.Contents
                |> Seq.tryPick (fun content ->
                    match content with
                    | :? FunctionResultContent as result -> Some result
                    | _ -> None))

    let tryGetApprovalRequest (messages: seq<ChatMessage>) =
        messages
        |> Seq.tryPick (fun message ->
            if isNull message.Contents then
                None
            else
                message.Contents
                |> Seq.tryPick (fun content ->
                    match content with
                    | :? ToolApprovalRequestContent as approvalRequest -> Some approvalRequest
                    | _ -> None))

    let buildSessionAgent (runtime: MafRuntime) agent : AIAgent =
        match (runtime.BuildSessionAgentAsync (RunId.New()) agent RunOptions.Default CancellationToken.None).Result with
        | Ok sessionAgent -> sessionAgent.Agent
        | Error failure -> failwith failure.Message

    let resolveSingleMappedTool (runtime: MafRuntime) agent runOptions =
        let signature = createSignature<TestOutput> ()
        let runContext = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        match (runtime.ResolveCapabilitiesAsync runContext.RunId runContext agent CancellationToken.None).Result with
        | Error failure -> failwith failure.Message
        | Ok(tools, _skills) -> Assert.Single tools

    let collectStreamEvents<'Output> (stream: IAsyncEnumerable<RunEvent<'Output>>) =
        let events = ResizeArray<RunEvent<'Output>>()
        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    events.Add enumerator.Current
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        events |> Seq.toArray

    let assertCompletesWithin (timeoutMs: int) (task: Task) message =
        Assert.True(task.Wait(timeoutMs), message)

    type SharedDependencyAssemblyLoadContext(assemblyPath: string) =
        inherit AssemblyLoadContext("circuit-microsoft-agent-framework-tests", false)

        let resolver = AssemblyDependencyResolver(assemblyPath)

        let sharedAssemblyNames =
            set
                [ "Circuit.Core"
                  "FSharp.Core"
                  "Microsoft.Agents.AI"
                  "Microsoft.Extensions.AI"
                  "Microsoft.Extensions.AI.Abstractions" ]

        override this.Load(assemblyName) =
            if isNull assemblyName.Name then
                null
            elif sharedAssemblyNames.Contains assemblyName.Name then
                AssemblyLoadContext.Default.Assemblies
                |> Seq.tryFind (fun assembly -> assembly.GetName().Name = assemblyName.Name)
                |> Option.defaultValue null
            else
                let resolvedPath = resolver.ResolveAssemblyToPath(assemblyName)

                if isNull resolvedPath then
                    null
                else
                    this.LoadFromAssemblyPath(resolvedPath)

module MafRuntimeTests =
    open Helpers

    [<Fact>]
    let ``valid object primitive and array structured outputs deserialize`` () =
        let queue =
            Queue<string>([| "{\"text\":\"hello\"}"; "7"; "{\"items\":[\"a\",\"b\"]}" |])

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, queue.Dequeue()) |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."

        let objectResult =
            runtime
                .RunAsync(
                    agent,
                    createSignature<TestOutput> (),
                    TestInput(Token = "object"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(
            objectResult.Result.IsSuccess,
            if objectResult.Result.IsSuccess then
                ""
            else
                objectResult.Result.Failure.Message
        )

        Assert.Equal("hello", objectResult.Result.Value.Text)

        let primitiveResult =
            runtime
                .RunAsync(
                    agent,
                    createSignature<int> (),
                    TestInput(Token = "primitive"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(primitiveResult.Result.IsSuccess)
        Assert.Equal(7, primitiveResult.Result.Value)

        let arrayResult =
            runtime
                .RunAsync(
                    agent,
                    createSignature<ArrayOutput> (),
                    TestInput(Token = "array"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(arrayResult.Result.IsSuccess)
        Assert.Equal<string[]>([| "a"; "b" |], arrayResult.Result.Value.Items)

    [<Fact>]
    let ``malformed and null outputs become decode failures`` () =
        let queue = Queue<string>([| "null"; "{not-json" |])

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, queue.Dequeue()) |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."
        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly

        let nullResult =
            runtime.RunAsync(agent, signature, TestInput(Token = "null"), runOptions, CancellationToken.None).Result

        let malformedResult =
            runtime.RunAsync(agent, signature, TestInput(Token = "bad"), runOptions, CancellationToken.None).Result

        Assert.False(nullResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Decode, nullResult.Result.Failure.Code)
        Assert.False(malformedResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Decode, malformedResult.Result.Failure.Code)

    [<Fact>]
    let ``streaming wrapper shape errors map to decode failures`` () =
        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, "{\"other\":1}") |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<Circuit.IRunObserver>

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<int> (),
                TestInput(Token = "stream-shape"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let events = ResizeArray<RunEvent<int>>()

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    events.Add enumerator.Current
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        Assert.Equal(RunEventKind.RunStarted, events[0].Kind)
        Assert.Equal(RunEventKind.RunFailed, events[events.Count - 1].Kind)
        Assert.True(events[events.Count - 1].Failure.IsSome)
        Assert.Equal(CircuitFailureCode.Decode, events[events.Count - 1].Failure.Value.Code)
        Assert.Equal(1, streamingClient.StreamingCalls)

    [<Fact>]
    let ``non-stream wrapper shape errors map to decode failures`` () =
        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"other\":1}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."

        let result =
            runtime
                .RunAsync(
                    agent,
                    createSignature<int> (),
                    TestInput(Token = "shape"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Decode, result.Result.Failure.Code)

    [<Fact>]
    let ``input validation prevents fake client invocation`` () =
        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"nope\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."
        let signature = createSignature<TestOutput> ()

        let result =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = null),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, result.Result.Failure.Code)
        Assert.Equal(0, fake.ResponseCalls)

    [<Fact>]
    let ``input serialization converter failures return sanitized decode failures`` () =
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.Converters.Add(ThrowingInputEnvelopeConverter())

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."

        let result =
            runtime
                .RunAsync(
                    agent,
                    createSignatureWith<TestOutput> "signature.test" "1.0.0" jsonOptions,
                    TestInput(Token = "serialize"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Decode, result.Result.Failure.Code)
        Assert.Equal("The run input could not be serialized.", result.Result.Failure.Message)
        Assert.DoesNotContain("converter-secret", result.Result.Failure.Message)
        Assert.Equal(0, fake.ResponseCalls)

    [<Fact>]
    let ``input serialization cancellation returns cancelled before provider invocation`` () =
        use cts = new CancellationTokenSource()
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.Converters.Add(CancellingInputEnvelopeConverter(cts))

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."

        let result =
            runtime
                .RunAsync(
                    agent,
                    createSignatureWith<TestOutput> "signature.test" "1.0.0" jsonOptions,
                    TestInput(Token = "serialize-cancel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    cts.Token
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.Equal(0, fake.ResponseCalls)

    [<Fact>]
    let ``streaming input serialization converter failures emit started then one sanitized decode terminal event`` () =
        let observer = RecordingObserver()
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.Converters.Add(ThrowingInputEnvelopeConverter())

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime streamingClient None [| observer :> Circuit.IRunObserver |]

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignatureWith<TestOutput> "signature.test" "1.0.0" jsonOptions,
                TestInput(Token = "stream-serialize"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)
        Assert.Equal(CircuitFailureCode.Decode, events[1].Failure.Value.Code)
        Assert.Equal("The run input could not be serialized.", events[1].Failure.Value.Message)

        let observerTerminalEvents =
            observer.Events
            |> Seq.filter (fun event ->
                event.Kind = AgentRunEventKind.RunCompleted
                || event.Kind = AgentRunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(observerTerminalEvents) |> ignore
        Assert.Equal(AgentRunEventKind.RunCompleted, observerTerminalEvents[0].Kind)

        let failedNode =
            Assert.Single(
                observer.Events
                |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.StepCompleted && not (isNull event.Failure))
            )

        Assert.Equal(AgentFailureCode.Decode, failedNode.Failure.Code)
        Assert.Equal(0, streamingClient.StreamingCalls)

    [<Fact>]
    let ``streaming input serialization cancellation emits started then one cancelled terminal event`` () =
        let observer = RecordingObserver()
        use cts = new CancellationTokenSource()
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.Converters.Add(CancellingInputEnvelopeConverter(cts))

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime streamingClient None [| observer :> Circuit.IRunObserver |]

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignatureWith<TestOutput> "signature.test" "1.0.0" jsonOptions,
                TestInput(Token = "stream-serialize-cancel"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                cts.Token
            )
            |> collectStreamEvents

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)
        Assert.Equal(CircuitFailureCode.Cancelled, events[1].Failure.Value.Code)

        let observerTerminalEvents =
            observer.Events
            |> Seq.filter (fun event ->
                event.Kind = AgentRunEventKind.RunCompleted
                || event.Kind = AgentRunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(observerTerminalEvents) |> ignore
        Assert.Equal(AgentRunEventKind.RunFailed, observerTerminalEvents[0].Kind)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(AgentFailureCode.Cancelled, observer.Observations[0].Failure.Code)
        Assert.Equal(0, streamingClient.StreamingCalls)

    [<Fact>]
    let ``streaming tool resolver failures emit one generic terminal event without leaking resolver details`` () =
        let observer = RecordingObserver()

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| { new IToolResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   raise (
                                       InvalidOperationException("resolver-secret /tmp/tool-secret-root/secret.txt")
                                   ) } |])
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| observer :> Circuit.IRunObserver |]

        let events =
            runtime.RunStreamingAsync(
                createAgent "Resolve tools safely.",
                createSignature<TestOutput> (),
                TestInput(Token = "resolver-secret"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)
        Assert.True(events[1].Failure.IsSome)
        Assert.Equal(CircuitFailureCode.Tool, events[1].Failure.Value.Code)
        Assert.Equal("Tool resolution failed.", events[1].Failure.Value.Message)
        Assert.DoesNotContain("resolver-secret", events[1].Failure.Value.Message)
        Assert.DoesNotContain("/tmp/tool-secret-root", events[1].Failure.Value.Message)
        Assert.True(events[1].Failure.Value.Exception.IsSome)
        Assert.Contains("resolver-secret", events[1].Failure.Value.Exception.Value.Message)

        let observerTerminalEvents =
            observer.Events
            |> Seq.filter (fun event ->
                event.Kind = AgentRunEventKind.RunCompleted
                || event.Kind = AgentRunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(observerTerminalEvents) |> ignore
        Assert.Equal(AgentRunEventKind.RunCompleted, observerTerminalEvents[0].Kind)

        let failedNode =
            Assert.Single(
                observer.Events
                |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.StepCompleted && not (isNull event.Failure))
            )

        Assert.Equal("Tool resolution failed.", failedNode.Failure.Message)
        Assert.DoesNotContain("resolver-secret", failedNode.Failure.Message)
        Assert.DoesNotContain("/tmp/tool-secret-root", failedNode.Failure.Message)

    [<Fact>]
    let ``cancellation reaches the fake client`` () =
        let tcs = TaskCompletionSource<ChatResponse>()

        let fake =
            new FakeChatClient(
                (fun _messages _options cancellationToken ->
                    cancellationToken.Register(fun () -> tcs.TrySetCanceled(cancellationToken) |> ignore)
                    |> ignore

                    tcs.Task),
                (fun _messages _options _ct ->
                    { new IAsyncEnumerable<ChatResponseUpdate> with
                        member _.GetAsyncEnumerator(token) =
                            { new IAsyncEnumerator<ChatResponseUpdate> with
                                member _.Current = Unchecked.defaultof<_>

                                member _.MoveNextAsync() =
                                    ValueTask<bool>(Task.Delay(Timeout.Infinite, token).ContinueWith(fun _ -> false))

                                member _.DisposeAsync() = ValueTask() } })
            )

        let runtime = createRuntime fake None Array.empty<Circuit.IRunObserver>
        let agent = createAgent "Follow directions."
        use cts = new CancellationTokenSource()

        let pending =
            runtime.RunAsync(
                agent,
                createSignature<TestOutput> (),
                TestInput(Token = "cancel"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                cts.Token
            )

        cts.Cancel()
        let result = pending.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.True(fake.SawCancellation)

    [<Fact>]
    let ``provider failures are sanitized before reaching public failures and observer events`` () =
        let observer = RecordingObserver()

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    raise (InvalidOperationException("provider-secret /tmp/provider-secret-path"))),
                (fun _messages _options _ct ->
                    raise (InvalidOperationException("provider-secret /tmp/provider-secret-path")))
            )

        let runtime = createRuntime fake None [| observer :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "provider-secret"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Provider, result.Result.Failure.Code)
        Assert.Equal("The provider request failed.", result.Result.Failure.Message)
        Assert.DoesNotContain("provider-secret", result.Result.Failure.Message)

        let failedEvent =
            Assert.Single(
                observer.Events
                |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.StepCompleted && not (isNull event.Failure))
            )

        Assert.Equal("The provider request failed.", failedEvent.Failure.Message)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(AgentRunEventKind.RunCompleted, observer.Observations[0].Kind)

    [<Fact>]
    let ``output decode failures are sanitized before reaching public failures and observer events`` () =
        let observer = RecordingObserver()
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.Converters.Add(ThrowingTestOutputConverter("decode-secret /tmp/decode-secret.json"))

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None [| observer :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignatureWith<TestOutput> "signature.test" "1.0.0" jsonOptions,
                    TestInput(Token = "decode-secret"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Decode, result.Result.Failure.Code)
        Assert.Equal("The provider response could not be decoded.", result.Result.Failure.Message)
        Assert.DoesNotContain("decode-secret", result.Result.Failure.Message)

        let failedEvent =
            Assert.Single(
                observer.Events
                |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.StepCompleted && not (isNull event.Failure))
            )

        Assert.Equal("The provider response could not be decoded.", failedEvent.Failure.Message)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(AgentRunEventKind.RunCompleted, observer.Observations[0].Kind)

    [<Fact>]
    let ``skill resolver failures are sanitized before reaching public failures and observer events`` () =
        let observer = RecordingObserver()

        let fake =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.SkillResolvers <-
                        [| { new ISkillResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   raise (InvalidOperationException("skill-secret /tmp/skill-secret-root/SKILL.md")) } |])
                fake
                None
                [| observer :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "skill-secret"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Skill, result.Result.Failure.Code)
        Assert.Equal("Skill resolution failed.", result.Result.Failure.Message)
        Assert.DoesNotContain("skill-secret", result.Result.Failure.Message)

        let failedEvent =
            Assert.Single(
                observer.Events
                |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.StepCompleted && not (isNull event.Failure))
            )

        Assert.Equal("Skill resolution failed.", failedEvent.Failure.Message)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(AgentRunEventKind.RunCompleted, observer.Observations[0].Kind)

    [<Fact>]
    let ``native only does not make a second model call`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"native\"}") |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"secondary\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime primary (Some secondary) Array.empty<Circuit.IRunObserver>

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "native"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("native", result.Result.Value.Text)
        Assert.Equal(1, primary.StreamingCalls)
        Assert.Equal(0, secondary.ResponseCalls)

    [<Fact>]
    let ``explicit repair makes exactly one repair call and records it`` () =
        let observer = RecordingObserver()
        let primaryOptions = ResizeArray<ChatOptions>()
        let secondaryOptions = ResizeArray<ChatOptions>()

        let primary =
            new FakeChatClient(
                (fun _messages options _ct ->
                    primaryOptions.Add options
                    Task.FromResult(jsonResponse "unstructured result")),
                (fun _messages options _ct ->
                    primaryOptions.Add options

                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, "unstructured result") |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let secondary =
            new FakeChatClient(
                (fun _messages options _ct ->
                    secondaryOptions.Add options
                    Task.FromResult(jsonResponse "{\"text\":\"repaired\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime primary (Some secondary) [| observer :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "repair"),
                    createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                    CancellationToken.None
                )
                .Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("repaired", result.Result.Value.Text)
        Assert.Equal(1, primary.StreamingCalls)
        Assert.Equal(1, secondary.ResponseCalls)
        Assert.Single(primaryOptions) |> ignore
        Assert.Single(secondaryOptions) |> ignore
        Assert.NotNull(secondaryOptions[0].ResponseFormat)

    [<Fact>]
    let ``repaired run usage aggregates primary and repair usage`` () =
        let observer = RecordingObserver()
        let primaryUsage = usageDetails 10 20
        let repairUsage = usageDetails 2 3

        let primary =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    Task.FromResult(jsonResponseWithUsage "unstructured result" primaryUsage)),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(ChatRole.Assistant, "unstructured result")
                           ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ UsageContent(primaryUsage) :> AIContent ]) :> IList<AIContent>
                           ) |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    Task.FromResult(jsonResponseWithUsage "{\"text\":\"repaired\"}" repairUsage)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime primary (Some secondary) [| observer :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "repair-usage"),
                    createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                    CancellationToken.None
                )
                .Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal(12, result.Usage.InputTokens)
        Assert.Equal(23, result.Usage.OutputTokens)
        Assert.Equal(35, result.Usage.TotalTokens)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(35, observer.Observations[0].Usage.TotalTokens)

    [<Fact>]
    let ``streaming repaired usage aggregates primary and repair usage`` () =
        let observer = RecordingObserver()
        let primaryUsage = usageDetails 10 20
        let repairUsage = usageDetails 2 3

        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(ChatRole.Assistant, "unstructured result")
                           ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ UsageContent(primaryUsage) :> AIContent ]) :> IList<AIContent>
                           ) |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    Task.FromResult(jsonResponseWithUsage "{\"text\":\"repaired\"}" repairUsage)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime primary (Some secondary) [| observer :> Circuit.IRunObserver |]

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "repair-stream-usage"),
                createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                CancellationToken.None
            )
            |> collectStreamEvents

        let completed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted))

        Assert.Equal("repaired", completed.Value.Value.Text)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(12, observer.Observations[0].Usage.InputTokens)
        Assert.Equal(23, observer.Observations[0].Usage.OutputTokens)
        Assert.Equal(35, observer.Observations[0].Usage.TotalTokens)

    [<Fact>]
    let ``streaming repair preflight fails before provider or tool resolution when repair client is missing`` () =
        let mutable toolResolverCalls = 0

        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| { new IToolResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   toolResolverCalls <- toolResolverCalls + 1

                                   ValueTask<IReadOnlyList<ResolvedTool>>(
                                       Array.empty<ResolvedTool> :> IReadOnlyList<ResolvedTool>
                                   ) } |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "missing-repair-client"),
                createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                CancellationToken.None
            )
            |> collectStreamEvents

        let failure =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed))

        Assert.Equal(CircuitFailureCode.StructuredOutputUnsupported, failure.Failure.Value.Code)
        Assert.Equal(0, primary.ResponseCalls)
        Assert.Equal(0, primary.StreamingCalls)
        Assert.Equal(0, toolResolverCalls)

    [<Fact>]
    let ``approval requested events map function call details and preserve ids`` () =
        let arguments = Dictionary<string, obj>()
        arguments["city"] <- "Paris"
        arguments["days"] <- 3

        let content =
            ToolApprovalRequestContent("approval-1", FunctionCallContent("call-7", "fetch_weather", arguments))

        let mapped =
            MafStreaming.StreamingMappedEvent.tryMapContent (CircuitJson.createOptions ()) (content :> AIContent)

        match mapped with
        | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(operationId, request)) ->
            arguments["city"] <- "London"
            Assert.Equal(ValueSome "call-7", operationId)
            Assert.Equal("approval-1", request.RequestId)
            Assert.Equal("fetch_weather", request.ToolName)
            use document = JsonDocument.Parse(request.ArgumentsJson.Value)
            Assert.Equal("Paris", document.RootElement.GetProperty("city").GetString())
            Assert.Equal(3, document.RootElement.GetProperty("days").GetInt32())
        | _ -> failwith "Approval content was not mapped."

    [<Fact>]
    let ``first concurrent streaming calls initialize the dispatcher safely`` () =
        let runInitializationRace () =
            let primary =
                new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                    (fun _messages _options _ct ->
                        ArrayAsyncEnumerable(Array.empty) :> IAsyncEnumerable<ChatResponseUpdate>)
                )

            let assemblyPath = typeof<MafRuntime>.Assembly.Location
            let loadContext = SharedDependencyAssemblyLoadContext(assemblyPath)

            let isolatedAssembly = loadContext.LoadFromAssemblyPath(assemblyPath)

            let runtimeType =
                isolatedAssembly.GetType("Circuit.MicrosoftAgentFramework.MafRuntime", true)

            let optionsType =
                isolatedAssembly.GetType("Circuit.MicrosoftAgentFramework.MafRuntimeOptions", true)

            let runtimeOptions = Activator.CreateInstance(optionsType)

            let runtime =
                Activator.CreateInstance(runtimeType, [| box primary; runtimeOptions |]) :?> ICircuitRuntime

            let agent = createAgent "Follow directions."
            let signature = createSignature<TestOutput> ()

            use start = new ManualResetEventSlim(false)

            let tasks =
                [| for index in 1..64 ->
                       Task.Run(
                           Action(fun () ->
                               start.Wait()

                               runtime.RunStreamingAsync(
                                   agent,
                                   signature,
                                   TestInput(Token = $"first-use-{index}"),
                                   createRunOptions None StructuredOutputPolicy.NativeOnly,
                                   CancellationToken.None
                               )
                               |> ignore)
                       ) |]

            start.Set()
            Task.WhenAll(tasks).GetAwaiter().GetResult()

        for _ in 1..16 do
            runInitializationRace ()

    [<Fact>]
    let ``approval requested events handle blank function-call names and malformed arguments safely`` () =
        let arguments = Dictionary<string, obj>()
        arguments["bad"] <- typeof<int>

        let content =
            ToolApprovalRequestContent("approval-2", FunctionCallContent("call-8", "", arguments))

        let mapped =
            MafStreaming.StreamingMappedEvent.tryMapContent (CircuitJson.createOptions ()) (content :> AIContent)

        match mapped with
        | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(operationId, request)) ->
            Assert.Equal(ValueSome "call-8", operationId)
            Assert.Equal("approval-2", request.RequestId)
            Assert.Equal("unknown-tool-call", request.ToolName)
            Assert.True(request.ArgumentsJson.IsNone)
        | _ -> failwith "Approval content was not mapped."

    [<Fact>]
    let ``approval requested events handle unknown tool-call content safely`` () =
        let content =
            ToolApprovalRequestContent("approval-2", UnknownToolCallContent("call-9"))

        let mapped =
            MafStreaming.StreamingMappedEvent.tryMapContent (CircuitJson.createOptions ()) (content :> AIContent)

        match mapped with
        | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(operationId, request)) ->
            Assert.Equal(ValueSome "call-9", operationId)
            Assert.Equal("approval-2", request.RequestId)
            Assert.Equal("unknown-tool-call", request.ToolName)
            Assert.True(request.ArgumentsJson.IsNone)
        | _ -> failwith "Approval content was not mapped."

    [<Fact>]
    let ``cancellation during observer startup returns cancelled without invoking the provider`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        use cts = new CancellationTokenSource()

        let observer =
            { new Circuit.IRunObserver with
                member _.OnEventAsync(event, _cancellationToken) =
                    if event.Kind = AgentRunEventKind.RunStarted then
                        cts.Cancel()
                        ValueTask(Task.FromCanceled(cts.Token))
                    else
                        ValueTask() }

        let runtime = createRuntime primary None [| observer |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "cancel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    cts.Token
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.Equal(0, primary.ResponseCalls)

    [<Fact>]
    let ``tool and skill cancellation map to cancelled failures`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let cancelledToolsRuntime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| { new IToolResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   use cancelled = new CancellationTokenSource()
                                   cancelled.Cancel()

                                   ValueTask<IReadOnlyList<ResolvedTool>>(
                                       Task.FromCanceled<IReadOnlyList<ResolvedTool>>(cancelled.Token)
                                   ) } |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let toolResult =
            cancelledToolsRuntime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "tool-cancel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(toolResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, toolResult.Result.Failure.Code)
        Assert.Equal(0, primary.ResponseCalls)

        let cancelledSkillsRuntime =
            createRuntimeWith
                (fun options ->
                    options.SkillResolvers <-
                        [| { new ISkillResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   use cancelled = new CancellationTokenSource()
                                   cancelled.Cancel()

                                   ValueTask<IReadOnlyList<ResolvedSkill>>(
                                       Task.FromCanceled<IReadOnlyList<ResolvedSkill>>(cancelled.Token)
                                   ) } |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let skillAgent =
            AgentDefinition.Create(
                "agent.skill",
                "1.0.0",
                "Agent Skill",
                "Follow directions.",
                ValueNone,
                Seq.empty,
                [| SkillReference.Create("skill.test", "1.0.0") |],
                Seq.empty
            )

        let skillResult =
            cancelledSkillsRuntime
                .RunAsync(
                    skillAgent,
                    createSignature<TestOutput> (),
                    TestInput(Token = "skill-cancel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(skillResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, skillResult.Result.Failure.Code)
        Assert.Equal(0, primary.ResponseCalls)

    [<Fact>]
    let ``duplicate model-facing identities fail before provider invocation`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntimeWith
                (fun options ->
                    let first =
                        createResolvedTool
                            (createTestTool "tool.read" ApprovalMode.Never ValueNone (fun _ _ ->
                                Task.FromResult(TestOutput(Text = "first"))))
                            Seq.empty

                    let second =
                        createResolvedTool
                            (createTestTool "tool-read" ApprovalMode.Never ValueNone (fun _ _ ->
                                Task.FromResult(TestOutput(Text = "second"))))
                            Seq.empty

                    options.ToolResolvers <- [| StaticToolResolver([| first; second |]) :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let result =
            runtime
                .RunAsync(
                    createAgent "Follow directions.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "duplicate-tools"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, result.Result.Failure.Code)
        Assert.Contains("tool_read_v1", result.Result.Failure.Message)
        Assert.Equal(0, primary.ResponseCalls)

    [<Fact>]
    let ``same runtime resolves different tenant tool snapshots concurrently without leakage`` () =
        let primary =
            new FakeChatClient(
                (fun messages _options _ct ->
                    match tryGetFunctionResult messages with
                    | Some functionResult ->
                        let toolOutput = Assert.IsType<TestOutput>(functionResult.Result)
                        Task.FromResult(jsonResponse $"{{\"text\":\"{toolOutput.Text}\"}}")
                    | None ->
                        let arguments = Dictionary<string, obj>()
                        arguments["token"] <- "invoke"
                        Task.FromResult(functionCallResponse "tenant-call" "tenant_reader_v1" arguments)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| DelegateToolResolver(
                               Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>
                                   (fun context _ ->
                                       let tenantId = context.TenantId |> ValueOption.defaultValue "missing-tenant"

                                       let tool =
                                           createResolvedTool
                                               (createTestTool
                                                   "tenant.reader"
                                                   ApprovalMode.Never
                                                   ValueNone
                                                   (fun _ _ -> Task.FromResult(TestOutput(Text = tenantId))))
                                               Seq.empty

                                       ValueTask<IReadOnlyList<ResolvedTool>>(
                                           [| tool |] :> IReadOnlyList<ResolvedTool>
                                       ))
                           )
                           :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let runForTenant tenantId =
            task {
                let! result =
                    runtime.RunAsync(
                        createAgent "Use the tenant tool.",
                        createSignature<TestOutput> (),
                        TestInput(Token = tenantId),
                        createRunOptionsWith None (Some tenantId) None StructuredOutputPolicy.NativeOnly,
                        CancellationToken.None
                    )

                return tenantId, result
            }

        let results =
            (Task.WhenAll([| for index in 0..39 -> runForTenant (if index % 2 = 0 then "tenant-a" else "tenant-b") |]))
                .Result

        for tenantId, result in results do
            Assert.True(result.Result.IsSuccess)
            Assert.Equal(tenantId, result.Result.Value.Text)

    [<Fact>]
    let ``approval requested events honor runtime serializer options`` () =
        let mutable executions = 0
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.PropertyNamingPolicy <- ApprovalNamingPolicy()

        let approvalArguments = Dictionary<string, obj>()
        approvalArguments["Title_runtime"] <- "write the report"
        approvalArguments["Payload_runtime"] <- ApprovalPayload(StreetName = "Main Street")

        let approvalFunctionCall =
            FunctionCallContent("call-7", "tool_approval_v1", approvalArguments)

        let approvalRequestContent =
            ToolApprovalRequestContent("approval-1", approvalFunctionCall)

        let tool =
            createResolvedTool
                (createToolDefinition<ApprovalInput, TestOutput>
                    "tool.approval"
                    "1.0.0"
                    "Approval tool"
                    ApprovalMode.Always
                    ValueNone
                    (Contract<ApprovalInput>.Create(CircuitJson.createOptions (), Seq.empty))
                    (Contract<TestOutput>.Create(CircuitJson.createOptions (), Seq.empty))
                    (fun _ input ->
                        executions <- executions + 1
                        Task.FromResult(TestOutput(Text = $"{input.Title}:{input.Payload.StreetName}"))))
                Seq.empty

        let primary =
            new FakeChatClient(
                (fun messages _options _ct ->
                    match tryGetFunctionResult messages with
                    | Some _ -> Task.FromResult(jsonResponse "completed")
                    | None ->
                        Task.FromResult(
                            ChatResponse(
                                ChatMessage(
                                    ChatRole.Assistant,
                                    ResizeArray<AIContent>([ approvalRequestContent :> AIContent ]) :> IList<AIContent>
                                )
                            )
                        )),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ approvalRequestContent :> AIContent ]) :> IList<AIContent>
                           )
                           ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"streamed\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime =
            createMafRuntimeWith
                (fun options ->
                    options.JsonSerializerOptions <- jsonOptions
                    options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let stream =
            (runtime :> ICircuitRuntime)
                .RunStreamingAsync(
                    createAgent "Use tools when needed.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "approval-stream"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let mutable approvalEvent = Unchecked.defaultof<RunEvent<TestOutput>>
        let mutable searching = true

        while searching && enumerator.MoveNextAsync().AsTask().Result do
            if enumerator.Current.Kind = RunEventKind.ApprovalRequested then
                approvalEvent <- enumerator.Current
                searching <- false

        Assert.False(searching, "The stream did not publish its approval request.")
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        let approval = approvalEvent.Approval.Value
        let approvalJson = approval.ArgumentsJson |> ValueOption.get

        use approvalDocument = JsonDocument.Parse(approvalJson)
        Assert.Equal("write the report", approvalDocument.RootElement.GetProperty("Title_runtime").GetString())

        Assert.Equal(
            "Main Street",
            approvalDocument.RootElement.GetProperty("Payload_runtime").GetProperty("StreetName_runtime").GetString()
        )

        let roundTrippedInput =
            JsonSerializer.Deserialize<ApprovalInput>(approvalJson, jsonOptions)

        Assert.Equal("write the report", roundTrippedInput.Title)
        Assert.Equal("Main Street", roundTrippedInput.Payload.StreetName)


    [<Fact>]
    let ``structured agent supports raw approval and typed approve or reject continuation on the same session`` () =
        let continueWithDecision approved =
            let mutable executions = 0

            let primary =
                new FakeChatClient(
                    (fun messages _options _ct ->
                        match tryGetFunctionResult messages with
                        | Some _ -> Task.FromResult(jsonResponse "{\"text\":\"completed\"}")
                        | None ->
                            let arguments = Dictionary<string, obj>()
                            arguments["path"] <- "spike.txt"
                            arguments["content"] <- "structured approval"
                            Task.FromResult(functionCallResponse "spike-call" "tool_write_v1" arguments)),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                )

            let runtime =
                createMafRuntimeWith
                    (fun options ->
                        let writeTool =
                            createResolvedTool
                                (createWriteTool ApprovalMode.Always ValueNone (fun _ input ->
                                    executions <- executions + 1
                                    Task.FromResult(WriteToolOutput(Status = $"{input.Path}:{input.Content}"))))
                                Seq.empty

                        options.ToolResolvers <- [| StaticToolResolver([| writeTool |]) :> IToolResolver |])
                    primary
                    None
                    Array.empty<Circuit.IRunObserver>

            let agent = createAgent "Use tools when needed."
            let signature = createSignature<TestOutput> ()
            let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
            let runId = RunId.New()
            let context = runtime.CreateRunContext(runId, agent, signature, runOptions)

            let tools, skills =
                match
                    runtime.ResolveCapabilitiesAsync runId context agent CancellationToken.None
                    |> _.Result
                with
                | Ok capabilities -> capabilities
                | Error failure -> failwith failure.Message

            use compiled =
                MafAgentFactory.createAgent
                    runtime.ChatClient
                    runtime.RuntimeOptions
                    context
                    agent
                    signature
                    tools
                    skills
                    false

            let session =
                compiled.Agent.CreateSessionAsync(CancellationToken.None).AsTask().Result

            let inputEnvelope =
                runtime.CreateInputEnvelope(signature, TestInput(Token = "spike"))

            let first =
                compiled.Agent.RunAsync(inputEnvelope, session, null, CancellationToken.None).Result

            let request =
                Assert.IsType<ToolApprovalRequestContent>(tryGetApprovalRequest first.Messages |> Option.get)

            let responseMessage =
                ChatMessage(
                    ChatRole.User,
                    ResizeArray<AIContent>([ request.CreateResponse(approved, "operator decision") :> AIContent ])
                    :> IList<AIContent>
                )

            let final =
                compiled.Agent
                    .RunAsync<TestOutput>(
                        [ responseMessage ],
                        session,
                        signature.JsonSerializerOptions,
                        null,
                        CancellationToken.None
                    )
                    .Result

            final.Result.Text, executions

        let approvedText, approvedExecutions = continueWithDecision true
        let rejectedText, rejectedExecutions = continueWithDecision false

        Assert.Equal("completed", approvedText)
        Assert.Equal(1, approvedExecutions)
        Assert.Equal("completed", rejectedText)
        Assert.Equal(0, rejectedExecutions)


    let private createTempSkillRoot (name: string) (description: string) (body: string) =
        let parent =
            Path.Combine(Path.GetTempPath(), $"circuit-maf-skill-{Guid.NewGuid():N}")

        let root = Path.Combine(parent, name)
        Directory.CreateDirectory(root) |> ignore

        File.WriteAllText(
            Path.Combine(root, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n{body}\n"
        )

        root

    let private deleteDirectory path =
        if Directory.Exists path then
            Directory.Delete(path, true)

    let private createSkillAgent skillReference =
        AgentDefinition.Create(
            "agent.skill",
            "1.0.0",
            "Agent Skill",
            "Use the available skills.",
            ValueNone,
            Seq.empty,
            [| skillReference |],
            Seq.empty
        )

    let private createApprovalMessage (approvalRequest: ToolApprovalRequestContent) =
        let response = approvalRequest.CreateResponse(true, "approved")

        ChatMessage(ChatRole.User, ResizeArray<AIContent>([ response :> AIContent ]) :> IList<AIContent>)

    let private createSkillResolver skillReference =
        StaticSkillResolver([| ResolvedSkill.Create(skillReference) |]) :> ISkillResolver

    let private collectRequestStrings (request: SkillScriptRequest) =
        seq {
            match request.SkillRoot with
            | ValueSome value -> yield value
            | ValueNone -> ()

            match request.ScriptPath with
            | ValueSome value -> yield value
            | ValueNone -> ()

            yield request.Skill.Id.Value
            yield request.Skill.Version.ToString()
            yield request.Skill.Description
            yield request.Skill.Source.Instructions

            for fileRoot in request.Skill.Source.FileRoots do
                yield fileRoot

            for resource in request.Skill.Source.Resources do
                yield resource.Name
                yield resource.Description

            for script in request.Skill.Source.Scripts do
                yield script.Name
                yield script.Description

                for entry in script.Metadata do
                    yield entry.Key
                    yield entry.Value

            for entry in request.Skill.Metadata do
                yield entry.Key
                yield entry.Value

            yield request.Script.Name
            yield request.Script.Description

            for entry in request.Script.Metadata do
                yield entry.Key
                yield entry.Value
        }
        |> Seq.filter (String.IsNullOrEmpty >> not)
        |> Seq.toArray

    let private createSkillSourceContext () =
        AgentSkillsSourceContext(DummyAIAgent(), DummyAgentSession())

    let private createSkillProviderInvocation () =
        AIContextProvider.InvokingContext(DummyAIAgent(), DummyAgentSession(), AIContext())

    let private createSkillRunContext skillReference =
        let agent = createSkillAgent skillReference
        RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

    let private loadSingleFileSkill options skillReference =
        MafSkillAdapter.loadFileSkill
            options
            (createSkillRunContext skillReference)
            skillReference
            skillReference.Source.FileRoots[0]

    let private disabledAgentFileScriptRunner =
        AgentFileSkillScriptRunner(fun _skill _script _arguments _services _cancellationToken ->
            Task.FromException<obj>(InvalidOperationException("Skill scripts are disabled.")))

    [<Fact>]
    let ``real maf file skills load content and resources through progressive disclosure`` () =
        let skillRoot = createTempSkillRoot "inspect-skill" "Skill" "Use the skill."
        let referencesDirectory = Path.Combine(skillRoot, "references")
        Directory.CreateDirectory(referencesDirectory) |> ignore
        File.WriteAllText(Path.Combine(referencesDirectory, "guide.md"), "Guide text")

        try
            let sourceOptions = AgentFileSkillsSourceOptions()

            use source =
                new AgentFileSkillsSource(skillRoot, disabledAgentFileScriptRunner, sourceOptions, null)

            let fileSkill =
                source.GetSkillsAsync(createSkillSourceContext (), CancellationToken.None).Result
                |> Assert.Single
                |> Assert.IsType<AgentFileSkill>

            let content = fileSkill.GetContentAsync(CancellationToken.None).Result
            Assert.Contains("Use the skill.", content)
            Assert.Contains("<available_resources>", content)
            Assert.Contains("references/guide.md", content)

            let resource =
                fileSkill.GetResourceAsync("references/guide.md", CancellationToken.None).Result

            Assert.NotNull(resource)
            Assert.Equal("Guide text", Assert.IsType<string>(resource.ReadAsync(null, CancellationToken.None).Result))
        finally
            deleteDirectory skillRoot

    [<Fact>]
    let ``maf skill provider creation rejects escaping symlink resources before provider build`` () =
        let skillRoot =
            createTempSkillRoot "secure-skill" "Secure skill" "Use the secure skill."

        let outsideRoot = Path.GetDirectoryName(skillRoot)
        let outsideFile = Path.Combine(outsideRoot, "outside.md")
        let outsideDirectory = Path.Combine(outsideRoot, "outside")
        Directory.CreateDirectory(outsideDirectory) |> ignore
        File.WriteAllText(outsideFile, "secret")
        File.WriteAllText(Path.Combine(outsideDirectory, "secret.md"), "secret")

        File.CreateSymbolicLink(Path.Combine(skillRoot, "linked.md"), outsideFile)
        |> ignore

        Directory.CreateSymbolicLink(Path.Combine(skillRoot, "linked-dir"), outsideDirectory)
        |> ignore

        try
            let skillReference =
                SkillReference.Create("skill.secure", "1.0.0", "Secure skill", SkillSource.CreateFile(skillRoot))

            let runContext = createSkillRunContext skillReference
            let options = MafRuntimeOptions()

            let ex =
                Assert.Throws<InvalidOperationException>(fun () ->
                    MafSkillAdapter.createAttachment
                        options
                        runContext
                        ([| ResolvedSkill.Create(skillReference) |] :> IReadOnlyList<ResolvedSkill>)
                    |> ignore)

            Assert.DoesNotContain(outsideFile, ex.ToString())
            Assert.DoesNotContain(outsideDirectory, ex.ToString())
        finally
            deleteDirectory skillRoot

    [<Fact>]
    let ``safe file skill resources snapshot in-root symlinks and ignore later swaps`` () =
        if not (OperatingSystem.IsLinux()) then
            ()
        else
            let skillRoot = createTempSkillRoot "linked-skill" "Skill" "Use the linked skill."
            let referencesDirectory = Path.Combine(skillRoot, "references")
            Directory.CreateDirectory(referencesDirectory) |> ignore

            let targetFile = Path.Combine(referencesDirectory, "guide.md")
            let linkedFile = Path.Combine(skillRoot, "linked.md")
            File.WriteAllText(targetFile, "Guide text")
            File.CreateSymbolicLink(linkedFile, targetFile) |> ignore

            let outsideRoot = Path.GetDirectoryName(skillRoot)
            let outsideFile = Path.Combine(outsideRoot, "outside-guide.md")
            File.WriteAllText(outsideFile, "Outside")

            try
                let skillReference =
                    SkillReference.Create("skill.linked", "1.0.0", "Linked skill", SkillSource.CreateFile(skillRoot))

                let skill = loadSingleFileSkill (MafRuntimeOptions()) skillReference

                let resource =
                    skill.GetResourceAsync("linked.md", CancellationToken.None).AsTask().Result

                Assert.NotNull(resource)

                Assert.Equal(
                    "Guide text",
                    Assert.IsType<string>(resource.ReadAsync(null, CancellationToken.None).Result)
                )

                File.Delete(linkedFile)
                File.CreateSymbolicLink(linkedFile, outsideFile) |> ignore

                let reread =
                    resource.ReadAsync(null, CancellationToken.None).GetAwaiter().GetResult()
                    |> Assert.IsType<string>

                Assert.Equal("Guide text", reread)
                Assert.DoesNotContain("Outside", reread)
            finally
                deleteDirectory skillRoot

    [<Fact>]
    let ``safe file skill resources keep snapshot content after original target disappears`` () =
        if not (OperatingSystem.IsLinux()) then
            ()
        else
            let skillRoot = createTempSkillRoot "broken-skill" "Skill" "Use the broken skill."
            let referencesDirectory = Path.Combine(skillRoot, "references")
            Directory.CreateDirectory(referencesDirectory) |> ignore

            let targetFile = Path.Combine(referencesDirectory, "guide.md")
            let linkedFile = Path.Combine(skillRoot, "linked.md")
            File.WriteAllText(targetFile, "Guide text")
            File.CreateSymbolicLink(linkedFile, targetFile) |> ignore

            try
                let skillReference =
                    SkillReference.Create("skill.broken", "1.0.0", "Broken skill", SkillSource.CreateFile(skillRoot))

                let skill = loadSingleFileSkill (MafRuntimeOptions()) skillReference

                let resource =
                    skill.GetResourceAsync("linked.md", CancellationToken.None).AsTask().Result

                File.Delete(targetFile)

                let reread =
                    resource.ReadAsync(null, CancellationToken.None).GetAwaiter().GetResult()
                    |> Assert.IsType<string>

                Assert.Equal("Guide text", reread)
            finally
                deleteDirectory skillRoot

    [<Fact>]
    let ``skill providers omit script tools and instructions when no runner is configured`` () =
        let skillRoot = createTempSkillRoot "inspect-skill" "Skill" "Use the skill."
        File.WriteAllText(Path.Combine(skillRoot, "run.py"), "print('hi')")

        try
            let skillReference =
                SkillReference.Create("skill.inspect", "1.0.0", "Inspect skill", SkillSource.CreateFile(skillRoot))

            let agent = createSkillAgent skillReference

            let runContext =
                RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

            let options = MafRuntimeOptions()

            let attachment =
                MafSkillAdapter.createAttachment
                    options
                    runContext
                    ([| ResolvedSkill.Create(skillReference) |] :> IReadOnlyList<ResolvedSkill>)
                |> ValueOption.get

            let provider = MafSkillAdapter.getProviders (ValueSome attachment) |> Assert.Single

            let aiContext =
                provider.InvokingAsync(createSkillProviderInvocation (), CancellationToken.None).AsTask().Result

            let toolNames = aiContext.Tools |> Seq.map _.Name |> Seq.toArray
            Assert.Contains(AgentSkillsProvider.LoadSkillToolName, toolNames)
            Assert.Contains(AgentSkillsProvider.ReadSkillResourceToolName, toolNames)
            Assert.DoesNotContain(AgentSkillsProvider.RunSkillScriptToolName, toolNames)
            Assert.DoesNotContain("run_skill_script", aiContext.Instructions)
        finally
            deleteDirectory skillRoot

    [<Fact>]
    let ``configured skill script tools are approval-gated and invoke the configured runner`` () =
        let skillRoot = createTempSkillRoot "inspect-skill" "Skill" "Use the skill."
        let scriptPath = Path.Combine(skillRoot, "run.py")
        File.WriteAllText(scriptPath, "print('hi')")

        let mutable executions = 0
        let mutable capturedRequest = ValueNone
        let mutable capturedSnapshotRoot = ValueNone

        try
            let skillReference =
                SkillReference.Create("skill.inspect", "1.0.0", "Inspect skill", SkillSource.CreateFile(skillRoot))

            let agent = createSkillAgent skillReference

            let runContext =
                RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

            let options = MafRuntimeOptions()

            options.SkillScriptRunner <-
                ValueSome
                    { new ISkillScriptRunner with
                        member _.RunAsync(request, _cancellationToken) =
                            executions <- executions + 1
                            capturedRequest <- ValueSome request
                            Task.FromResult(SkillScriptResult.Create(box "script-output")) }

            let attachment =
                MafSkillAdapter.createAttachment
                    options
                    runContext
                    ([| ResolvedSkill.Create(skillReference) |] :> IReadOnlyList<ResolvedSkill>)
                |> ValueOption.get

            let provider = MafSkillAdapter.getProviders (ValueSome attachment) |> Assert.Single

            let aiContext =
                provider.InvokingAsync(createSkillProviderInvocation (), CancellationToken.None).AsTask().Result

            let runScriptTool =
                aiContext.Tools
                |> Seq.find (fun tool ->
                    StringComparer.Ordinal.Equals(tool.Name, AgentSkillsProvider.RunSkillScriptToolName))

            Assert.Equal("ApprovalRequiredAIFunction", runScriptTool.GetType().Name)
            Assert.Equal(0, executions)

            let runScriptFunction = runScriptTool |> Assert.IsAssignableFrom<AIFunction>
            let arguments = Dictionary<string, obj>()
            arguments["skillName"] <- "inspect-skill"
            arguments["scriptName"] <- "run.py"

            use argumentDocument = JsonDocument.Parse("[\"hello\"]")
            arguments["arguments"] <- argumentDocument.RootElement.Clone()

            let result =
                runScriptFunction.InvokeAsync(AIFunctionArguments(arguments), CancellationToken.None).AsTask().Result

            let resultElement = Assert.IsType<JsonElement>(result)
            Assert.Equal(JsonValueKind.String, resultElement.ValueKind)
            Assert.Equal("script-output", resultElement.GetString())
            Assert.Equal(1, executions)

            match capturedRequest with
            | ValueSome request ->
                Assert.Equal("run.py", request.Script.Name)
                Assert.True(request.SkillRoot.IsSome)
                Assert.True(request.ScriptPath.IsSome)
                Assert.NotEqual(ValueSome(Path.GetFullPath skillRoot), request.SkillRoot)
                Assert.NotEqual(ValueSome(Path.GetFullPath scriptPath), request.ScriptPath)
                Assert.True(Directory.Exists(request.SkillRoot.Value))
                Assert.True(File.Exists(request.ScriptPath.Value))
                Assert.Equal("print('hi')", File.ReadAllText(request.ScriptPath.Value))
                Assert.True((request.Skill.Source.FileRoots |> Seq.toArray) = [| request.SkillRoot.Value |])

                let requestStrings = collectRequestStrings request

                let pathComparison =
                    if OperatingSystem.IsWindows() then
                        StringComparison.OrdinalIgnoreCase
                    else
                        StringComparison.Ordinal

                Assert.DoesNotContain(
                    requestStrings,
                    fun value -> value.IndexOf(Path.GetFullPath skillRoot, pathComparison) >= 0
                )

                Assert.DoesNotContain(
                    requestStrings,
                    fun value -> value.IndexOf(Path.GetFullPath scriptPath, pathComparison) >= 0
                )

                if not (OperatingSystem.IsWindows()) then
                    let expectedDirectoryMode =
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

                    let expectedFileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                    Assert.Equal(expectedDirectoryMode, File.GetUnixFileMode(request.SkillRoot.Value))
                    Assert.Equal(expectedFileMode, File.GetUnixFileMode(request.ScriptPath.Value))

                capturedSnapshotRoot <- request.SkillRoot
                Assert.True(request.Arguments.HasValue)
                Assert.Equal(1, request.Arguments.Value.GetArrayLength())

                let argumentValue = request.Arguments.Value.EnumerateArray() |> Seq.exactlyOne
                Assert.Equal("hello", argumentValue.GetString())
            | ValueNone -> Assert.Fail("Expected the skill script runner to receive a request.")

            (attachment :> IDisposable).Dispose()

            match capturedSnapshotRoot with
            | ValueSome snapshotRoot -> Assert.False(Directory.Exists snapshotRoot)
            | ValueNone -> Assert.Fail("Expected a snapshot root path to be captured.")
        finally
            deleteDirectory skillRoot

    [<Fact>]
    let ``safe file skill scripts use immutable snapshot paths after symlink swaps`` () =
        if not (OperatingSystem.IsLinux()) then
            ()
        else
            let skillRoot = createTempSkillRoot "script-skill" "Skill" "Use the script skill."
            let scriptsDirectory = Path.Combine(skillRoot, "scripts")
            Directory.CreateDirectory(scriptsDirectory) |> ignore

            let targetScript = Path.Combine(scriptsDirectory, "run.py")
            let linkedScript = Path.Combine(skillRoot, "run.py")
            File.WriteAllText(targetScript, "print('safe')")
            File.CreateSymbolicLink(linkedScript, targetScript) |> ignore

            let outsideRoot = Path.GetDirectoryName(skillRoot)
            let outsideScript = Path.Combine(outsideRoot, "outside-run.py")
            File.WriteAllText(outsideScript, "print('outside')")

            let mutable executions = 0
            let mutable capturedRequest = ValueNone

            try
                let skillReference =
                    SkillReference.Create("skill.script", "1.0.0", "Script skill", SkillSource.CreateFile(skillRoot))

                let options = MafRuntimeOptions()

                options.SkillScriptRunner <-
                    ValueSome
                        { new ISkillScriptRunner with
                            member _.RunAsync(request, _cancellationToken) =
                                executions <- executions + 1
                                capturedRequest <- ValueSome request
                                Task.FromResult(SkillScriptResult.Create(box "script-output")) }

                let skill = loadSingleFileSkill options skillReference

                let script = skill.GetScriptAsync("run.py", CancellationToken.None).AsTask().Result

                let initialResult =
                    script.RunAsync(skill, Nullable(), null, CancellationToken.None).GetAwaiter().GetResult()

                Assert.Equal("script-output", Assert.IsType<string>(initialResult))
                Assert.Equal(1, executions)

                let initialSnapshotPath =
                    match capturedRequest with
                    | ValueSome request ->
                        Assert.True(request.ScriptPath.IsSome)
                        Assert.NotEqual(ValueSome(Path.GetFullPath targetScript), request.ScriptPath)
                        request.ScriptPath.Value
                    | ValueNone ->
                        Assert.Fail("Expected an initial script invocation.")
                        String.Empty

                File.Delete(linkedScript)
                File.CreateSymbolicLink(linkedScript, outsideScript) |> ignore

                let rerunResult =
                    script.RunAsync(skill, Nullable(), null, CancellationToken.None).GetAwaiter().GetResult()

                Assert.Equal("script-output", Assert.IsType<string>(rerunResult))
                Assert.Equal(2, executions)

                match capturedRequest with
                | ValueSome request ->
                    Assert.Equal(ValueSome initialSnapshotPath, request.ScriptPath)
                    Assert.DoesNotContain(outsideScript, request.ScriptPath.Value)
                | ValueNone -> Assert.Fail("Expected the snapshot-backed script invocation.")
            finally
                deleteDirectory skillRoot

    [<Fact>]
    let ``safe file skill scripts keep snapshot content after original target disappears`` () =
        if not (OperatingSystem.IsLinux()) then
            ()
        else
            let skillRoot =
                createTempSkillRoot "broken-script-skill" "Skill" "Use the script skill."

            let scriptsDirectory = Path.Combine(skillRoot, "scripts")
            Directory.CreateDirectory(scriptsDirectory) |> ignore

            let targetScript = Path.Combine(scriptsDirectory, "run.py")
            let linkedScript = Path.Combine(skillRoot, "run.py")
            File.WriteAllText(targetScript, "print('safe')")
            File.CreateSymbolicLink(linkedScript, targetScript) |> ignore

            let mutable executions = 0

            try
                let skillReference =
                    SkillReference.Create(
                        "skill.broken-script",
                        "1.0.0",
                        "Broken script skill",
                        SkillSource.CreateFile(skillRoot)
                    )

                let options = MafRuntimeOptions()

                options.SkillScriptRunner <-
                    ValueSome
                        { new ISkillScriptRunner with
                            member _.RunAsync(_request, _cancellationToken) =
                                executions <- executions + 1
                                Task.FromResult(SkillScriptResult.Create(box "script-output")) }

                let skill = loadSingleFileSkill options skillReference

                let script = skill.GetScriptAsync("run.py", CancellationToken.None).AsTask().Result

                File.Delete(targetScript)

                let result =
                    script.RunAsync(skill, Nullable(), null, CancellationToken.None).GetAwaiter().GetResult()

                Assert.Equal("script-output", Assert.IsType<string>(result))
                Assert.Equal(1, executions)
            finally
                deleteDirectory skillRoot

    [<Fact>]
    let ``snapshot capture rejects symlink swaps between validation and open`` () =
        if not (OperatingSystem.IsLinux()) then
            ()
        else
            let skillRoot = createTempSkillRoot "swap-skill" "Skill" "Use the swap skill."
            let referencesDirectory = Path.Combine(skillRoot, "references")
            Directory.CreateDirectory(referencesDirectory) |> ignore

            let targetFile = Path.Combine(referencesDirectory, "guide.md")
            let linkedFile = Path.Combine(skillRoot, "linked.md")
            File.WriteAllText(targetFile, "Guide text")
            File.CreateSymbolicLink(linkedFile, targetFile) |> ignore

            let outsideRoot = Path.GetDirectoryName(skillRoot)
            let outsideFile = Path.Combine(outsideRoot, "outside-guide.md")
            File.WriteAllText(outsideFile, "Outside")

            try
                let canonicalRoot = SkillPathSecurity.validateSkillRootPath skillRoot

                let hooks: MafSkillAdapter.SnapshotCaptureHooks =
                    { BeforeOpen =
                        ValueSome(
                            Action<MafSkillAdapter.SnapshotCaptureEntryState>(fun _ ->
                                File.Delete(linkedFile)
                                File.CreateSymbolicLink(linkedFile, outsideFile) |> ignore)
                        ) }

                let ex =
                    Assert.Throws<InvalidOperationException>(fun () ->
                        MafSkillAdapter.SnapshotCapture.readTextFileWithHooks
                            canonicalRoot
                            linkedFile
                            "linked.md"
                            hooks
                        |> ignore)

                Assert.DoesNotContain(outsideFile, ex.ToString())
            finally
                deleteDirectory skillRoot

    [<Fact>]
    let ``materialized snapshots report cleanup success and remove private roots`` () =
        let skillRoot = createTempSkillRoot "cleanup-skill" "Skill" "Use the cleanup skill."
        File.WriteAllText(Path.Combine(skillRoot, "run.py"), "print('hi')")

        try
            let skillReference =
                SkillReference.Create("skill.cleanup", "1.0.0", "Cleanup skill", SkillSource.CreateFile(skillRoot))

            let preparedSkill =
                MafSkillAdapter.prepareResolvedSkill (ResolvedSkill.Create(skillReference))

            let preparedFileSkills =
                preparedSkill.TryGetProperty<MafSkillAdapter.MafPreparedFileSkills>(
                    MafSkillAdapterProperties.FileSkills
                )
                |> ValueOption.get

            let snapshot = preparedFileSkills.Snapshots.Values |> Seq.exactlyOne

            let materializedRoot =
                match snapshot.TryResolveScriptLocation("run.py") with
                | ValueSome(struct (root, _)) ->
                    Assert.True(Directory.Exists root)
                    root
                | ValueNone ->
                    Assert.Fail("Expected a materialized snapshot script location.")
                    String.Empty

            (preparedFileSkills :> IDisposable).Dispose()

            Assert.Equal(MafSkillAdapter.SnapshotCleanupResult.Deleted, snapshot.CleanupResult)
            Assert.False(Directory.Exists materializedRoot)
        finally
            deleteDirectory skillRoot

    [<Fact>]
    let ``materialized snapshot cleanup failures are sanitized and non-fatal`` () =
        let skillRoot =
            createTempSkillRoot "cleanup-failure-skill" "Skill" "Use the cleanup failure skill."

        let canonicalRoot = SkillPathSecurity.validateSkillRootPath skillRoot
        let runPath = Path.Combine(skillRoot, "run.py")
        File.WriteAllText(runPath, "print('hi')")

        let fileContents =
            Dictionary<string, string>(seq [ KeyValuePair("run.py", "print('hi')") ], StringComparer.Ordinal)
            |> fun value -> value.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let resolvedPaths =
            Dictionary<string, string>(
                seq
                    [ KeyValuePair("SKILL.md", Path.Combine(skillRoot, "SKILL.md"))
                      KeyValuePair("run.py", runPath) ],
                StringComparer.Ordinal
            )
            |> fun value -> value.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let emptyResourceNames =
            HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>

        let scriptNames =
            HashSet<string>(seq [ "run.py" ], StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal)
            :> IReadOnlySet<string>

        let mutable createdRoot = String.Empty

        let snapshot =
            new MafSkillAdapter.MafFileSkillSnapshot(
                canonicalRoot,
                File.ReadAllText(Path.Combine(skillRoot, "SKILL.md")),
                fileContents,
                resolvedPaths,
                emptyResourceNames,
                scriptNames,
                "fingerprint",
                Func<string>(fun () ->
                    createdRoot <- Path.Combine(Path.GetTempPath(), $"circuit-maf-cleanup-failure-{Guid.NewGuid():N}")
                    Directory.CreateDirectory(createdRoot) |> ignore
                    createdRoot),
                Action<string>(fun _ -> raise (IOException($"super-secret cleanup failure at {skillRoot}")))
            )

        let prepared =
            new MafSkillAdapter.MafPreparedFileSkills(
                Dictionary<string, MafSkillAdapter.MafFileSkillSnapshot>(
                    seq [ KeyValuePair(canonicalRoot, snapshot) ],
                    SkillPathSecurity.PathComparer
                )
                    .ToFrozenDictionary(SkillPathSecurity.PathComparer)
                :> IReadOnlyDictionary<string, MafSkillAdapter.MafFileSkillSnapshot>
            )

        try
            match snapshot.TryResolveScriptLocation("run.py") with
            | ValueSome _ -> ()
            | ValueNone -> Assert.Fail("Expected a materialized snapshot before cleanup.")

            let ex = Record.Exception(fun () -> (prepared :> IDisposable).Dispose())
            Assert.Null(ex)

            match snapshot.CleanupResult with
            | MafSkillAdapter.SnapshotCleanupResult.Failed reason ->
                Assert.Equal("IOException", reason)
                Assert.DoesNotContain(skillRoot, reason)
            | other -> Assert.Fail($"Expected a sanitized cleanup failure, got {other}.")

            Assert.True(Directory.Exists createdRoot)
        finally
            deleteDirectory createdRoot
            deleteDirectory skillRoot

    [<Fact>]
    let ``skill script failures are sanitized before becoming model-visible output`` () =
        let skillRoot = createTempSkillRoot "inspect-skill" "Skill" "Use the skill."
        let scriptPath = Path.Combine(skillRoot, "run.py")
        File.WriteAllText(scriptPath, "print('hi')")

        try
            let skillReference =
                SkillReference.Create("skill.inspect", "1.0.0", "Inspect skill", SkillSource.CreateFile(skillRoot))

            let agent = createSkillAgent skillReference

            let runContext =
                RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

            let options = MafRuntimeOptions()

            options.SkillScriptRunner <-
                ValueSome
                    { new ISkillScriptRunner with
                        member _.RunAsync(_request, _cancellationToken) =
                            Task.FromException<SkillScriptResult>(
                                InvalidOperationException($"super-secret failure at {Path.GetFullPath scriptPath}")
                            ) }

            let attachment =
                MafSkillAdapter.createAttachment
                    options
                    runContext
                    ([| ResolvedSkill.Create(skillReference) |] :> IReadOnlyList<ResolvedSkill>)
                |> ValueOption.get

            let provider = MafSkillAdapter.getProviders (ValueSome attachment) |> Assert.Single

            let aiContext =
                provider.InvokingAsync(createSkillProviderInvocation (), CancellationToken.None).AsTask().Result

            let runScriptFunction =
                aiContext.Tools
                |> Seq.find (fun tool ->
                    StringComparer.Ordinal.Equals(tool.Name, AgentSkillsProvider.RunSkillScriptToolName))
                |> Assert.IsAssignableFrom<AIFunction>

            let arguments = Dictionary<string, obj>()
            arguments["skillName"] <- "inspect-skill"
            arguments["scriptName"] <- "run.py"

            let ex =
                Assert.Throws<InvalidOperationException>(fun () ->
                    runScriptFunction
                        .InvokeAsync(AIFunctionArguments(arguments), CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult()
                    |> ignore)

            Assert.DoesNotContain("super-secret", ex.Message)
            Assert.DoesNotContain(Path.GetFullPath scriptPath, ex.Message)
            Assert.Equal("Skill script execution failed.", ex.Message)
        finally
            deleteDirectory skillRoot
