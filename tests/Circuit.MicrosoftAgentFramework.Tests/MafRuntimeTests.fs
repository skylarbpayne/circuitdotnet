namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Runtime.Loader
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Xunit

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

type RecordingObserver() =
    let observations = ResizeArray<MafRunObservation>()
    let events = ResizeArray<MafObservedEvent>()

    member _.Observations = observations
    member _.Events = events

    interface IRunObserver with
        member _.OnRunStartedAsync(_context, _startedAt, _cancellationToken) = ValueTask()

        member _.OnRunEventAsync(event, _cancellationToken) =
            events.Add event
            ValueTask()

        member _.OnRunCompletedAsync(observation, _cancellationToken) =
            observations.Add observation
            ValueTask()

module private Helpers =
    let private runOptionsCtor =
        typeof<RunOptions>
            .GetConstructor(
                BindingFlags.Instance ||| BindingFlags.NonPublic,
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

    let createRunOptionsWith
        (session: CircuitSession option)
        (tenantId: string option)
        (userId: string option)
        (policy: StructuredOutputPolicy)
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
               box SensitiveDataMode.Standard
               box (NullServiceProvider() :> IServiceProvider) |]
        )
        :?> RunOptions

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

    let buildSessionAgent (runtime: MafRuntime) agent =
        match (runtime.BuildSessionAgentAsync (RunId.New()) agent CancellationToken.None).Result with
        | Ok sessionAgent -> sessionAgent
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
                (fun _messages _options _ct -> Task.FromResult(jsonResponse (queue.Dequeue()))),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        Assert.True(objectResult.Result.IsSuccess)
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
                (fun _messages _options _ct -> Task.FromResult(jsonResponse (queue.Dequeue()))),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

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

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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

        let runtime = createRuntime streamingClient None [| observer :> IRunObserver |]

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
            |> Seq.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(observerTerminalEvents) |> ignore
        Assert.Equal(RunEventKind.RunFailed, observerTerminalEvents[0].Kind)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(CircuitFailureCode.Decode, observer.Observations[0].Failure.Value.Code)
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

        let runtime = createRuntime streamingClient None [| observer :> IRunObserver |]

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
            |> Seq.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(observerTerminalEvents) |> ignore
        Assert.Equal(RunEventKind.RunFailed, observerTerminalEvents[0].Kind)
        Assert.Single(observer.Observations) |> ignore
        Assert.Equal(CircuitFailureCode.Cancelled, observer.Observations[0].Failure.Value.Code)
        Assert.Equal(0, streamingClient.StreamingCalls)

    [<Fact>]
    let ``cancellation reaches the fake client`` () =
        let tcs = TaskCompletionSource<ChatResponse>()

        let fake =
            new FakeChatClient(
                (fun _messages _options cancellationToken ->
                    cancellationToken.Register(fun () -> tcs.TrySetCanceled(cancellationToken) |> ignore)
                    |> ignore

                    tcs.Task),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime fake None Array.empty<IRunObserver>
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
    let ``native only does not make a second model call`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"native\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"secondary\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime primary (Some secondary) Array.empty<IRunObserver>

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
        Assert.Equal(1, primary.ResponseCalls)
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
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let secondary =
            new FakeChatClient(
                (fun _messages options _ct ->
                    secondaryOptions.Add options
                    Task.FromResult(jsonResponse "{\"text\":\"repaired\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime primary (Some secondary) [| observer :> IRunObserver |]

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
        Assert.Equal(1, primary.ResponseCalls)
        Assert.Equal(1, secondary.ResponseCalls)
        Assert.Single(primaryOptions) |> ignore
        Assert.Null(primaryOptions[0].ResponseFormat)
        Assert.Single(secondaryOptions) |> ignore
        Assert.NotNull(secondaryOptions[0].ResponseFormat)
        Assert.Single(observer.Observations) |> ignore
        Assert.True(observer.Observations[0].Repaired)
        Assert.Equal("true", observer.Observations[0].DiagnosticMetadata["circuit.repaired"])

        Assert.Equal(
            "unstructured result",
            observer.Observations[0].DiagnosticMetadata["circuit.repair.originalResponse"]
        )

        Assert.True(
            observer.Events
            |> Seq.exists (fun event -> event.Kind = RunEventKind.RunCompleted)
        )

    [<Fact>]
    let ``repaired run usage aggregates primary and repair usage`` () =
        let observer = RecordingObserver()
        let primaryUsage = usageDetails 10 20
        let repairUsage = usageDetails 2 3

        let primary =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    Task.FromResult(jsonResponseWithUsage "unstructured result" primaryUsage)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    Task.FromResult(jsonResponseWithUsage "{\"text\":\"repaired\"}" repairUsage)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime primary (Some secondary) [| observer :> IRunObserver |]

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

        let runtime = createRuntime primary (Some secondary) [| observer :> IRunObserver |]

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
                Array.empty<IRunObserver>

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

        let functionCall = FunctionCallContent("call-7", "fetch_weather", arguments)
        let approval = ToolApprovalRequestContent("approval-1", functionCall)

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ approval :> AIContent ]) :> IList<AIContent>
                           )
                           ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"pending\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "approval"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        arguments["city"] <- "London"

        let approvalEvent =
            Assert.Single(
                events
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let approvalRequest = approvalEvent.Approval.Value
        Assert.Equal(ValueSome "call-7", approvalEvent.OperationId)
        Assert.Equal("approval-1", approvalRequest.RequestId)
        Assert.Equal("fetch_weather", approvalRequest.ToolName)

        let argumentsJson = approvalRequest.ArgumentsJson |> ValueOption.get
        use argumentsDocument = JsonDocument.Parse(argumentsJson)
        Assert.Equal("Paris", argumentsDocument.RootElement.GetProperty("city").GetString())
        Assert.Equal(3, argumentsDocument.RootElement.GetProperty("days").GetInt32())

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

        let functionCall = FunctionCallContent("call-8", "", arguments)
        let approval = ToolApprovalRequestContent("approval-2", functionCall)

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ approval :> AIContent ]) :> IList<AIContent>
                           )
                           ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"pending\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "approval-blank"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        let approvalEvent =
            Assert.Single(
                events
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let approvalRequest = approvalEvent.Approval.Value
        Assert.Equal(ValueSome "call-8", approvalEvent.OperationId)
        Assert.Equal("approval-2", approvalRequest.RequestId)
        Assert.Equal("unknown-tool-call", approvalRequest.ToolName)
        Assert.True(approvalRequest.ArgumentsJson.IsNone)

    [<Fact>]
    let ``approval requested events handle unknown tool-call content safely`` () =
        let approval =
            ToolApprovalRequestContent("approval-2", UnknownToolCallContent("call-9"))

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ approval :> AIContent ]) :> IList<AIContent>
                           )
                           ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"pending\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "approval-unknown"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        let approvalEvent =
            Assert.Single(
                events
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let approvalRequest = approvalEvent.Approval.Value
        Assert.Equal(ValueSome "call-9", approvalEvent.OperationId)
        Assert.Equal("approval-2", approvalRequest.RequestId)
        Assert.Equal("unknown-tool-call", approvalRequest.ToolName)
        Assert.True(approvalRequest.ArgumentsJson.IsNone)

    [<Fact>]
    let ``cancellation during observer startup returns cancelled without invoking the provider`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        use cts = new CancellationTokenSource()

        let observer =
            { new IRunObserver with
                member _.OnRunStartedAsync(_context, _startedAt, _cancellationToken) =
                    cts.Cancel()
                    ValueTask(Task.FromCanceled(cts.Token))

                member _.OnRunEventAsync(_event, _cancellationToken) = ValueTask()
                member _.OnRunCompletedAsync(_observation, _cancellationToken) = ValueTask() }

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
                Array.empty<IRunObserver>

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
                               member _.ResolveSkillsAsync(_context, _cancellationToken) =
                                   use cancelled = new CancellationTokenSource()
                                   cancelled.Cancel()

                                   ValueTask<IReadOnlyList<ResolvedSkill>>(
                                       Task.FromCanceled<IReadOnlyList<ResolvedSkill>>(cancelled.Token)
                                   ) } |])
                primary
                None
                Array.empty<IRunObserver>

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
                Array.empty<IRunObserver>

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
                Array.empty<IRunObserver>

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
    let ``approval requested events and approval responses honor runtime serializer options`` () =
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
                Array.empty<IRunObserver>

        let streamingEvents =
            (runtime :> ICircuitRuntime)
                .RunStreamingAsync(
                    createAgent "Use tools when needed.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "approval-stream"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
            |> collectStreamEvents

        let approvalEvent =
            Assert.Single(
                streamingEvents
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

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

        let sessionAgent = buildSessionAgent runtime (createAgent "Use tools when needed.")

        let session =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let initialResponse =
            sessionAgent
                .RunAsync([ ChatMessage(ChatRole.User, "write the report") ], session, null, CancellationToken.None)
                .Result

        let sessionApprovalRequest =
            Assert.IsType<ToolApprovalRequestContent>(tryGetApprovalRequest initialResponse.Messages |> Option.get)

        Assert.Equal(approvalRequestContent.RequestId, sessionApprovalRequest.RequestId)

        let roundTrippedArguments = Dictionary<string, obj>()
        roundTrippedArguments["Title_runtime"] <- roundTrippedInput.Title
        roundTrippedArguments["Payload_runtime"] <- roundTrippedInput.Payload

        let approvalResponse =
            ToolApprovalResponseContent(
                approval.RequestId,
                true,
                FunctionCallContent(approvalEvent.OperationId.Value, "tool_approval_v1", roundTrippedArguments)
            )

        let finalResponse =
            sessionAgent
                .RunAsync(
                    [ ChatMessage(
                          ChatRole.User,
                          ResizeArray<AIContent>([ approvalResponse :> AIContent ]) :> IList<AIContent>
                      ) ],
                    session,
                    null,
                    CancellationToken.None
                )
                .Result

        Assert.Equal("completed", finalResponse.Text)
        Assert.Equal(1, executions)

    [<Fact>]
    let ``write tool pauses for approval and executes only after an affirmative matching response`` () =
        let mutable executions = 0

        let primary =
            new FakeChatClient(
                (fun messages _options _ct ->
                    match tryGetFunctionResult messages with
                    | Some _ -> Task.FromResult(jsonResponse "completed")
                    | None ->
                        let arguments = Dictionary<string, obj>()
                        arguments["path"] <- "notes.txt"
                        arguments["content"] <- "hello"
                        Task.FromResult(functionCallResponse "write-call" "tool_write_v1" arguments)),
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
                Array.empty<IRunObserver>

        let sessionAgent = buildSessionAgent runtime (createAgent "Use tools when needed.")

        let session =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        Assert.Equal(0, executions)

        let initialResponse =
            sessionAgent
                .RunAsync([ ChatMessage(ChatRole.User, "write the file") ], session, null, CancellationToken.None)
                .Result

        let approvalRequest =
            Assert.IsType<ToolApprovalRequestContent>(tryGetApprovalRequest initialResponse.Messages |> Option.get)

        Assert.Equal(0, executions)

        let wrongApprovalResponse =
            ToolApprovalResponseContent("wrong-request", true, approvalRequest.ToolCall)

        let wrongApprovalMessage =
            ChatMessage(
                ChatRole.User,
                ResizeArray<AIContent>([ wrongApprovalResponse :> AIContent ]) :> IList<AIContent>
            )

        sessionAgent.RunAsync([ wrongApprovalMessage ], session, null, CancellationToken.None).Result
        |> ignore

        Assert.Equal(0, executions)

        let matchingApprovalResponse = approvalRequest.CreateResponse(true, "approved")

        let matchingApprovalMessage =
            ChatMessage(
                ChatRole.User,
                ResizeArray<AIContent>([ matchingApprovalResponse :> AIContent ]) :> IList<AIContent>
            )

        let finalResponse =
            sessionAgent.RunAsync([ matchingApprovalMessage ], session, null, CancellationToken.None).Result

        Assert.Equal(1, executions)
        Assert.Equal("completed", finalResponse.Text)

    [<Fact>]
    let ``approval responses require the original pending tool call and cannot be replayed across runs`` () =
        let mutable executions = 0

        let primary =
            new FakeChatClient(
                (fun messages _options _ct ->
                    match tryGetFunctionResult messages with
                    | Some _ -> Task.FromResult(jsonResponse "completed")
                    | None ->
                        let prompt = messages[messages.Count - 1].Text
                        let arguments = Dictionary<string, obj>()

                        arguments["path"] <-
                            if prompt.Contains("second", StringComparison.Ordinal) then
                                "second.txt"
                            else
                                "first.txt"

                        arguments["content"] <-
                            if prompt.Contains("second", StringComparison.Ordinal) then
                                "goodbye"
                            else
                                "hello"

                        Task.FromResult(functionCallResponse "write-call" "tool_write_v1" arguments)),
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
                Array.empty<IRunObserver>

        let sessionAgent = buildSessionAgent runtime (createAgent "Use tools when needed.")

        let createUserMessage (content: AIContent) =
            ChatMessage(ChatRole.User, ResizeArray<AIContent>([ content ]) :> IList<AIContent>)

        let session1 =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let request1 =
            sessionAgent
                .RunAsync(
                    [ ChatMessage(ChatRole.User, "write the first file") ],
                    session1,
                    null,
                    CancellationToken.None
                )
                .Result
            |> fun response ->
                Assert.IsType<ToolApprovalRequestContent>(tryGetApprovalRequest response.Messages |> Option.get)

        let session2 =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let request2 =
            sessionAgent
                .RunAsync(
                    [ ChatMessage(ChatRole.User, "write the second file") ],
                    session2,
                    null,
                    CancellationToken.None
                )
                .Result
            |> fun response ->
                Assert.IsType<ToolApprovalRequestContent>(tryGetApprovalRequest response.Messages |> Option.get)

        let substitutedArguments = Dictionary<string, obj>()
        substitutedArguments["path"] <- "hacked.txt"
        substitutedArguments["content"] <- "tampered"

        let substitutedToolCall =
            FunctionCallContent("write-call", "tool_write_v1", substitutedArguments)

        let substitutedResponse =
            ToolApprovalResponseContent(request1.RequestId, true, substitutedToolCall)

        (sessionAgent
            .RunAsync([ createUserMessage (substitutedResponse :> AIContent) ], session1, null, CancellationToken.None)
            .Result)
        |> ignore

        Assert.Equal(0, executions)

        let crossRunResponse = request1.CreateResponse(true, "approved")

        (sessionAgent
            .RunAsync([ createUserMessage (crossRunResponse :> AIContent) ], session2, null, CancellationToken.None)
            .Result)
        |> ignore

        Assert.Equal(0, executions)

        let finalResponse1 =
            sessionAgent
                .RunAsync(
                    [ createUserMessage (request1.CreateResponse(true, "approved") :> AIContent) ],
                    session1,
                    null,
                    CancellationToken.None
                )
                .Result

        Assert.Equal(1, executions)
        Assert.Equal("completed", finalResponse1.Text)

        (sessionAgent
            .RunAsync(
                [ createUserMessage (request1.CreateResponse(true, "approved") :> AIContent) ],
                session1,
                null,
                CancellationToken.None
            )
            .Result)
        |> ignore

        Assert.Equal(1, executions)

        let finalResponse2 =
            sessionAgent
                .RunAsync(
                    [ createUserMessage (request2.CreateResponse(true, "approved") :> AIContent) ],
                    session2,
                    null,
                    CancellationToken.None
                )
                .Result

        Assert.Equal(2, executions)
        Assert.Equal("completed", finalResponse2.Text)

    [<Fact>]
    let ``pending approval snapshots update atomically and each approval is consumed at most once`` () =
        let pendingCount = 8

        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createMafRuntimeWith ignore primary None Array.empty<IRunObserver>
        let sessionAgent = buildSessionAgent runtime (createAgent "Use tools when needed.")

        let session =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let jsonOptions = CircuitJson.createOptions ()

        let approvalModule =
            typeof<MafRuntime>.Assembly.GetType("Circuit.MicrosoftAgentFramework.MafApprovalResponses", true)

        let captureMethod =
            approvalModule.GetMethod(
                "captureOutboundApprovalRequests",
                BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
            )

        let filterMethod =
            approvalModule.GetMethod(
                "filterInboundApprovalResponses",
                BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public
            )

        Assert.NotNull(captureMethod)
        Assert.NotNull(filterMethod)

        let createArguments index =
            let arguments = Dictionary<string, obj>()
            arguments["path"] <- $"file-{index}.txt"
            arguments["content"] <- $"content-{index}"
            arguments :> IDictionary<string, obj>

        let createApprovalRequest index =
            ToolApprovalRequestContent(
                $"approval-{index}",
                FunctionCallContent($"write-call-{index}", "tool_write_v1", createArguments index)
            )

        let createApprovalResponse index =
            ToolApprovalResponseContent(
                $"approval-{index}",
                true,
                FunctionCallContent($"write-call-{index}", "tool_write_v1", createArguments index)
            )

        let createCaptureResponse index =
            AgentResponse(
                [| ChatMessage(
                       ChatRole.Assistant,
                       ResizeArray<AIContent>([ createApprovalRequest index :> AIContent ]) :> IList<AIContent>
                   ) |]
                :> IList<ChatMessage>
            )

        let invokeCapture index =
            captureMethod.Invoke(null, [| box (createCaptureResponse index); box session; box jsonOptions |])
            |> ignore

        let invokeFilter index =
            let message =
                ChatMessage(
                    ChatRole.User,
                    ResizeArray<AIContent>([ createApprovalResponse index :> AIContent ]) :> IList<AIContent>
                )

            let result =
                filterMethod.Invoke(
                    null,
                    [| box ([| message |] :> IEnumerable<ChatMessage>)
                       box session
                       box jsonOptions |]
                )
                :?> (IReadOnlyList<ChatMessage> * bool)

            let filteredMessages, droppedInvalidResponses = result

            let approvalsPassedThrough =
                filteredMessages
                |> Seq.collect (fun filteredMessage ->
                    if isNull filteredMessage.Contents then
                        Seq.empty
                    else
                        filteredMessage.Contents :> seq<AIContent>)
                |> Seq.choose (fun content ->
                    match content with
                    | :? ToolApprovalResponseContent as approvalResponse -> Some approvalResponse
                    | _ -> None)
                |> Seq.toArray

            approvalsPassedThrough, droppedInvalidResponses

        use captureStart = new ManualResetEventSlim(false)

        let captureTasks =
            [| for index in 1..pendingCount ->
                   Task.Run(fun () ->
                       captureStart.Wait()
                       invokeCapture index) |]

        captureStart.Set()
        Task.WhenAll(captureTasks).Wait()

        use consumeStart = new ManualResetEventSlim(false)

        let consumeTasks =
            [| for index in 1..pendingCount do
                   for _ in 1..4 ->
                       Task.Run(fun () ->
                           consumeStart.Wait()
                           index, invokeFilter index) |]

        consumeStart.Set()

        let consumeResults = Task.WhenAll(consumeTasks).Result

        for index in 1..pendingCount do
            let approvalsPassedThrough =
                consumeResults
                |> Array.filter (fun (resultIndex, _) -> resultIndex = index)
                |> Array.sumBy (fun (_, (approvals, _dropped)) -> approvals.Length)

            Assert.Equal(1, approvalsPassedThrough)

        for index in 1..pendingCount do
            let approvalsPassedThrough, droppedInvalidResponses = invokeFilter index
            Assert.Empty approvalsPassedThrough
            Assert.True(droppedInvalidResponses)

    [<Fact>]
    let ``approval mode by-policy fails closed when policy configuration is missing`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let createPolicyTool approvalPolicy =
            createResolvedTool
                (createTestTool "tool.policy" ApprovalMode.ByPolicy approvalPolicy (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "ok"))))
                Seq.empty

        let runtimeWithoutHostPolicy =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| StaticToolResolver([| createPolicyTool (ValueSome "allow") |]) :> IToolResolver |])
                primary
                None
                Array.empty<IRunObserver>

        let resultWithoutHostPolicy =
            runtimeWithoutHostPolicy
                .RunAsync(
                    createAgent "Use tools when needed.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "policy"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(resultWithoutHostPolicy.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, resultWithoutHostPolicy.Result.Failure.Code)
        Assert.Contains("ToolApprovalPolicy", resultWithoutHostPolicy.Result.Failure.Message)
        Assert.Equal(0, primary.ResponseCalls)

        let runtimeWithoutNamedPolicy =
            createRuntimeWith
                (fun options ->
                    options.ToolApprovalPolicy <- ValueSome(StubToolApprovalPolicy(true) :> IToolApprovalPolicy)

                    let toolWithoutNamedPolicy =
                        createResolvedTool
                            (createToolDefinition<TestInput, TestOutput>
                                "tool.policy"
                                "1.0.0"
                                "Policy tool"
                                ApprovalMode.ByPolicy
                                ValueNone
                                (Contract<TestInput>.Create(CircuitJson.createOptions (), Seq.empty))
                                (Contract<TestOutput>.Create(CircuitJson.createOptions (), Seq.empty))
                                (fun _ _ -> Task.FromResult(TestOutput(Text = "ok"))))
                            Seq.empty

                    options.ToolResolvers <- [| StaticToolResolver([| toolWithoutNamedPolicy |]) :> IToolResolver |])
                primary
                None
                Array.empty<IRunObserver>

        let resultWithoutNamedPolicy =
            runtimeWithoutNamedPolicy
                .RunAsync(
                    createAgent "Use tools when needed.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "policy"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(resultWithoutNamedPolicy.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, resultWithoutNamedPolicy.Result.Failure.Code)
        Assert.Contains("configured tool approval policy name", resultWithoutNamedPolicy.Result.Failure.Message)
        Assert.Equal(0, primary.ResponseCalls)

    [<Fact>]
    let ``tool cancellation and validation failures do not execute the handler`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let mutable executions = 0

        let runtime =
            createMafRuntimeWith
                (fun options ->
                    let tool =
                        createResolvedTool
                            (createTestTool "tool.validate" ApprovalMode.Never ValueNone (fun _ _ ->
                                executions <- executions + 1
                                Task.FromResult(TestOutput(Text = "ok"))))
                            Seq.empty

                    options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                primary
                None
                Array.empty<IRunObserver>

        let mappedTool =
            resolveSingleMappedTool
                runtime
                (createAgent "Use the validation tool.")
                (createRunOptions None StructuredOutputPolicy.NativeOnly)

        let validationArguments = AIFunctionArguments()

        let validationEx =
            Assert.ThrowsAny<InvalidOperationException>(fun () ->
                mappedTool.MafFunction
                    .InvokeAsync(validationArguments, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("Validation failed", validationEx.Message)
        Assert.Equal(0, executions)

        let cancelledArguments = AIFunctionArguments()
        cancelledArguments["token"] <- "ok"

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            mappedTool.MafFunction.InvokeAsync(cancelledArguments, cancelled.Token).AsTask().GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.Equal(0, executions)

    [<Fact>]
    let ``tool argument serializer and converter failures are sanitized`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let mutable executions = 0

        let createRuntimeWithJsonOptions (jsonOptions: JsonSerializerOptions) =
            createMafRuntimeWith
                (fun options ->
                    let tool =
                        createResolvedTool
                            (createTestTool "tool.sanitize" ApprovalMode.Never ValueNone (fun _ _ ->
                                executions <- executions + 1
                                Task.FromResult(TestOutput(Text = "ok"))))
                            Seq.empty

                    options.JsonSerializerOptions <- jsonOptions
                    options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                primary
                None
                Array.empty<IRunObserver>

        let serializationJsonOptions = CircuitJson.createOptions ()
        serializationJsonOptions.Converters.Add(ThrowingArgumentValueConverter())

        let serializationRuntime = createRuntimeWithJsonOptions serializationJsonOptions

        let serializationTool =
            resolveSingleMappedTool
                serializationRuntime
                (createAgent "Use the sanitizing tool.")
                (createRunOptions None StructuredOutputPolicy.NativeOnly)

        let serializationArguments = AIFunctionArguments()
        serializationArguments["token"] <- ThrowingArgumentValue()

        let serializationEx =
            Assert.ThrowsAny<InvalidOperationException>(fun () ->
                serializationTool.MafFunction
                    .InvokeAsync(serializationArguments, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Equal("Tool input could not be parsed.", serializationEx.Message)
        Assert.DoesNotContain("converter-secret", serializationEx.Message)
        Assert.Equal(0, executions)

        let deserializationJsonOptions = CircuitJson.createOptions ()
        deserializationJsonOptions.Converters.Add(ThrowingTestInputConverter())

        let deserializationRuntime = createRuntimeWithJsonOptions deserializationJsonOptions

        let deserializationTool =
            resolveSingleMappedTool
                deserializationRuntime
                (createAgent "Use the sanitizing tool.")
                (createRunOptions None StructuredOutputPolicy.NativeOnly)

        let deserializationArguments = AIFunctionArguments()
        deserializationArguments["token"] <- "ok"

        let deserializationEx =
            Assert.ThrowsAny<InvalidOperationException>(fun () ->
                deserializationTool.MafFunction
                    .InvokeAsync(deserializationArguments, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Equal("Tool input could not be parsed.", deserializationEx.Message)
        Assert.DoesNotContain("converter-secret", deserializationEx.Message)
        Assert.Equal(0, executions)

    [<Fact>]
    let ``cancellation during repair returns cancelled`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unstructured result")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let repairTcs = TaskCompletionSource<ChatResponse>()

        let secondary =
            new FakeChatClient(
                (fun _messages _options cancellationToken ->
                    cancellationToken.Register(fun () -> repairTcs.TrySetCanceled(cancellationToken) |> ignore)
                    |> ignore

                    repairTcs.Task),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime primary (Some secondary) Array.empty<IRunObserver>
        use cts = new CancellationTokenSource()

        let pending =
            runtime.RunAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "repair-cancel"),
                createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                cts.Token
            )

        cts.Cancel()
        let result = pending.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.True(secondary.SawCancellation)

    [<Fact>]
    let ``deserializing malformed session envelopes returns the intended errors`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime primary None Array.empty<IRunObserver>
        let agent = createAgent "Follow directions."

        let first =
            runtime
                .RunAsync(
                    agent,
                    createSignature<TestOutput> (),
                    TestInput(Token = "session"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        let session = first.Session |> ValueOption.get

        let serialized =
            runtime.SerializeSessionAsync(agent, session, CancellationToken.None).Result

        let fingerprint = serialized.GetProperty("definitionFingerprint").GetString()

        let badAdapter =
            JsonDocument
                .Parse(
                    $"{{\"formatVersion\":1,\"adapter\":1,\"definitionFingerprint\":\"{fingerprint}\",\"providerState\":{{}}}}"
                )
                .RootElement.Clone()

        let adapterError =
            Assert.ThrowsAny<ArgumentException>(fun () ->
                runtime
                    .DeserializeSessionAsync(agent, badAdapter, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("adapter is not supported", adapterError.Message)

        let badFingerprint =
            JsonDocument
                .Parse(
                    "{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":{},\"providerState\":{}}"
                )
                .RootElement.Clone()

        let fingerprintError =
            Assert.ThrowsAny<ArgumentException>(fun () ->
                runtime
                    .DeserializeSessionAsync(agent, badFingerprint, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("definitionFingerprint must be a string", fingerprintError.Message)

    [<Fact>]
    let ``streaming pre-cancel still emits started then one cancelled terminal event`` () =
        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>
        use cts = new CancellationTokenSource()
        cts.Cancel()

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "cancel-before-start"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                cts.Token
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let events = ResizeArray<RunEvent<TestOutput>>()

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

        Assert.Equal<RunEventKind[]>(
            [| RunEventKind.RunStarted; RunEventKind.RunFailed |],
            events |> Seq.map _.Kind |> Seq.toArray
        )

        Assert.Equal(CircuitFailureCode.Cancelled, events[1].Failure.Value.Code)
        Assert.Equal(0, streamingClient.StreamingCalls)

    [<Fact>]
    let ``disposing a partially consumed stream cancels provider work`` () =
        let providerCancelled =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let providerDisposed =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let streamingUpdates () =
            { new IAsyncEnumerable<ChatResponseUpdate> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    cancellationToken.Register(fun () -> providerCancelled.TrySetResult(true) |> ignore)
                    |> ignore

                    let mutable index = 0
                    let mutable current = Unchecked.defaultof<ChatResponseUpdate>

                    { new IAsyncEnumerator<ChatResponseUpdate> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            match index with
                            | 0 ->
                                index <- 1
                                current <- ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"partial")
                                ValueTask<bool>(true)
                            | _ ->
                                let pending =
                                    TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                                cancellationToken.Register(fun () ->
                                    pending.TrySetCanceled(cancellationToken) |> ignore)
                                |> ignore

                                ValueTask<bool>(pending.Task)

                        member _.DisposeAsync() =
                            providerDisposed.TrySetResult(true) |> ignore
                            ValueTask() } }

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> streamingUpdates ())
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "abandon-stream"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            Assert.True(enumerator.MoveNextAsync().AsTask().Result)
            Assert.Equal(RunEventKind.RunStarted, enumerator.Current.Kind)
            Assert.True(enumerator.MoveNextAsync().AsTask().Result)
            Assert.Equal(RunEventKind.OutputDelta, enumerator.Current.Kind)
        finally
            assertCompletesWithin 1000 (enumerator.DisposeAsync().AsTask()) "stream dispose should not hang"

        assertCompletesWithin 1000 providerCancelled.Task "provider cancellation was not observed"
        assertCompletesWithin 1000 providerDisposed.Task "provider enumerator was not disposed"

    [<Fact>]
    let ``backpressure cancellation releases blocked writes and completes disposal`` () =
        let providerReachedBackpressure =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let providerCancelled =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let providerDisposed =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let streamingUpdates () =
            { new IAsyncEnumerable<ChatResponseUpdate> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    cancellationToken.Register(fun () -> providerCancelled.TrySetResult(true) |> ignore)
                    |> ignore

                    let mutable index = 0
                    let mutable current = Unchecked.defaultof<ChatResponseUpdate>

                    { new IAsyncEnumerator<ChatResponseUpdate> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            if cancellationToken.IsCancellationRequested then
                                ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                            else
                                index <- index + 1

                                if index = 64 then
                                    providerReachedBackpressure.TrySetResult(true) |> ignore

                                current <- ChatResponseUpdate(ChatRole.Assistant, "x")
                                ValueTask<bool>(true)

                        member _.DisposeAsync() =
                            providerDisposed.TrySetResult(true) |> ignore
                            ValueTask() } }

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> streamingUpdates ())
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "backpressure"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            assertCompletesWithin
                1000
                providerReachedBackpressure.Task
                "provider never reached the bounded channel limit"
        finally
            assertCompletesWithin
                1000
                (enumerator.DisposeAsync().AsTask())
                "stream dispose should release blocked writes"

        assertCompletesWithin 1000 providerCancelled.Task "provider cancellation was not observed under backpressure"
        assertCompletesWithin 1000 providerDisposed.Task "provider enumerator was not disposed under backpressure"

    [<Fact>]
    let ``run cancellation reaches the streaming provider and emits one cancelled terminal event`` () =
        let providerCancelled =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let streamingUpdates () =
            { new IAsyncEnumerable<ChatResponseUpdate> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    let mutable index = 0
                    let mutable current = Unchecked.defaultof<ChatResponseUpdate>

                    cancellationToken.Register(fun () -> providerCancelled.TrySetResult(true) |> ignore)
                    |> ignore

                    { new IAsyncEnumerator<ChatResponseUpdate> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            if index = 0 then
                                index <- 1
                                current <- ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"partial")
                                ValueTask<bool>(true)
                            elif cancellationToken.IsCancellationRequested then
                                ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                            else
                                let pending =
                                    TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                                cancellationToken.Register(fun () ->
                                    pending.TrySetCanceled(cancellationToken) |> ignore)
                                |> ignore

                                ValueTask<bool>(pending.Task)

                        member _.DisposeAsync() = ValueTask() } }

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> streamingUpdates ())
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>
        use cts = new CancellationTokenSource()

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "cancel-provider"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                cts.Token
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)
        let events = ResizeArray<RunEvent<TestOutput>>()

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    let event = enumerator.Current
                    events.Add event

                    if event.Kind = RunEventKind.OutputDelta then
                        cts.Cancel()
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        assertCompletesWithin 1000 providerCancelled.Task "provider cancellation was not observed"

        let terminalEvents =
            events
            |> Seq.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(terminalEvents) |> ignore
        Assert.Equal(RunEventKind.RunFailed, terminalEvents[0].Kind)
        Assert.True(terminalEvents[0].Failure.IsSome)
        Assert.Equal(CircuitFailureCode.Cancelled, terminalEvents[0].Failure.Value.Code)

    [<Fact>]
    let ``stream cancellation ends with a terminal cancelled event`` () =
        let streamingUpdates () =
            { new IAsyncEnumerable<ChatResponseUpdate> with
                member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                    let mutable index = 0
                    let mutable current = Unchecked.defaultof<ChatResponseUpdate>

                    { new IAsyncEnumerator<ChatResponseUpdate> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            if index = 0 then
                                index <- 1
                                current <- ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"partial")
                                ValueTask<bool>(true)
                            elif cancellationToken.IsCancellationRequested then
                                ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                            else
                                let pending = TaskCompletionSource<bool>()

                                cancellationToken.Register(fun () ->
                                    pending.TrySetCanceled(cancellationToken) |> ignore)
                                |> ignore

                                ValueTask<bool>(pending.Task)

                        member _.DisposeAsync() = ValueTask() } }

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> streamingUpdates ())
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>

        let stream =
            runtime.RunStreamingAsync(
                createAgent "Follow directions.",
                createSignature<TestOutput> (),
                TestInput(Token = "cancel-stream"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )

        use cts = new CancellationTokenSource()
        let enumerator = stream.GetAsyncEnumerator(cts.Token)
        let events = ResizeArray<RunEvent<TestOutput>>()

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    let event = enumerator.Current
                    events.Add event

                    if event.Kind = RunEventKind.OutputDelta then
                        cts.Cancel()
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        let terminalEvents =
            events
            |> Seq.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Single(terminalEvents) |> ignore
        Assert.Equal(RunEventKind.RunFailed, terminalEvents[0].Kind)
        Assert.True(terminalEvents[0].Failure.IsSome)
        Assert.Equal(CircuitFailureCode.Cancelled, terminalEvents[0].Failure.Value.Code)

    [<Fact>]
    let ``session round trip binds signature tenant user and capability snapshot`` () =
        let primary =
            new FakeChatClient(
                (fun messages _options _ct ->
                    let tokenText = messages |> Seq.map _.Text |> String.concat "\n"

                    if tokenText.Contains("second", StringComparison.Ordinal) then
                        Assert.True(
                            messages
                            |> Seq.exists (fun message -> message.Text.Contains("first", StringComparison.Ordinal))
                        )

                    Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let mutable toolVersion = "1.0.0"

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| DelegateToolResolver(
                               Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>
                                   (fun _ _ ->
                                       let tool =
                                           createResolvedTool
                                               (createToolDefinition<TestInput, TestOutput>
                                                   "tool.snapshot"
                                                   toolVersion
                                                   "Snapshot tool"
                                                   ApprovalMode.Never
                                                   ValueNone
                                                   (Contract<TestInput>
                                                       .Create(CircuitJson.createOptions (), Seq.empty))
                                                   (Contract<TestOutput>
                                                       .Create(CircuitJson.createOptions (), Seq.empty))
                                                   (fun _ _ -> Task.FromResult(TestOutput(Text = "ok"))))
                                               Seq.empty

                                       ValueTask<IReadOnlyList<ResolvedTool>>(
                                           [| tool |] :> IReadOnlyList<ResolvedTool>
                                       ))
                           )
                           :> IToolResolver |])
                primary
                None
                Array.empty<IRunObserver>

        let agent = createAgent "Follow directions."
        let signature = createSignature<TestOutput> ()

        let runOptions =
            createRunOptionsWith None (Some "tenant-a") (Some "user-a") StructuredOutputPolicy.NativeOnly

        let first =
            runtime.RunAsync(agent, signature, TestInput(Token = "first"), runOptions, CancellationToken.None).Result

        let session = first.Session |> ValueOption.get

        let serialized =
            runtime.SerializeSessionAsync(agent, session, CancellationToken.None).Result

        let restored =
            runtime.DeserializeSessionAsync(agent, serialized, CancellationToken.None).Result

        let sameContextOptions =
            createRunOptionsWith (Some restored) (Some "tenant-a") (Some "user-a") StructuredOutputPolicy.NativeOnly

        let second =
            runtime
                .RunAsync(agent, signature, TestInput(Token = "second"), sameContextOptions, CancellationToken.None)
                .Result

        Assert.True(second.Result.IsSuccess)
        Assert.Equal(2, primary.ResponseCalls)

        let mismatchedAgent = createAgent "Different instructions."

        let agentMismatch =
            runtime
                .RunAsync(
                    mismatchedAgent,
                    signature,
                    TestInput(Token = "agent-mismatch"),
                    sameContextOptions,
                    CancellationToken.None
                )
                .Result

        Assert.False(agentMismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, agentMismatch.Result.Failure.Code)
        Assert.Equal(2, primary.ResponseCalls)

        let differentSignature =
            createSignatureWith<TestOutput> "signature.other" "1.0.0" (CircuitJson.createOptions ())

        let signatureMismatch =
            runtime
                .RunAsync(
                    agent,
                    differentSignature,
                    TestInput(Token = "signature-mismatch"),
                    sameContextOptions,
                    CancellationToken.None
                )
                .Result

        Assert.False(signatureMismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, signatureMismatch.Result.Failure.Code)
        Assert.Equal(2, primary.ResponseCalls)

        let tenantMismatch =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "tenant-mismatch"),
                    createRunOptionsWith
                        (Some restored)
                        (Some "tenant-b")
                        (Some "user-a")
                        StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(tenantMismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, tenantMismatch.Result.Failure.Code)
        Assert.Equal(2, primary.ResponseCalls)

        let userMismatch =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "user-mismatch"),
                    createRunOptionsWith
                        (Some restored)
                        (Some "tenant-a")
                        (Some "user-b")
                        StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(userMismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, userMismatch.Result.Failure.Code)
        Assert.Equal(2, primary.ResponseCalls)

        toolVersion <- "2.0.0"

        let capabilityMismatch =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "capability-mismatch"),
                    sameContextOptions,
                    CancellationToken.None
                )
                .Result

        Assert.False(capabilityMismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, capabilityMismatch.Result.Failure.Code)
        Assert.Equal(2, primary.ResponseCalls)

    [<Fact>]
    let ``streaming concurrency preserves run isolation and monotonic sequences`` () =
        let markerRegex = Regex("run-\\d+", RegexOptions.CultureInvariant)

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun messages _options _ct ->
                    let text = messages[messages.Count - 1].Text
                    let marker = markerRegex.Match(text).Value
                    let callArgs = Dictionary<string, obj>()
                    callArgs["marker"] <- marker

                    ArrayAsyncEnumerable(
                        [| ChatResponseUpdate(ChatRole.Assistant, $"{{\"text\":\"{marker}")
                           ChatResponseUpdate(
                               ChatRole.Assistant,
                               ResizeArray<AIContent>([ FunctionCallContent(marker, "tool", callArgs) :> AIContent ])
                               :> IList<AIContent>
                           )
                           ChatResponseUpdate(
                               ChatRole.Tool,
                               ResizeArray<AIContent>([ FunctionResultContent(marker, "done") :> AIContent ])
                               :> IList<AIContent>
                           )
                           ChatResponseUpdate(ChatRole.Assistant, "\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<IRunObserver>
        let agent = createAgent "Follow directions."
        let signature = createSignature<TestOutput> ()

        let collectRun index =
            task {
                use cts = new CancellationTokenSource()
                let events = ResizeArray<RunEvent<TestOutput>>()

                let stream =
                    runtime.RunStreamingAsync(
                        agent,
                        signature,
                        TestInput(Token = $"run-{index}"),
                        createRunOptions None StructuredOutputPolicy.NativeOnly,
                        cts.Token
                    )

                let enumerator = stream.GetAsyncEnumerator(cts.Token)

                try
                    try
                        let mutable keepGoing = true

                        while keepGoing do
                            let! moved = enumerator.MoveNextAsync().AsTask()

                            if moved then
                                let event = enumerator.Current
                                events.Add event

                                if index % 10 = 0 && event.Kind = RunEventKind.OutputDelta then
                                    cts.Cancel()
                            else
                                keepGoing <- false
                    finally
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                with :? OperationCanceledException ->
                    ()

                return index, events |> Seq.toArray
            }

        let results = Task.WhenAll([| for index in 1..100 -> collectRun index |]).Result

        let runIds =
            results |> Seq.map (fun (_, events) -> events[0].RunId.Value) |> Set.ofSeq

        Assert.Equal(100, runIds.Count)

        for index, events in results do
            Assert.NotEmpty events
            Assert.Equal(0L, events[0].Sequence)

            let sequences = events |> Array.map _.Sequence
            Assert.Equal<int64[]>([| 0L .. int64 (events.Length - 1) |], sequences)

            let marker = $"run-{index}"

            let deltas =
                events |> Array.choose (fun event -> event.TextDelta |> ValueOption.toOption)

            let combinedDelta = String.concat "" deltas
            Assert.Contains(marker, combinedDelta)

            let toolOperationIds =
                events
                |> Array.choose (fun event ->
                    match event.Kind, event.OperationId with
                    | RunEventKind.ToolStarted, ValueSome operationId
                    | RunEventKind.ToolCompleted, ValueSome operationId -> Some operationId
                    | _ -> None)

            for operationId in toolOperationIds do
                Assert.Equal(marker, operationId)

            if index % 10 <> 0 then
                Assert.Equal(RunEventKind.RunCompleted, events[events.Length - 1].Kind)
                Assert.True(events[events.Length - 1].Value.IsSome)
