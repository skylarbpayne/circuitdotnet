namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Runtime.Loader
open Circuit.Core
open Circuit.MicrosoftAgentFramework
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

type UnknownToolCallContent(callId: string) =
    inherit ToolCallContent(callId)

    member val Secret = "top-secret" with get, set

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

    let createRunOptions (session: CircuitSession option) (policy: StructuredOutputPolicy) =
        let tags =
            Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let sessionValue: CircuitSession voption =
            match session with
            | Some value -> ValueSome value
            | None -> ValueNone

        runOptionsCtor.Invoke(
            [| box sessionValue
               box (ValueNone: string voption)
               box (ValueNone: string voption)
               box tags
               box policy
               box SensitiveDataMode.Standard
               box (NullServiceProvider() :> IServiceProvider) |]
        )
        :?> RunOptions

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

    let createSignature<'Output> () =
        Signature<TestInput, 'Output>
            .Create(
                "signature.test",
                "1.0.0",
                "Test signature",
                "Return structured JSON.",
                CircuitJson.createOptions (),
                Seq.empty,
                Seq.empty
            )

    let createRuntimeWith configure primaryClient secondaryClient observers =
        let options = MafRuntimeOptions()
        options.Observers <- observers

        match secondaryClient with
        | Some client -> options.SecondaryStructuredOutputClient <- ValueSome(client :> IChatClient)
        | None -> ()

        configure options
        MafRuntime(primaryClient :> IChatClient, options) :> ICircuitRuntime

    let createRuntime primaryClient secondaryClient observers =
        createRuntimeWith ignore primaryClient secondaryClient observers

    let jsonResponse (text: string) =
        ChatResponse(ChatMessage(ChatRole.Assistant, text))

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
                               member _.ResolveToolsAsync(_context, _cancellationToken) =
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
                               member _.ResolveToolsAsync(_context, _cancellationToken) =
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
    let ``session round trip preserves conversation state and rejects a definition mismatch`` () =
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

        let runtime = createRuntime primary None Array.empty<IRunObserver>
        let agent = createAgent "Follow directions."
        let signature = createSignature<TestOutput> ()

        let first =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "first"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        let session = first.Session |> ValueOption.get

        let serialized =
            runtime.SerializeSessionAsync(agent, session, CancellationToken.None).Result

        let restored =
            runtime.DeserializeSessionAsync(agent, serialized, CancellationToken.None).Result

        let second =
            runtime
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "second"),
                    createRunOptions (Some restored) StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(second.Result.IsSuccess)

        let mismatchedAgent = createAgent "Different instructions."

        let mismatch =
            runtime
                .RunAsync(
                    mismatchedAgent,
                    signature,
                    TestInput(Token = "mismatch"),
                    createRunOptions (Some restored) StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(mismatch.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, mismatch.Result.Failure.Code)

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
