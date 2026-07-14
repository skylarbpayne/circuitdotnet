module Circuit.Core.Tests.SchedulerTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

type private SessionCheckpointRuntime(blockExecution: bool) =
    inherit CircuitRuntime()

    let started =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable serialized = 0
    let mutable deserialized = 0
    let mutable restoredProviderState: string option = None
    let mutable resumedService: obj = null

    member _.Started = started.Task
    member _.Release() = release.TrySetResult(()) |> ignore
    member _.Serialized = serialized
    member _.Deserialized = deserialized
    member _.RestoredProviderState = restoredProviderState
    member _.ResumedService = resumedService

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            runId,
            path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            input: 'Input,
            options,
            _idempotencyKey,
            _onDelta,
            _onApproval,
            onSession,
            cancellationToken
        ) : Task<RunResult<'Output>> =
        task {
            started.TrySetResult(()) |> ignore

            if blockExecution then
                do! release.Task.WaitAsync(cancellationToken)

            resumedService <- options.Services.GetService(typeof<SessionCheckpointRuntime>)

            match options.Session with
            | ValueSome session ->
                restoredProviderState <-
                    session.ProviderSession |> ValueOption.map unbox<string> |> ValueOption.toOption
            | ValueNone -> ()

            let output = unbox<'Output> (box input)

            let resultSession =
                options.Session
                |> ValueOption.defaultWith (fun () ->
                    CircuitSession(
                        "session",
                        Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                        ValueSome "test-adapter",
                        ValueSome "test-definition",
                        ValueSome(box "provider-v1")
                    ))

            do! onSession resultSession

            return
                RunResult(
                    runId,
                    CircuitResult<'Output>.Success output,
                    RunUsage(1, 2),
                    ValueSome resultSession,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
        }

    override _.SerializeSessionCoreAsync(_agent, session, _runOptions, _cancellationToken) =
        Interlocked.Increment(&serialized) |> ignore

        let provider =
            session.ProviderSession
            |> ValueOption.map unbox<string>
            |> ValueOption.defaultValue ""

        use document =
            JsonDocument.Parse($"{{\"provider\":{JsonSerializer.Serialize provider}}}")

        ValueTask<JsonElement>(document.RootElement.Clone())

    override _.DeserializeSessionCoreAsync(_agent, state, _runOptions, _cancellationToken) =
        Interlocked.Increment(&deserialized) |> ignore
        let provider = state.GetProperty("provider").GetString()

        ValueTask<CircuitSession>(
            CircuitSession(
                "restored-session",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test-adapter",
                ValueSome "test-definition",
                ValueSome(box provider)
            )
        )

type private ConcurrentSessionRuntime() =
    inherit CircuitRuntime()
    let mutable active = 0
    let mutable maximum = 0
    member _.Maximum = maximum

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            input: 'Input,
            options,
            _key,
            _delta,
            _approval,
            onSession,
            cancellationToken
        ) =
        task {
            let current = Interlocked.Increment(&active)
            maximum <- max maximum current
            do! Task.Delay(30, cancellationToken)
            Interlocked.Decrement(&active) |> ignore
            let session = options.Session.Value
            do! onSession session

            return
                RunResult(
                    runId,
                    CircuitResult<'Output>.Success(unbox<'Output> (box input)),
                    RunUsage(0, 0),
                    ValueSome session,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
        }

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) =
        use document = JsonDocument.Parse("{}")
        ValueTask<JsonElement>(document.RootElement.Clone())

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

type private AliasedSessionRuntime
    (blockExecution: bool, ?throwExecutor: bool, ?throwCodec: bool, ?restoreNullSession: bool) =
    inherit CircuitRuntime()

    let started =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable active = 0
    let mutable maximum = 0
    let mutable executions = 0
    let throwExecutor = defaultArg throwExecutor false
    let throwCodec = defaultArg throwCodec false
    let restoreNullSession = defaultArg restoreNullSession false

    member _.Started = started.Task
    member _.Release() = release.TrySetResult(()) |> ignore
    member _.Maximum = maximum
    member _.Executions = executions

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            input: 'Input,
            options,
            _key,
            _delta,
            _approval,
            _onSession,
            cancellationToken
        ) =
        task {
            Interlocked.Increment(&executions) |> ignore
            let current = Interlocked.Increment(&active)
            maximum <- max maximum current
            started.TrySetResult(()) |> ignore

            try
                if throwExecutor then
                    raise (InvalidOperationException("executor failed"))

                if blockExecution then
                    do! release.Task.WaitAsync(cancellationToken)
                else
                    do! Task.Delay(30, cancellationToken)

                return
                    RunResult(
                        runId,
                        CircuitResult<'Output>.Success(unbox<'Output> (box input)),
                        RunUsage(0, 0),
                        options.Session,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow
                    )
            finally
                Interlocked.Decrement(&active) |> ignore
        }

    override _.SerializeSessionCoreAsync(_agent, session, _runOptions, _cancellationToken) =
        if throwCodec then
            raise (InvalidOperationException("codec failed"))

        use document =
            JsonDocument.Parse($"{{\"id\":{JsonSerializer.Serialize session.Id}}}")

        ValueTask<JsonElement>(document.RootElement.Clone())

    override _.DeserializeSessionCoreAsync(_agent, state, _runOptions, _cancellationToken) =
        if restoreNullSession then
            ValueTask<CircuitSession>(Unchecked.defaultof<CircuitSession>)
        else
            ValueTask<CircuitSession>(
                CircuitSession(
                    state.GetProperty("id").GetString(),
                    Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                    ValueSome "test",
                    ValueSome "definition",
                    ValueSome(box "restored")
                )
            )

type private AgentFailureRuntime() =
    inherit CircuitRuntime()

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            _input,
            _options,
            _idempotencyKey,
            onDelta,
            _onApproval,
            _onSession,
            _cancellationToken
        ) =
        task {
            do! onDelta "partial provider output"

            return
                RunResult(
                    runId,
                    CircuitResult<'Output>
                        .Error(CircuitFailure.Create(CircuitFailureCode.Provider, "provider rejected")),
                    RunUsage(2, 3),
                    ValueNone,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
        }

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

type private AgentApprovalRuntime() =
    inherit CircuitRuntime()

    let mutable providerResponse: ApprovalResponse option = None

    member _.ProviderResponse = providerResponse

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            _input,
            _options,
            _idempotencyKey,
            _onDelta,
            onApproval,
            _onSession,
            _cancellationToken
        ) =
        task {
            let! approved = onApproval (ApprovalRequest("provider-request", "provider-tool", ValueSome "{}"))

            providerResponse <- Some approved

            return
                RunResult(
                    runId,
                    CircuitResult<'Output>.Success(unbox<'Output> (box approved.Approved)),
                    RunUsage(0, 0),
                    ValueNone,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
        }

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

type private ThrowingObservationRuntime() =
    inherit CircuitRuntime()

    let mutable observations = 0
    member _.Observations = observations

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            _runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            _input,
            _options,
            _idempotencyKey,
            _onDelta,
            _onApproval,
            _onSession,
            _cancellationToken
        ) =
        Task.FromException<RunResult<'Output>>(InvalidOperationException("No agent leaf was expected."))

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

    override _.ObserveCircuitAsync(_observation, _options, _cancellationToken) =
        Interlocked.Increment(&observations) |> ignore
        Task.FromException(InvalidOperationException("observer failed"))

type private HangingObservationRuntime() =
    inherit CircuitRuntime()

    let observationStarted =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let never =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.ObservationStarted = observationStarted.Task

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            _runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            _input,
            _options,
            _idempotencyKey,
            _onDelta,
            _onApproval,
            _onSession,
            _cancellationToken
        ) =
        Task.FromException<RunResult<'Output>>(InvalidOperationException("No agent leaf was expected."))

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

    override _.ObserveCircuitAsync(_observation, _options, _cancellationToken) =
        observationStarted.TrySetResult(()) |> ignore
        never.Task

type private CodeOnlyRuntime() =
    inherit CircuitRuntime()

    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            _runId,
            _path,
            _agent,
            _signature: Signature<'Input, 'Output>,
            _input: 'Input,
            _options,
            _idempotencyKey,
            _onDelta,
            _onApproval,
            _onSession,
            _cancellationToken
        ) : Task<RunResult<'Output>> =
        Task.FromException<RunResult<'Output>>(InvalidOperationException("No agent leaf was expected."))

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

type private ScriptedEvents<'T>(values: 'T array, failAt: int option) =
    let mutable moves = 0
    let mutable disposals = 0

    member _.Moves = moves
    member _.Disposals = disposals

    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            let mutable index = -1

            { new IAsyncEnumerator<'T> with
                member _.Current = values[index]

                member _.MoveNextAsync() =
                    moves <- moves + 1
                    index <- index + 1

                    match failAt with
                    | Some expected when index = expected ->
                        ValueTask<bool>(Task.FromException<bool>(InvalidOperationException("scripted event failure")))
                    | _ -> ValueTask<bool>(index < values.Length)

                member _.DisposeAsync() =
                    disposals <- disposals + 1
                    ValueTask() }

type private ScriptedProjectionRuntime(events: CircuitEvent<int> array, ?failAt: int, ?failStart: bool) =
    let mutable starts = 0
    let mutable disposals = 0
    let failStart = defaultArg failStart false
    let scripted = ScriptedEvents(events, failAt)

    member _.Starts = starts
    member _.Disposals = disposals
    member _.EventMoves = scripted.Moves
    member _.EventDisposals = scripted.Disposals

    interface ICircuitRuntime with
        member _.StartAsync<'Input, 'Output>
            (
                _circuit: Circuit<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _cancellationToken: CancellationToken
            ) =
            starts <- starts + 1

            if failStart then
                Task.FromException<CircuitRun<'Output>>(InvalidOperationException("scripted start failure"))
            else
                let typedEvents = scripted :> obj |> unbox<IAsyncEnumerable<CircuitEvent<'Output>>>

                Task.FromResult(
                    CircuitRun<'Output>(
                        RunId.New(),
                        typedEvents,
                        (fun _ -> ValueTask<Response<unit>>()),
                        (fun _ -> ValueTask<Response<CircuitCheckpoint<'Output>>>()),
                        (fun () ->
                            disposals <- disposals + 1
                            ValueTask())
                    )
                )

        member _.ResumeAsync<'Input, 'Output>
            (
                _circuit: Circuit<'Input, 'Output>,
                _checkpoint: CircuitCheckpoint<'Output>,
                _options: ResumeOptions,
                _cancellationToken: CancellationToken
            ) =
            Task.FromException<CircuitRun<'Output>>(InvalidOperationException("Resume was not expected."))

        member _.SerializeSessionAsync(_agent, _session, _cancellationToken) = ValueTask<JsonElement>()
        member _.DeserializeSessionAsync(_agent, _state, _cancellationToken) = ValueTask<CircuitSession>()

module private Helpers =
    let runtime = CodeOnlyRuntime() :> ICircuitRuntime
    let options = RunOptions.Default.WithMaxConcurrency(2)

    let success context value =
        Response.succeed context value |> Task.FromResult

    let toArray (values: 'T list) =
        values |> List.toArray :> IReadOnlyList<'T>

    let collectEvents (run: CircuitRun<'T>) =
        task {
            let values = ResizeArray<CircuitEvent<'T>>()
            let enumerator = run.Events.GetAsyncEnumerator()
            let mutable more = true

            while more do
                let! available = enumerator.MoveNextAsync().AsTask()
                more <- available

                if available then
                    values.Add enumerator.Current

            do! enumerator.DisposeAsync().AsTask()
            return values.ToArray()
        }

[<Fact>]
let ``finite pipeline hands completed lanes downstream in completion order`` () =
    task {
        let source =
            Circuit.items "numbers" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2; 3 ])

        let seen = System.Collections.Concurrent.ConcurrentQueue<int>()

        let delayed =
            Circuit.code "delay" "1.0.0" (fun context value ->
                task {
                    seen.Enqueue value
                    do! Task.Delay((if value = 1 then 100 else 10), context.CancellationToken)
                    return Response.succeed context (value * 10)
                })

        let pipeline = source |> Circuit.thenStep delayed
        let! result = Circuit.collect Helpers.runtime pipeline () Helpers.options CancellationToken.None

        match result.Outcome with
        | Failed failure -> failwith failure.Message
        | Succeeded values ->
            let actual = values |> Seq.map _.Value |> Seq.toList
            let inputs = seen.ToArray() |> Array.toList
            let expectedOrder = [ 20; 30; 10 ]

            let lanes =
                values
                |> Seq.map (fun response ->
                    response.Metadata.ItemKey |> ValueOption.map _.Value, response.Metadata.SourceOrdinal)
                |> Seq.toList

            Assert.True((actual = expectedOrder), sprintf "outputs=%A; inputs=%A; lanes=%A" actual inputs lanes)
    }

[<Fact>]
let ``source concurrency is bounded`` () =
    task {
        let mutable active = 0
        let mutable maximum = 0

        let source =
            Circuit.items "items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1..8 ])

        let work =
            Circuit.code "work" "1.0.0" (fun context value ->
                task {
                    let now = Interlocked.Increment(&active)
                    maximum <- max maximum now
                    do! Task.Delay(20, context.CancellationToken)
                    Interlocked.Decrement(&active) |> ignore
                    return Response.succeed context value
                })

        let! result =
            Circuit.collect Helpers.runtime (source |> Circuit.thenStep work) () Helpers.options CancellationToken.None

        Assert.True(result.IsSuccess)
        Assert.Equal(2, maximum)
    }

[<Fact>]
let ``ordinary continuation propagates lane failure without invoking next`` () =
    task {
        let mutable called = false

        let failure =
            CircuitFailure(CircuitFailureCode.Provider, "controlled", ValueNone, ValueNone, ValueNone, ValueNone)

        let first =
            Circuit.code "fail" "1.0.0" (fun context (_: unit) -> Response.fail context failure |> Task.FromResult)

        let next =
            Circuit.code "next" "1.0.0" (fun context (_: int) ->
                called <- true
                Helpers.success context 1)

        let! response =
            Circuit.run Helpers.runtime (first |> Circuit.thenStep next) () Helpers.options CancellationToken.None

        Assert.False(response.IsSuccess)
        Assert.False(called)

        let detail =
            response.Failure.Exception
            |> ValueOption.map string
            |> ValueOption.defaultValue ""

        Assert.True(response.Failure.Code = CircuitFailureCode.Provider, response.Failure.Message + " " + detail)
    }

[<Fact>]
let ``approval pauses one lane and accepts one matching response`` () =
    task {
        let approval =
            Circuit.approval "review" "1.0.0" (fun (_: string) -> ApprovalPrompt.Create("Review", "Approve"))

        let! run = Circuit.start Helpers.runtime approval "ticket" Helpers.options CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable request: ApprovalRequest option = None

        while request.IsNone do
            let! available = enumerator.MoveNextAsync().AsTask()
            Assert.True(available)

            match enumerator.Current with
            | ApprovalRequested value -> request <- Some value
            | _ -> ()

        let request = request.Value

        let! accepted =
            run.RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None).AsTask()

        Assert.True(accepted.IsSuccess)

        let! rejected =
            run.RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None).AsTask()

        Assert.False(rejected.IsSuccess)
        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``agent leaf approval maps public decisions back to provider request ids`` () =
    task {
        let runtime = AgentApprovalRuntime()

        let agent =
            AgentDefinition.Create(
                "approval-agent",
                "1.0.0",
                "Approval agent",
                "Request approval",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<unit, bool>
                .Create(
                    "approval-signature",
                    "1.0.0",
                    "Approval signature",
                    "Return the decision",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature

        let! run = Circuit.start (runtime :> ICircuitRuntime) definition () Helpers.options CancellationToken.None

        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable publicRequest: ApprovalRequest option = None
        let mutable output: Response<bool> option = None

        while output.IsNone do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested request ->
                publicRequest <- Some request
                Assert.False(StringComparer.Ordinal.Equals("provider-request", request.RequestId))
                Assert.Equal("provider-tool", request.ToolName)

                let! accepted =
                    run
                        .RespondAsync(
                            ApprovalResponse(request.RequestId, true, "approved by host"),
                            CancellationToken.None
                        )
                        .AsTask()

                Assert.True(accepted.IsSuccess)
            | OutputProduced(_, response) -> output <- Some response
            | _ -> ()

        Assert.True(publicRequest.IsSome)
        Assert.True(output.Value.IsSuccess)
        Assert.True(output.Value.Value)
        Assert.True(runtime.ProviderResponse.IsSome)
        Assert.Equal("provider-request", runtime.ProviderResponse.Value.RequestId)
        Assert.True(runtime.ProviderResponse.Value.Approved)
        Assert.Equal("approved by host", runtime.ProviderResponse.Value.Note)
        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``agent failures restamp paths emit deltas and reject cross-agent session sharing`` () =
    task {
        let failingRuntime = AgentFailureRuntime()

        let failedAgent =
            AgentDefinition.Create(
                "failed-agent",
                "1.0.0",
                "Failed agent",
                "Fail with a delta",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let failedSignature =
            Signature<unit, int>
                .Create(
                    "failed-agent-signature",
                    "1.0.0",
                    "Failed signature",
                    "Return an integer",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let failedDefinition = Circuit.agent failedAgent failedSignature

        let! failedRun =
            Circuit.start (failingRuntime :> ICircuitRuntime) failedDefinition () Helpers.options CancellationToken.None

        let! failureEvents = Helpers.collectEvents failedRun

        let delta =
            failureEvents
            |> Array.choose (function
                | OutputDelta value -> Some value
                | _ -> None)
            |> Array.exactlyOne

        Assert.Equal("partial provider output", delta.Text)

        let failedOutput =
            failureEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.False(failedOutput.IsSuccess)
        Assert.Equal(CircuitFailureCode.Provider, failedOutput.Failure.Code)
        Assert.Equal(ValueSome failedOutput.Metadata.NodePath, failedOutput.Failure.OperationId)
        Assert.Equal(2, failedOutput.Metadata.Usage.InputTokens)
        Assert.Equal(3, failedOutput.Metadata.Usage.OutputTokens)
        do! (failedRun :> IAsyncDisposable).DisposeAsync().AsTask()

        let signature =
            Signature<unit, unit>
                .Create(
                    "shared-session-signature",
                    "1.0.0",
                    "Shared session",
                    "Return unit",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let createAgent id =
            AgentDefinition.Create(id, "1.0.0", id, "Return unit", ValueNone, Seq.empty, Seq.empty, Seq.empty)

        let branches =
            [| Circuit.agent (createAgent "session-agent-one") signature
               Circuit.agent (createAgent "session-agent-two") signature |]
            :> IReadOnlyList<Circuit<unit, unit>>

        let definition = Circuit.merge "cross-agent-session" "1.0.0" 2 branches

        let session =
            CircuitSession(
                "shared-session",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test",
                ValueSome "definition",
                ValueNone
            )

        let options = Helpers.options.WithSession session
        let runtime = ConcurrentSessionRuntime()

        let! shared = Circuit.collect (runtime :> ICircuitRuntime) definition () options CancellationToken.None

        Assert.False(shared.IsSuccess)
        Assert.Equal(CircuitFailureCode.GeneratedGraphIntegrity, shared.Failure.Code)
        Assert.Contains("cannot be shared", shared.Failure.Message)
        Assert.True(shared.Failure.Exception.IsNone)
    }

[<Fact>]
let ``run emits exactly one terminal event`` () =
    task {
        let circuit = Circuit.value 42
        let! run = Circuit.start Helpers.runtime circuit () Helpers.options CancellationToken.None
        let! events = Helpers.collectEvents run

        let terminals =
            events
            |> Array.filter (function
                | RunCompleted _ -> true
                | _ -> false)

        Assert.Single(terminals) |> ignore
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``observer failures remain isolated from scheduler protocol outcomes`` () =
    task {
        let runtime = ThrowingObservationRuntime()

        let! response =
            Circuit.run (runtime :> ICircuitRuntime) (Circuit.value 42) () Helpers.options CancellationToken.None

        Assert.True(response.IsSuccess)
        Assert.Equal(42, response.Value)
        Assert.Equal(4, runtime.Observations)
    }

[<Fact>]
let ``dynamic children rebuild by fingerprint and reject changed topology`` () =
    task {
        let source =
            Circuit.items "dynamic-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let mutable version = "1.0.0"

        let build value =
            Circuit.code ($"child-{value}") version (fun context input -> Helpers.success context (input * 10))

        let definition = source |> Circuit.thenDynamic "dynamic" "1.0.0" string 2 build
        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let! _ = Helpers.collectEvents run
        let! checkpointResponse = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpointResponse.IsSuccess)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        version <- "2.0.0"

        let! resumed =
            Circuit.resume
                Helpers.runtime
                definition
                checkpointResponse.Value
                ResumeOptions.Default
                CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let terminal =
            events
            |> Array.choose (function
                | RunCompleted response -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.False(terminal.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, terminal.Failure.Code)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``active lane checkpoint replays in-flight leaf with stable keyed lane`` () =
    task {
        let started =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable calls = 0

        let source =
            Circuit.keyedItems "active-items" "1.0.0" string (fun (_: unit) -> Helpers.toArray [ 42 ])

        let code =
            Circuit.code "active-code" "1.0.0" (fun context value ->
                task {
                    Interlocked.Increment(&calls) |> ignore
                    started.TrySetResult(()) |> ignore
                    do! release.Task.WaitAsync(context.CancellationToken)
                    return Response.succeed context value
                })

        let definition = source |> Circuit.thenStep code
        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        do! started.Task
        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpoint.IsSuccess)
        release.TrySetResult(()) |> ignore
        let! _ = Helpers.collectEvents run
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed =
            Circuit.resume Helpers.runtime definition checkpoint.Value ResumeOptions.Default CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let output =
            events
            |> Array.choose (function
                | OutputProduced(key, response) -> Some(key, response.Value)
                | _ -> None)
            |> Array.exactlyOne

        Assert.Equal(ValueSome(ItemKey.Create("42")), fst output)
        Assert.Equal(42, snd output)
        Assert.Equal(2, calls)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``checkpoint resume replays root outputs without repeating committed code`` () =
    task {
        let mutable calls = 0

        let source =
            Circuit.items "checkpoint-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let code =
            Circuit.code "checkpoint-code" "1.0.0" (fun context value ->
                Interlocked.Increment(&calls) |> ignore
                Helpers.success context value)

        let definition = source |> Circuit.thenStep code
        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let! _ = Helpers.collectEvents run
        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpoint.IsSuccess)
        Assert.Equal(2, calls)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed =
            Circuit.resume Helpers.runtime definition checkpoint.Value ResumeOptions.Default CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let replayed =
            events
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)

        Assert.Equal<int[]>([| 1; 2 |], replayed |> Array.sort)
        Assert.Equal(2, calls)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``attempt routes a controlled failed lane into dynamic recovery`` () =
    task {
        let failure = CircuitFailure.Create(CircuitFailureCode.Provider, "controlled")

        let failed: Circuit<unit, int> =
            Circuit.code "controlled" "1.0.0" (fun context _ -> Response.fail context failure |> Task.FromResult)

        let routed =
            failed
            |> Circuit.attempt
            |> Circuit.thenDynamic "route" "1.0.0" (fun _ -> "one") 1 (fun response ->
                match response.Outcome with
                | Succeeded value -> Circuit.value value
                | Failed error -> Circuit.value (if error.Code = CircuitFailureCode.Provider then 99 else 0))

        let! response = Circuit.run Helpers.runtime routed () Helpers.options CancellationToken.None

        let detail =
            if response.IsSuccess then
                ""
            else
                response.Failure.Message
                + " "
                + (response.Failure.Exception
                   |> ValueOption.map string
                   |> ValueOption.defaultValue "")

        Assert.True(response.IsSuccess, detail)
        Assert.Equal(99, response.Value)
    }

[<Fact>]
let ``pending approval survives checkpoint resume with stable request id`` () =
    task {
        let definition =
            Circuit.approval "durable-review" "1.0.0" (fun (_: unit) -> ApprovalPrompt.Create("Review", "Approve"))

        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable firstRequest: ApprovalRequest option = None

        while firstRequest.IsNone do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested request -> firstRequest <- Some request
            | _ -> ()

        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpoint.IsSuccess)
        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed =
            Circuit.resume Helpers.runtime definition checkpoint.Value ResumeOptions.Default CancellationToken.None

        let resumedEnumerator = resumed.Events.GetAsyncEnumerator()
        let mutable secondRequest: ApprovalRequest option = None
        let mutable output: Response<ApprovalResponse> option = None

        while output.IsNone do
            let! more = resumedEnumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match resumedEnumerator.Current with
            | ApprovalRequested request ->
                secondRequest <- Some request

                let! accepted =
                    resumed
                        .RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(accepted.IsSuccess)
            | OutputProduced(_, response) -> output <- Some response
            | _ -> ()

        Assert.Equal(firstRequest.Value.RequestId, secondRequest.Value.RequestId)
        Assert.True(output.Value.Value.Approved)
        do! resumedEnumerator.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``merge and bounded loop use the same scheduler`` () =
    task {
        let increment =
            Circuit.code "increment" "1.0.0" (fun context value -> Helpers.success context (value + 1))

        let loop = Circuit.loop "loop" "1.0.0" 3 (fun value -> value < 3) increment
        let branches = [| loop; Circuit.value 9 |] :> IReadOnlyList<Circuit<int, int>>
        let definition = Circuit.merge "merge" "1.0.0" 2 branches
        let! collected = Circuit.collect Helpers.runtime definition 0 Helpers.options CancellationToken.None
        Assert.True(collected.IsSuccess)
        Assert.Equal<int list>([ 3; 9 ], collected.Value |> Seq.map _.Value |> Seq.sort |> Seq.toList)
    }

[<Fact>]
let ``disposing active run cancels work and emits exactly one terminal event`` () =
    task {
        let started =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let waiting =
            Circuit.code "wait" "1.0.0" (fun context (_: unit) ->
                task {
                    started.TrySetResult(()) |> ignore
                    do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                    return Response.succeed context ()
                })

        let! run = Circuit.start Helpers.runtime waiting () Helpers.options CancellationToken.None
        let events = run.Events

        let consume =
            task {
                let terminalKinds = ResizeArray<string>()
                let enumerator = events.GetAsyncEnumerator()

                try
                    let mutable reading = true

                    while reading do
                        let! more = enumerator.MoveNextAsync().AsTask()

                        if not more then
                            reading <- false
                        else
                            match enumerator.Current with
                            | RunCompleted _ -> terminalKinds.Add "completed"
                            | _ -> ()
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

                return terminalKinds
            }

        do! started.Task
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
        let! terminalKinds = consume
        Assert.Single(terminalKinds) |> ignore
    }

[<Fact>]
let ``async sources run but reject checkpoint creation`` () =
    task {
        let source =
            { new IAsyncEnumerable<int> with
                member _.GetAsyncEnumerator(_cancellationToken) =
                    let mutable current = 0

                    { new IAsyncEnumerator<int> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            current <- current + 1
                            ValueTask<bool>(current <= 2)

                        member _.DisposeAsync() = ValueTask() } }

        let circuit = Circuit.asyncSource "async" "1.0.0" (fun (_: unit) -> source)
        let! run = Circuit.start Helpers.runtime circuit () Helpers.options CancellationToken.None
        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.False(checkpoint.IsSuccess)
        Assert.Equal(CircuitFailureCode.NotCheckpointable, checkpoint.Failure.Code)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``checkpoint creation reports snapshot codec and serialized size failures`` () =
    task {
        let unsupported =
            Circuit.items "unsupported-checkpoint-item" "1.0.0" (fun (_: unit) ->
                [| typeof<int> |] :> IReadOnlyList<Type>)

        let! unsupportedRun = Circuit.start Helpers.runtime unsupported () Helpers.options CancellationToken.None

        let! unsupportedEvents = Helpers.collectEvents unsupportedRun

        let unsupportedOutput =
            unsupportedEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.exactlyOne

        Assert.Same(typeof<int>, unsupportedOutput)

        let! unsupportedCheckpoint = unsupportedRun.CreateCheckpointAsync(CancellationToken.None).AsTask()

        Assert.False(unsupportedCheckpoint.IsSuccess)
        Assert.Equal(CircuitFailureCode.NotCheckpointable, unsupportedCheckpoint.Failure.Code)
        Assert.Contains("could not be encoded", unsupportedCheckpoint.Failure.Message)
        Assert.True(unsupportedCheckpoint.Failure.Exception.IsSome)
        do! (unsupportedRun :> IAsyncDisposable).DisposeAsync().AsTask()

        let largeInput = String('x', 4096)

        let compactOutput =
            Circuit.code "large-checkpoint-input" "1.0.0" (fun context (_: string) -> Helpers.success context 1)

        let limitedOptions = Helpers.options.WithMaxCheckpointBytes(1024)

        let! largeRun = Circuit.start Helpers.runtime compactOutput largeInput limitedOptions CancellationToken.None

        let! _ = Helpers.collectEvents largeRun
        let! oversized = largeRun.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.False(oversized.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, oversized.Failure.Code)
        Assert.Contains("size limit", oversized.Failure.Message)
        do! (largeRun :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``bounded event pressure never drops root outputs`` () =
    task {
        let definition =
            Circuit.items "pressure-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1..12 ])

        let options = RunOptions.Default.WithMaxConcurrency(4).WithEventBufferCapacity(1)
        let! run = Circuit.start Helpers.runtime definition () options CancellationToken.None
        do! Task.Delay 30
        let! events = Helpers.collectEvents run

        let outputs =
            events
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)

        Assert.Equal<int list>([ 1..12 ], outputs |> Array.sort |> Array.toList)

        Assert.Single(
            events
            |> Array.choose (function
                | RunCompleted value -> Some value
                | _ -> None)
        )
        |> ignore

        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``bounded event pressure never drops approval requests`` () =
    task {
        let definition =
            Circuit.approval "pressure-review" "1.0.0" (fun (_: unit) -> ApprovalPrompt.Create("Review", "Approve"))

        let options = RunOptions.Default.WithEventBufferCapacity(1)
        let! run = Circuit.start Helpers.runtime definition () options CancellationToken.None
        do! Task.Delay 30
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable request: ApprovalRequest option = None
        let mutable output = false

        while not output do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested value ->
                request <- Some value

                let! accepted =
                    run.RespondAsync(ApprovalResponse.Create(value.RequestId, true), CancellationToken.None).AsTask()

                Assert.True(accepted.IsSuccess)
            | OutputProduced _ -> output <- true
            | _ -> ()

        Assert.True(request.IsSome)
        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``serialized checkpoint restores adapter session through runtime codec and drops process services`` () =
    task {
        let firstRuntime = SessionCheckpointRuntime(true)

        let agent =
            AgentDefinition.Create(
                "session-agent",
                "1.0.0",
                "Session agent",
                "Echo input",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "session-signature",
                    "1.0.0",
                    "Session signature",
                    "Echo input",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature

        let services =
            { new IServiceProvider with
                member _.GetService(serviceType) =
                    if serviceType = typeof<SessionCheckpointRuntime> then
                        box firstRuntime
                    else
                        null }

        let initialSession =
            CircuitSession(
                "initial-session",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test-adapter",
                ValueSome "test-definition",
                ValueSome(box "provider-v1")
            )

        let defaults = RunOptions.Default

        let options =
            RunOptions(
                ValueSome initialSession,
                defaults.TenantId,
                defaults.UserId,
                defaults.Tags,
                defaults.StructuredOutputPolicy,
                defaults.SensitiveDataMode,
                services
            )

        let! first = Circuit.start (firstRuntime :> ICircuitRuntime) definition "payload" options CancellationToken.None
        do! firstRuntime.Started
        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        Assert.Equal(1, firstRuntime.Serialized)
        let serialized = saved.Value.Serialize()
        let roundTrip = CircuitCheckpoint<string>.Deserialize(serialized)
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let secondRuntime = SessionCheckpointRuntime(false)

        let! resumed =
            Circuit.resume
                (secondRuntime :> ICircuitRuntime)
                definition
                roundTrip
                (ResumeOptions(services))
                CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let output =
            events
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.True(output.IsSuccess)
        Assert.Equal("payload", output.Value)
        Assert.Equal(1, secondRuntime.Deserialized)
        Assert.Equal(Some "provider-v1", secondRuntime.RestoredProviderState)
        Assert.Same(firstRuntime, secondRuntime.ResumedService)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``malformed checkpoint payload is rejected before code executes`` () =
    task {
        let executions = ref 0

        let definition =
            Circuit.code "malformed-checkpoint" "1.0.0" (fun context value ->
                Interlocked.Increment executions |> ignore
                Helpers.success context value)

        let! run = Circuit.start Helpers.runtime definition 7 Helpers.options CancellationToken.None
        let! _ = Helpers.collectEvents run
        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpoint.IsSuccess)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let root =
            System.Text.Json.Nodes.JsonNode.Parse(checkpoint.Value.Serialize().GetRawText()).AsObject()

        root["payload"].AsObject()["responses"] <- System.Text.Json.Nodes.JsonValue.Create("corrupt")
        use document = JsonDocument.Parse(root.ToJsonString())
        let malformed = CircuitCheckpoint<int>.Deserialize(document.RootElement)
        let! resumed = Circuit.resume Helpers.runtime definition malformed ResumeOptions.Default CancellationToken.None
        let! events = Helpers.collectEvents resumed

        let terminal =
            events
            |> Array.choose (function
                | RunCompleted value -> Some value
                | _ -> None)
            |> Array.exactlyOne

        Assert.False(terminal.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, terminal.Failure.Code)
        Assert.Equal(1, executions.Value)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``loop exhaustion returns a resource limit failure`` () =
    task {
        let increment =
            Circuit.code "bounded-increment" "1.0.0" (fun context value -> Helpers.success context (value + 1))

        let definition = Circuit.loop "bounded-loop" "1.0.0" 2 (fun _ -> true) increment
        let! response = Circuit.run Helpers.runtime definition 0 Helpers.options CancellationToken.None
        Assert.False(response.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, response.Failure.Code)
    }

[<Fact>]
let ``resumable sources pipeline page items concurrently and enforce page limits`` () =
    task {
        let source =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    ValueTask<CircuitSourcePage<int>>(
                        CircuitSourcePage<int>(Helpers.toArray [ 1; 2; 3; 4 ], ValueNone, true)
                    ) }

        let mutable active = 0
        let mutable maximum = 0

        let downstream =
            Circuit.code "durable-downstream" "1.0.0" (fun context value ->
                task {
                    let current = Interlocked.Increment(&active)
                    maximum <- max maximum current
                    do! Task.Delay(25, context.CancellationToken)
                    Interlocked.Decrement(&active) |> ignore
                    return Response.succeed context value
                })

        let definition =
            Circuit.source "durable-source" "1.0.0" source |> Circuit.thenStep downstream

        let options = RunOptions.Default.WithMaxConcurrency(3)
        let! collected = Circuit.collect Helpers.runtime definition () options CancellationToken.None
        Assert.True(collected.IsSuccess)
        Assert.Equal(4, collected.Value.Count)
        Assert.True(maximum > 1, $"Expected concurrent durable lanes, observed {maximum}.")

        let limited = options.WithLimits(16, 1024, 16, 2)
        let! failed = Circuit.collect Helpers.runtime definition () limited CancellationToken.None
        Assert.False(failed.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, failed.Failure.Code)
    }

[<Fact>]
let ``approval responses reject replay and cross-run request ids`` () =
    task {
        let approval =
            Circuit.approval "cross-run-review" "1.0.0" (fun (_: string) -> ApprovalPrompt.Create("Review", "Approve"))

        let! first = Circuit.start Helpers.runtime approval "first" Helpers.options CancellationToken.None
        let! second = Circuit.start Helpers.runtime approval "second" Helpers.options CancellationToken.None
        let firstEnumerator = first.Events.GetAsyncEnumerator()
        let secondEnumerator = second.Events.GetAsyncEnumerator()

        let readRequest (enumerator: IAsyncEnumerator<CircuitEvent<ApprovalResponse>>) =
            task {
                let mutable found = None

                while found.IsNone do
                    let! more = enumerator.MoveNextAsync().AsTask()
                    Assert.True(more)

                    match enumerator.Current with
                    | ApprovalRequested request -> found <- Some request
                    | _ -> ()

                return found.Value
            }

        let! firstRequest = readRequest firstEnumerator
        let! secondRequest = readRequest secondEnumerator
        Assert.False(StringComparer.Ordinal.Equals(firstRequest.RequestId, secondRequest.RequestId))

        let! crossRun =
            second.RespondAsync(ApprovalResponse.Create(firstRequest.RequestId, true), CancellationToken.None).AsTask()

        Assert.False(crossRun.IsSuccess)

        let! accepted =
            first.RespondAsync(ApprovalResponse.Create(firstRequest.RequestId, true), CancellationToken.None).AsTask()

        Assert.True(accepted.IsSuccess)

        let! replay =
            first.RespondAsync(ApprovalResponse.Create(firstRequest.RequestId, true), CancellationToken.None).AsTask()

        Assert.False(replay.IsSuccess)

        let! secondAccepted =
            second.RespondAsync(ApprovalResponse.Create(secondRequest.RequestId, true), CancellationToken.None).AsTask()

        Assert.True(secondAccepted.IsSuccess)
        do! firstEnumerator.DisposeAsync().AsTask()
        do! secondEnumerator.DisposeAsync().AsTask()
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()
        do! (second :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``checkpoint atomically restores every pending approval lane`` () =
    task {
        let source =
            Circuit.items "pending-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let approval =
            Circuit.approval "pending-review" "1.0.0" (fun value -> ApprovalPrompt.Create($"Review {value}", "Approve"))

        let definition = source |> Circuit.thenStep approval
        let options = RunOptions.Default.WithMaxConcurrency(2)
        let! run = Circuit.start Helpers.runtime definition () options CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let requests = ResizeArray<ApprovalRequest>()

        while requests.Count < 2 do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested request -> requests.Add request
            | _ -> ()

        let! saved = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        let expectedIds = requests |> Seq.map _.RequestId |> Set.ofSeq

        let roundTrip =
            CircuitCheckpoint<ApprovalResponse>.Deserialize(saved.Value.Serialize())

        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed = Circuit.resume Helpers.runtime definition roundTrip ResumeOptions.Default CancellationToken.None
        let resumedEnumerator = resumed.Events.GetAsyncEnumerator()
        let restored = ResizeArray<ApprovalRequest>()
        let mutable terminal = false

        while not terminal do
            let! more = resumedEnumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match resumedEnumerator.Current with
            | ApprovalRequested request ->
                restored.Add request

                let! accepted =
                    resumed
                        .RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(accepted.IsSuccess)
            | RunCompleted _ -> terminal <- true
            | _ -> ()

        Assert.Equal<Set<string>>(expectedIds, restored |> Seq.map _.RequestId |> Set.ofSeq)
        do! resumedEnumerator.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``sixteen approval round bound counts concurrent pending lanes`` () =
    task {
        let source =
            Circuit.items "limited-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1..17 ])

        let approval =
            Circuit.approval "limited-review" "1.0.0" (fun value -> ApprovalPrompt.Create($"Review {value}", "Approve"))

        let definition = source |> Circuit.thenStep approval
        let options = RunOptions.Default.WithMaxConcurrency(17)
        let! run = Circuit.start Helpers.runtime definition () options CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let events = ResizeArray<CircuitEvent<ApprovalResponse>>()
        let mutable terminal = false

        while not terminal do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)
            events.Add enumerator.Current

            match enumerator.Current with
            | ApprovalRequested request ->
                let! accepted =
                    run.RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None).AsTask()

                Assert.True(accepted.IsSuccess)
            | RunCompleted _ -> terminal <- true
            | _ -> ()

        let limited =
            events
            |> Seq.choose (function
                | OutputProduced(_, response) when not response.IsSuccess -> Some response
                | _ -> None)
            |> Seq.exactlyOne

        Assert.Equal(CircuitFailureCode.ResourceLimit, limited.Failure.Code)

        Assert.Equal(
            16,
            events
            |> Seq.filter (function
                | ApprovalRequested _ -> true
                | _ -> false)
            |> Seq.length
        )

        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``disposing an unread full event buffer releases blocked structural writers`` () =
    task {
        let source =
            Circuit.items "buffer-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1..20 ])

        let options =
            RunOptions.Default
                .WithMaxConcurrency(4)
                .WithEventBufferCapacity(1)
                .WithDisposalDrainTimeout(TimeSpan.FromSeconds(1.0))

        let! run = Circuit.start Helpers.runtime source () options CancellationToken.None
        do! Task.Delay(25)
        let disposal = (run :> IAsyncDisposable).DisposeAsync().AsTask()
        do! disposal.WaitAsync(TimeSpan.FromSeconds(2.0))
        Assert.True(disposal.IsCompletedSuccessfully)
    }

[<Fact>]
let ``continued provider session is serialized across source lanes`` () =
    task {
        let runtime = ConcurrentSessionRuntime()

        let session =
            CircuitSession(
                "shared",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test",
                ValueSome "definition",
                ValueSome(box "state")
            )

        let agent =
            AgentDefinition.Create(
                "shared-agent",
                "1.0.0",
                "Shared",
                "Echo",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<int, int>
                .Create("shared", "1.0.0", "Shared", "Echo", CircuitJson.createOptions (), Seq.empty, Seq.empty)

        let source =
            Circuit.items "shared-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2; 3 ])

        let definition = source |> Circuit.thenStep (Circuit.agent agent signature)
        let options = RunOptions.Default.WithSession(session).WithMaxConcurrency(3)
        let! response = Circuit.collect (runtime :> ICircuitRuntime) definition () options CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal(1, runtime.Maximum)
    }

[<Fact>]
let ``nested finite and dynamic sources complete at concurrency one`` () =
    task {
        let outer =
            Circuit.items "outer" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let inner =
            Circuit.items "inner" "1.0.0" (fun value -> Helpers.toArray [ value; value + 10 ])

        let nested = outer |> Circuit.thenStep inner
        let options = RunOptions.Default.WithMaxConcurrency(1)

        let finite =
            Circuit.collect Helpers.runtime nested () options CancellationToken.None

        let! finiteResult = finite.WaitAsync(TimeSpan.FromSeconds(2.0))
        Assert.True(finiteResult.IsSuccess)
        Assert.Equal<int list>([ 1; 2; 11; 12 ], finiteResult.Value |> Seq.map _.Value |> Seq.sort |> Seq.toList)

        let dynamic =
            outer
            |> Circuit.thenDynamic "dynamic-source" "1.0.0" string 1 (fun value ->
                Circuit.items "dynamic-items" "1.0.0" (fun (_: int) -> Helpers.toArray [ value; value * 10 ]))

        let! dynamicResult =
            Circuit.collect Helpers.runtime dynamic () options CancellationToken.None
            |> _.WaitAsync(TimeSpan.FromSeconds(2.0))

        Assert.True(dynamicResult.IsSuccess)
        Assert.Equal(4, dynamicResult.Value.Count)
    }

[<Fact>]
let ``accepted approval is durable immediately when RespondAsync returns`` () =
    task {
        let definition =
            Circuit.approval "atomic-approval" "1.0.0" (fun (_: unit) -> ApprovalPrompt.Create("Review", "Approve"))

        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable request = None

        while request.IsNone do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested value -> request <- Some value
            | _ -> ()

        let! accepted =
            run.RespondAsync(ApprovalResponse.Create(request.Value.RequestId, true), CancellationToken.None).AsTask()

        Assert.True(accepted.IsSuccess)
        let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(checkpoint.IsSuccess)

        let roundTrip =
            CircuitCheckpoint<ApprovalResponse>.Deserialize(checkpoint.Value.Serialize())

        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed = Circuit.resume Helpers.runtime definition roundTrip ResumeOptions.Default CancellationToken.None
        let! events = Helpers.collectEvents resumed

        Assert.DoesNotContain(
            events,
            fun event ->
                match event with
                | ApprovalRequested _ -> true
                | _ -> false
        )

        Assert.Contains(
            events,
            fun event ->
                match event with
                | OutputProduced(_, response) -> response.Value.Approved
                | _ -> false
        )

        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``lineage dynamic approval and source page counters survive resume`` () =
    task {
        let source =
            Circuit.items "counter-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let child value =
            Circuit.approval ($"child-approval-{value}") "1.0.0" (fun (_: int) ->
                ApprovalPrompt.Create("Review", string value))

        let dynamic = source |> Circuit.thenDynamic "counter-dynamic" "1.0.0" string 1 child
        let limits = RunOptions.Default.WithMaxConcurrency(1).WithLimits(16, 1, 1, 256, 8)
        let! first = Circuit.start Helpers.runtime dynamic () limits CancellationToken.None
        let firstEvents = first.Events.GetAsyncEnumerator()
        let mutable firstRequest = None

        while firstRequest.IsNone do
            let! more = firstEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match firstEvents.Current with
            | ApprovalRequested value -> firstRequest <- Some value
            | _ -> ()

        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)

        let checkpoint =
            CircuitCheckpoint<ApprovalResponse>.Deserialize(saved.Value.Serialize())

        do! firstEvents.DisposeAsync().AsTask()
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed = Circuit.resume Helpers.runtime dynamic checkpoint ResumeOptions.Default CancellationToken.None
        let resumedEvents = resumed.Events.GetAsyncEnumerator()
        let mutable terminal: Response<RunSummary> option = None

        while terminal.IsNone do
            let! more = resumedEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match resumedEvents.Current with
            | ApprovalRequested value ->
                let! response =
                    resumed
                        .RespondAsync(ApprovalResponse.Create(value.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(response.IsSuccess)
            | RunCompleted value -> terminal <- Some value
            | _ -> ()

        Assert.False(terminal.Value.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, terminal.Value.Failure.Code)
        do! resumedEvents.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()

        let mutable reads = 0

        let resumable =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, cursor, _) =
                    reads <- reads + 1

                    let next =
                        cursor
                        |> ValueOption.map (fun value -> value + "x")
                        |> ValueOption.defaultValue "x"

                    ValueTask<CircuitSourcePage<int>>(
                        CircuitSourcePage<int>(Helpers.toArray [ reads ], ValueSome next, false)
                    ) }

        let paged =
            Circuit.source "paged-counter" "1.0.0" resumable
            |> Circuit.thenStep (
                Circuit.approval "page-review" "1.0.0" (fun value -> ApprovalPrompt.Create("Review", string value))
            )

        let pageLimits =
            RunOptions.Default.WithMaxConcurrency(1).WithLimits(16, 16, 4, 8, 1)

        let! pageRun = Circuit.start Helpers.runtime paged () pageLimits CancellationToken.None
        let pageEvents = pageRun.Events.GetAsyncEnumerator()
        let mutable pageRequest = None

        while pageRequest.IsNone do
            let! more = pageEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match pageEvents.Current with
            | ApprovalRequested value -> pageRequest <- Some value
            | _ -> ()

        let! pageSaved = pageRun.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(pageSaved.IsSuccess)

        let pageCheckpoint =
            CircuitCheckpoint<ApprovalResponse>.Deserialize(pageSaved.Value.Serialize())

        do! pageEvents.DisposeAsync().AsTask()
        do! (pageRun :> IAsyncDisposable).DisposeAsync().AsTask()

        let! pageResumed =
            Circuit.resume Helpers.runtime paged pageCheckpoint ResumeOptions.Default CancellationToken.None

        let pageResumedEvents = pageResumed.Events.GetAsyncEnumerator()
        let mutable pageTerminal: Response<RunSummary> option = None

        while pageTerminal.IsNone do
            let! more = pageResumedEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match pageResumedEvents.Current with
            | ApprovalRequested value ->
                let! response =
                    pageResumed
                        .RespondAsync(ApprovalResponse.Create(value.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(response.IsSuccess)
            | RunCompleted value -> pageTerminal <- Some value
            | _ -> ()

        Assert.False(pageTerminal.Value.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, pageTerminal.Value.Failure.Code)
        Assert.Equal(1, reads)
        do! pageResumedEvents.DisposeAsync().AsTask()
        do! (pageResumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``resumable source rejects a nonprogressing cursor`` () =
    task {
        let source =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    ValueTask<CircuitSourcePage<int>>(CircuitSourcePage<int>(Helpers.toArray [], ValueNone, false)) }

        let definition = Circuit.source "stuck-source" "1.0.0" source
        let! response = Circuit.collect Helpers.runtime definition () Helpers.options CancellationToken.None
        Assert.False(response.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, response.Failure.Code)
    }

[<Fact>]
let ``dynamic constant factory keys remain distinct across source items`` () =
    task {
        let source =
            Circuit.items "constant-key-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 3; 4 ])

        let definition =
            source
            |> Circuit.thenDynamic "constant-key-dynamic" "1.0.0" (fun _ -> "same") 2 (fun value ->
                Circuit.value (value * 10))

        let! response = Circuit.collect Helpers.runtime definition () Helpers.options CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal<int list>([ 30; 40 ], response.Value |> Seq.map _.Value |> Seq.sort |> Seq.toList)
    }

[<Fact>]
let ``replayed response usage is not charged to resumed attempt`` () =
    task {
        let runtime = SessionCheckpointRuntime(false)

        let agent =
            AgentDefinition.Create("usage-agent", "1.0.0", "Usage", "Echo", ValueNone, Seq.empty, Seq.empty, Seq.empty)

        let signature =
            Signature<string, string>
                .Create("usage", "1.0.0", "Usage", "Echo", CircuitJson.createOptions (), Seq.empty, Seq.empty)

        let definition = Circuit.agent agent signature

        let! first =
            Circuit.start (runtime :> ICircuitRuntime) definition "value" RunOptions.Default CancellationToken.None

        let! _ = Helpers.collectEvents first
        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()
        let roundTrip = CircuitCheckpoint<string>.Deserialize(saved.Value.Serialize())

        let! resumed =
            Circuit.resume
                (runtime :> ICircuitRuntime)
                definition
                roundTrip
                ResumeOptions.Default
                CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let terminal =
            events
            |> Array.choose (function
                | RunCompleted value -> Some value
                | _ -> None)
            |> Array.exactlyOne

        Assert.True(terminal.IsSuccess)
        Assert.Equal(0, terminal.Value.Usage.InputTokens)
        Assert.Equal(0, terminal.Value.Usage.OutputTokens)

        let replay =
            events
            |> Array.choose (function
                | OutputProduced(_, value) -> Some value
                | _ -> None)
            |> Array.exactlyOne

        Assert.Equal(1, replay.Metadata.Usage.InputTokens)
        Assert.Equal(2, replay.Metadata.Usage.OutputTokens)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``Circuit value freezes mutable input at definition creation`` () =
    task {
        let value = ResizeArray<int>([ 1; 2 ])
        let definition: Circuit<unit, ResizeArray<int>> = Circuit.value value
        value.Add 3
        let! response = Circuit.run Helpers.runtime definition () Helpers.options CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal<int list>([ 1; 2 ], response.Value |> Seq.toList)
    }

[<Fact>]
let ``disposing unread full buffer leaves exactly one cancelled terminal`` () =
    task {
        let waiting =
            Circuit.code "unread-wait" "1.0.0" (fun context (_: unit) ->
                task {
                    do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                    return Response.succeed context ()
                })

        let options =
            RunOptions.Default.WithEventBufferCapacity(1).WithDisposalDrainTimeout(TimeSpan.FromSeconds(1.0))

        let! run = Circuit.start Helpers.runtime waiting () options CancellationToken.None
        let stream = run.Events
        do! Task.Delay 30
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
        let enumerator = stream.GetAsyncEnumerator()
        let terminals = ResizeArray<Response<RunSummary>>()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                match enumerator.Current with
                | RunCompleted response -> terminals.Add response
                | _ -> ()

        Assert.Single(terminals) |> ignore
        Assert.False(terminals[0].IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, terminals[0].Failure.Code)
        do! enumerator.DisposeAsync().AsTask()
    }

[<Fact>]
let ``hanging observer cannot delay disposal or suppress the typed terminal`` () =
    task {
        let runtime = HangingObservationRuntime()

        let options =
            RunOptions.Default.WithEventBufferCapacity(1).WithDisposalDrainTimeout(TimeSpan.FromMilliseconds(10.0))

        let! run = Circuit.start (runtime :> ICircuitRuntime) (Circuit.value 42) () options CancellationToken.None

        let stream = run.Events
        do! runtime.ObservationStarted.WaitAsync(TimeSpan.FromSeconds(1.0))
        let disposal = (run :> IAsyncDisposable).DisposeAsync().AsTask()
        do! disposal.WaitAsync(TimeSpan.FromMilliseconds(250.0))
        Assert.True(disposal.IsCompletedSuccessfully)

        let enumerator = stream.GetAsyncEnumerator()
        let terminals = ResizeArray<Response<RunSummary>>()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                match enumerator.Current with
                | RunCompleted response -> terminals.Add response
                | _ -> ()

        let terminal = Assert.Single(terminals)
        Assert.False(terminal.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, terminal.Failure.Code)
        do! enumerator.DisposeAsync().AsTask()
    }

[<Fact>]
let ``concurrent root outputs and disposal always close with one typed terminal`` () =
    task {
        let lanes = 32

        for iteration in 1..25 do
            let mutable started = 0

            let allStarted =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let source =
                Circuit.items $"dispose-race-items-{iteration}" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1..lanes ])

            let gated =
                Circuit.code $"dispose-race-gate-{iteration}" "1.0.0" (fun context value ->
                    task {
                        if Interlocked.Increment(&started) = lanes then
                            allStarted.TrySetResult(()) |> ignore

                        do! release.Task
                        return Response.succeed context value
                    })

            let options =
                RunOptions.Default
                    .WithMaxConcurrency(lanes)
                    .WithEventBufferCapacity(256)
                    .WithDisposalDrainTimeout(TimeSpan.FromMilliseconds(1.0))

            let! run =
                Circuit.start Helpers.runtime (source |> Circuit.thenStep gated) () options CancellationToken.None

            let events = Helpers.collectEvents run
            do! allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0))

            let disposal = Task.Run(fun () -> (run :> IAsyncDisposable).DisposeAsync().AsTask())

            release.TrySetResult(()) |> ignore
            do! disposal.WaitAsync(TimeSpan.FromSeconds(1.0))
            let! observed = events.WaitAsync(TimeSpan.FromSeconds(1.0))

            let terminals =
                observed
                |> Array.choose (function
                    | RunCompleted response -> Some response
                    | _ -> None)

            Assert.Single(terminals) |> ignore
    }

[<Fact>]
let ``nested source lanes compose parent and child identity across checkpoint replay`` () =
    task {
        let outer =
            Circuit.items "identity-outer" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let inner =
            Circuit.items "identity-inner" "1.0.0" (fun value -> Helpers.toArray [ value ])

        let multiply =
            Circuit.code "identity-times-ten" "1.0.0" (fun context value ->
                Response.succeed context (value * 10) |> Task.FromResult)

        let definition = outer |> Circuit.thenStep inner |> Circuit.thenStep multiply
        let! first = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let! firstEvents = Helpers.collectEvents first

        let firstValues =
            firstEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.sort

        Assert.Equal<int[]>([| 10; 20 |], firstValues)
        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        let checkpoint = CircuitCheckpoint<int>.Deserialize(saved.Value.Serialize())
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed = Circuit.resume Helpers.runtime definition checkpoint ResumeOptions.Default CancellationToken.None

        let! resumedEvents = Helpers.collectEvents resumed

        let resumedValues =
            resumedEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.sort

        Assert.Equal<int[]>([| 10; 20 |], resumedValues)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``continued session aliases survive active two lane process boundary resume`` () =
    task {
        let agent =
            AgentDefinition.Create("alias-agent", "1.0.0", "Alias", "Echo", ValueNone, Seq.empty, Seq.empty, Seq.empty)

        let signature =
            Signature<int, int>
                .Create("alias", "1.0.0", "Alias", "Echo", CircuitJson.createOptions (), Seq.empty, Seq.empty)

        let source =
            Circuit.items "alias-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let definition = source |> Circuit.thenStep (Circuit.agent agent signature)

        let session =
            CircuitSession(
                "shared-alias",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test",
                ValueSome "definition",
                ValueSome(box "active")
            )

        let options = RunOptions.Default.WithSession(session).WithMaxConcurrency(2)
        let firstRuntime = AliasedSessionRuntime(true)
        let! first = Circuit.start (firstRuntime :> ICircuitRuntime) definition () options CancellationToken.None
        do! firstRuntime.Started.WaitAsync(TimeSpan.FromSeconds(2.0))

        let mutable checkpoint: CircuitCheckpoint<int> option = None
        let mutable attempts = 0

        while checkpoint.IsNone && attempts < 100 do
            let! candidate = first.CreateCheckpointAsync(CancellationToken.None).AsTask()

            if candidate.IsSuccess then
                let payload = candidate.Value.Serialize().GetProperty("payload")
                let aliases = payload.GetProperty("sessionAliases").EnumerateObject() |> Seq.length
                let sessions = payload.GetProperty("sessions").EnumerateObject() |> Seq.length

                if aliases = 2 && sessions = 1 then
                    checkpoint <- Some(CircuitCheckpoint<int>.Deserialize(candidate.Value.Serialize()))

            attempts <- attempts + 1
            do! Task.Delay 5

        Assert.True(checkpoint.IsSome, "Expected two durable lane aliases bound to one serialized session.")
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let resumedRuntime = AliasedSessionRuntime(false)

        let! resumed =
            Circuit.resume
                (resumedRuntime :> ICircuitRuntime)
                definition
                checkpoint.Value
                ResumeOptions.Default
                CancellationToken.None

        let! events = Helpers.collectEvents resumed

        let terminal =
            events
            |> Array.choose (function
                | RunCompleted response -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.True(terminal.IsSuccess, if terminal.IsSuccess then "" else terminal.Failure.Message)
        Assert.Equal(1, resumedRuntime.Maximum)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``continued session permit releases when executor or codec throws`` throwCodec =
    task {
        let agent =
            AgentDefinition.Create("throw-agent", "1.0.0", "Throw", "Fail", ValueNone, Seq.empty, Seq.empty, Seq.empty)

        let signature =
            Signature<int, int>
                .Create("throw", "1.0.0", "Throw", "Fail", CircuitJson.createOptions (), Seq.empty, Seq.empty)

        let source =
            Circuit.items "throw-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let session =
            CircuitSession(
                "throw-shared",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test",
                ValueSome "definition",
                ValueSome(box "active")
            )

        let runtime =
            AliasedSessionRuntime(false, throwExecutor = not throwCodec, throwCodec = throwCodec)

        let definition = source |> Circuit.thenStep (Circuit.agent agent signature)
        let options = RunOptions.Default.WithSession(session).WithMaxConcurrency(2)

        let! result =
            Circuit.collect (runtime :> ICircuitRuntime) definition () options CancellationToken.None
            |> _.WaitAsync(TimeSpan.FromSeconds(2.0))

        Assert.False(result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, result.Failure.Code)
    }

[<Fact>]
let ``Circuit value clones every uncached evaluation after response mutation`` () =
    task {
        let definition: Circuit<unit, ResizeArray<int>> =
            Circuit.value (ResizeArray<int>([ 1; 2 ]))

        let! first = Circuit.run Helpers.runtime definition () Helpers.options CancellationToken.None
        Assert.True(first.IsSuccess)
        first.Value.Add 9

        let! second = Circuit.run Helpers.runtime definition () Helpers.options CancellationToken.None
        Assert.True(second.IsSuccess)
        Assert.Equal<int list>([ 1; 2 ], second.Value |> Seq.toList)

        let! concurrent =
            Task.WhenAll(
                [| Circuit.run Helpers.runtime definition () Helpers.options CancellationToken.None
                   Circuit.run Helpers.runtime definition () Helpers.options CancellationToken.None |]
            )

        Assert.False(Object.ReferenceEquals(concurrent[0].Value, concurrent[1].Value))
        concurrent[0].Value.Add 7
        Assert.Equal<int list>([ 1; 2 ], concurrent[1].Value |> Seq.toList)
    }

[<Fact>]
let ``disposing cancellation ignoring unread work publishes one typed cancelled terminal`` () =
    task {
        let never =
            TaskCompletionSource<Response<unit>>(TaskCreationOptions.RunContinuationsAsynchronously)

        let definition =
            Circuit.code "ignore-cancellation" "1.0.0" (fun _ (_: unit) -> never.Task)

        let options =
            RunOptions.Default.WithEventBufferCapacity(1).WithDisposalDrainTimeout(TimeSpan.FromMilliseconds(10.0))

        let! run = Circuit.start Helpers.runtime definition () options CancellationToken.None
        let stream = run.Events
        do! Task.Delay 30
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
        let enumerator = stream.GetAsyncEnumerator()
        let terminals = ResizeArray<Response<RunSummary>>()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                match enumerator.Current with
                | RunCompleted response -> terminals.Add response
                | _ -> ()

        Assert.Single(terminals) |> ignore
        Assert.False(terminals[0].IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, terminals[0].Failure.Code)
        do! enumerator.DisposeAsync().AsTask()
        never.TrySetCanceled() |> ignore
    }

[<Fact>]
let ``graph approval metadata survives protocol checkpoint and resume`` () =
    task {
        let metadata = Dictionary<string, string>(StringComparer.Ordinal)
        metadata["route"] <- "security"
        metadata["audit"] <- "required"

        let definition =
            Circuit.approval "metadata-approval" "1.0.0" (fun (_: unit) ->
                ApprovalPrompt("Review", "Inspect", metadata))

        let assertPrompt (request: ApprovalRequest) =
            Assert.True(request.Prompt.IsSome)
            Assert.Equal("Review", request.Prompt.Value.Title)
            Assert.Equal("Inspect", request.Prompt.Value.Message)
            Assert.Equal(2, request.Prompt.Value.Metadata.Count)
            Assert.Equal("security", request.Prompt.Value.Metadata["route"])
            Assert.Equal("required", request.Prompt.Value.Metadata["audit"])

        let! first = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let firstEvents = first.Events.GetAsyncEnumerator()
        let mutable firstRequest: ApprovalRequest option = None

        while firstRequest.IsNone do
            let! more = firstEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match firstEvents.Current with
            | ApprovalRequested request -> firstRequest <- Some request
            | _ -> ()

        assertPrompt firstRequest.Value
        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        let serialized = saved.Value.Serialize()

        let durablePrompt =
            serialized.GetProperty("payload").GetProperty("pendingApprovalRequests").EnumerateObject()
            |> Seq.exactlyOne
            |> _.Value.GetProperty("prompt")

        Assert.Equal("security", durablePrompt.GetProperty("metadata").GetProperty("route").GetString())
        let checkpoint = CircuitCheckpoint<ApprovalResponse>.Deserialize(serialized)
        do! firstEvents.DisposeAsync().AsTask()
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed = Circuit.resume Helpers.runtime definition checkpoint ResumeOptions.Default CancellationToken.None
        let resumedEvents = resumed.Events.GetAsyncEnumerator()
        let mutable resumedRequest: ApprovalRequest option = None
        let mutable terminal = false

        while not terminal do
            let! more = resumedEvents.MoveNextAsync().AsTask()
            Assert.True(more)

            match resumedEvents.Current with
            | ApprovalRequested request ->
                resumedRequest <- Some request
                assertPrompt request

                let! accepted =
                    resumed
                        .RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(accepted.IsSuccess)
            | RunCompleted _ -> terminal <- true
            | _ -> ()

        Assert.True(resumedRequest.IsSome)
        do! resumedEvents.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``durable combinators reject malformed ids and versions`` () =
    let previous: Circuit<unit, int> = Circuit.value 1
    let next: Circuit<int, int> = Circuit.value 2

    Assert.Throws<ArgumentException>(fun () ->
        Circuit.thenDynamic "a/b|x" "1.0.0" string 1 (fun _ -> next) previous |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () ->
        Circuit.thenDynamic "dynamic" "changed" string 1 (fun _ -> next) previous
        |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () -> Circuit.recover "bad/id" "1.0.0" (fun _ -> 0) previous |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () -> Circuit.recover "recover" "v1" (fun _ -> 0) previous |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () ->
        Circuit.aggregate
            "aggregate|bad"
            "1.0.0"
            (fun context _ _ -> Response.succeed context 0 |> Task.FromResult)
            previous
        |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () ->
        Circuit.aggregate "aggregate" "1" (fun context _ _ -> Response.succeed context 0 |> Task.FromResult) previous
        |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () -> Circuit.named "bad/name" previous |> ignore)
    |> ignore

    let valid =
        previous
        |> Circuit.thenDynamic "dynamic" "1.2.3" string 1 (fun _ -> next)
        |> Circuit.recover "recover" "2.0.0" (fun _ -> 0)
        |> Circuit.named "safe-local"

    Assert.Empty(Circuit.validate valid)

[<Fact>]
let ``collectSourceOrder uses hierarchical outer then inner ordinals`` () =
    task {
        let outer =
            Circuit.items "order-outer" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

        let inner =
            Circuit.items "order-inner" "1.0.0" (fun value -> Helpers.toArray [ value * 10; value * 10 + 1 ])

        let delayed =
            Circuit.code "order-delay" "1.0.0" (fun context value ->
                task {
                    do! Task.Delay((if value < 20 then 80 else 5), context.CancellationToken)
                    return Response.succeed context value
                })

        let definition = outer |> Circuit.thenStep inner |> Circuit.thenStep delayed

        let! ordered =
            Circuit.collectSourceOrder
                Helpers.runtime
                definition
                ()
                (RunOptions.Default.WithMaxConcurrency(4))
                CancellationToken.None

        Assert.True(ordered.IsSuccess)
        Assert.Equal<int list>([ 10; 11; 20; 21 ], ordered.Value |> Seq.map _.Value |> Seq.toList)

        Assert.Equal<int64 list list>(
            [ [ 0L; 0L ]; [ 0L; 1L ]; [ 1L; 0L ]; [ 1L; 1L ] ],
            ordered.Value
            |> Seq.map (fun response -> response.Metadata.SourceOrder |> Seq.toList)
            |> Seq.toList
        )

        let! first =
            Circuit.start
                Helpers.runtime
                definition
                ()
                (RunOptions.Default.WithMaxConcurrency(4))
                CancellationToken.None

        let! _ = Helpers.collectEvents first
        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        let checkpoint = CircuitCheckpoint<int>.Deserialize(saved.Value.Serialize())
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()
        let! resumed = Circuit.resume Helpers.runtime definition checkpoint ResumeOptions.Default CancellationToken.None
        let! replay = Helpers.collectEvents resumed

        let replayOrders =
            replay
            |> Array.choose (function
                | OutputProduced(_, response) -> Some(response.Value, response.Metadata.SourceOrder |> Seq.toList)
                | _ -> None)
            |> Array.sortBy fst

        Assert.Equal<int64 list list>(
            [ [ 0L; 0L ]; [ 0L; 1L ]; [ 1L; 0L ]; [ 1L; 1L ] ],
            replayOrders |> Array.map snd |> Array.toList
        )

        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``Circuit graph descriptor exposes immutable topology cardinality and bounds`` () =
    let source =
        Circuit.items "graph-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2 ])

    let child = Circuit.value 1

    let definition =
        source
        |> Circuit.thenDynamic "graph-dynamic" "2.0.0" string 3 (fun _ -> child)
        |> Circuit.named "graph-root"

    let graph = definition.Graph
    Assert.True(graph.IsValid)
    Assert.Equal(definition.Fingerprint, graph.Fingerprint)
    Assert.Equal(CircuitCardinality.Many, graph.Cardinality)
    Assert.Equal(definition.Checkpointability, graph.Checkpointability)
    Assert.Contains(graph.Nodes, fun node -> node.Kind = CircuitNodeKind.Dynamic && node.ConcurrencyLimit = ValueSome 3)
    Assert.Contains(graph.Nodes, fun node -> node.Kind = CircuitNodeKind.Items && node.Id = "graph-items")
    Assert.All(graph.Nodes, fun node -> Assert.False(String.IsNullOrWhiteSpace node.Path))
    let nodes = graph.Nodes :?> System.Collections.IList
    Assert.True(nodes.IsReadOnly)

[<Fact>]
let ``recovery preserves successful lanes and replaces or reports failed lanes`` () =
    task {
        let providerFailure =
            CircuitFailure.Create(CircuitFailureCode.Provider, "provider failed")

        let successful =
            Circuit.code "recover-success-input" "1.0.0" (fun context value -> Helpers.success context value)
            |> Circuit.recover "recover-success" "1.0.0" (fun _ -> -1)

        let! success = Circuit.run Helpers.runtime successful 7 Helpers.options CancellationToken.None
        Assert.True(success.IsSuccess)
        Assert.Equal(7, success.Value)

        let failed: Circuit<int, int> =
            Circuit.code "recover-failed-input" "1.0.0" (fun context _ ->
                Response.fail context providerFailure |> Task.FromResult)

        let recovered =
            failed
            |> Circuit.recover "recover-value" "1.0.0" (fun failure -> int failure.Code)

        let! recovery = Circuit.run Helpers.runtime recovered 0 Helpers.options CancellationToken.None
        Assert.True(recovery.IsSuccess)
        Assert.Equal(int CircuitFailureCode.Provider, recovery.Value)

        let throwing =
            failed
            |> Circuit.recover "recover-throws" "1.0.0" (fun _ -> raise (InvalidOperationException("recovery failed")))

        let! reported = Circuit.run Helpers.runtime throwing 0 Helpers.options CancellationToken.None
        Assert.False(reported.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, reported.Failure.Code)
        Assert.Contains("recovery handler failed", reported.Failure.Message, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``aggregate reports successful failed and throwing handler outcomes`` () =
    task {
        let source =
            Circuit.items "aggregate-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1; 2; 3 ])

        let summed =
            source
            |> Circuit.aggregate "sum" "1.0.0" (fun context responses _ ->
                responses |> Seq.sumBy _.Value |> Response.succeed context |> Task.FromResult)

        let! sum = Circuit.run Helpers.runtime summed () Helpers.options CancellationToken.None
        Assert.True(sum.IsSuccess)
        Assert.Equal(6, sum.Value)

        let controlledFailure =
            CircuitFailure.Create(CircuitFailureCode.Validation, "aggregate rejected")

        let rejected =
            source
            |> Circuit.aggregate "reject" "1.0.0" (fun context _ _ ->
                Response.fail context controlledFailure |> Task.FromResult)

        let! rejection = Circuit.run Helpers.runtime rejected () Helpers.options CancellationToken.None
        Assert.False(rejection.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, rejection.Failure.Code)

        let throwing =
            source
            |> Circuit.aggregate "throw" "1.0.0" (fun _ _ _ ->
                Task.FromException<Response<int>>(InvalidOperationException("aggregate failed")))

        let! reported = Circuit.run Helpers.runtime throwing () Helpers.options CancellationToken.None
        Assert.False(reported.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, reported.Failure.Code)
        Assert.Contains("aggregate handler failed", reported.Failure.Message, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``branch selects exact and default cases and reports selector failures or missing cases`` () =
    task {
        let cases = Dictionary<string, Circuit<int, string>>(StringComparer.Ordinal)

        cases.Add("even", Circuit.value "matched")

        let withDefault =
            Circuit.branch
                "parity"
                "1.0.0"
                (fun value -> if value % 2 = 0 then "even" else "odd")
                cases
                (ValueSome(Circuit.value "defaulted"))

        let! exact = Circuit.run Helpers.runtime withDefault 2 Helpers.options CancellationToken.None
        let! fallback = Circuit.run Helpers.runtime withDefault 3 Helpers.options CancellationToken.None
        Assert.Equal("matched", exact.Value)
        Assert.Equal("defaulted", fallback.Value)

        let withoutDefault =
            Circuit.branch "required-case" "1.0.0" (fun _ -> "missing") cases ValueNone

        let! missing = Circuit.run Helpers.runtime withoutDefault 1 Helpers.options CancellationToken.None
        Assert.False(missing.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, missing.Failure.Code)
        Assert.Contains("No branch matched", missing.Failure.Message)

        let throwingSelector =
            Circuit.branch
                "throwing-selector"
                "1.0.0"
                (fun _ -> raise (InvalidOperationException("selector failed")))
                cases
                ValueNone

        let! selectorFailure = Circuit.run Helpers.runtime throwingSelector 1 Helpers.options CancellationToken.None

        Assert.False(selectorFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, selectorFailure.Failure.Code)
        Assert.Contains("branch selector failed", selectorFailure.Failure.Message, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``branch child failures retain scheduler ownership instead of becoming selector failures`` () =
    task {
        let failingSource message =
            Circuit.asyncSource "branch-child-source" "1.0.0" (fun (_: int) ->
                { new IAsyncEnumerable<string> with
                    member _.GetAsyncEnumerator(_cancellationToken) =
                        raise (InvalidOperationException(message)) })

        let cases = Dictionary<string, Circuit<int, string>>(StringComparer.Ordinal)
        cases["exact"] <- failingSource "exact child failed"

        let exactBranch =
            Circuit.branch "exact-child" "1.0.0" (fun _ -> "exact") cases ValueNone

        let defaultBranch =
            Circuit.branch
                "default-child"
                "1.0.0"
                (fun _ -> "other")
                cases
                (ValueSome(failingSource "default child failed"))

        for definition, expected in [ exactBranch, "exact child failed"; defaultBranch, "default child failed" ] do
            let! result = Circuit.run Helpers.runtime definition 1 Helpers.options CancellationToken.None
            Assert.False(result.IsSuccess)
            Assert.Equal(CircuitFailureCode.Engine, result.Failure.Code)
            Assert.Contains("scheduler failed", result.Failure.Message, StringComparison.OrdinalIgnoreCase)
            Assert.DoesNotContain("selector", result.Failure.Message, StringComparison.OrdinalIgnoreCase)
            Assert.Contains(expected, string result.Failure.Exception.Value)
    }

[<Fact>]
let ``async source collection emits admitted items and reports enumerator failures`` () =
    task {
        let mutable disposals = 0

        let source (values: int array) (failure: exn option) =
            { new IAsyncEnumerable<int> with
                member _.GetAsyncEnumerator(_cancellationToken) =
                    let mutable index = -1

                    { new IAsyncEnumerator<int> with
                        member _.Current = values[index]

                        member _.MoveNextAsync() =
                            index <- index + 1

                            if index < values.Length then
                                ValueTask<bool>(true)
                            else
                                match failure with
                                | Some ex -> ValueTask<bool>(Task.FromException<bool>(ex))
                                | None -> ValueTask<bool>(false)

                        member _.DisposeAsync() =
                            Interlocked.Increment(&disposals) |> ignore
                            ValueTask() } }

        let complete =
            Circuit.asyncSource "async-complete" "1.0.0" (fun (_: unit) -> source [| 4; 5; 6 |] None)

        let! collected = Circuit.collect Helpers.runtime complete () Helpers.options CancellationToken.None
        Assert.True(collected.IsSuccess)
        Assert.Equal<int list>([ 4; 5; 6 ], collected.Value |> Seq.map _.Value |> Seq.toList)
        Assert.Equal(1, disposals)

        let broken =
            Circuit.asyncSource "async-broken" "1.0.0" (fun (_: unit) ->
                source [| 9 |] (Some(InvalidOperationException("enumeration failed"))))

        let! reported = Circuit.collect Helpers.runtime broken () Helpers.options CancellationToken.None
        Assert.False(reported.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, reported.Failure.Code)
        Assert.Contains("enumeration failed", string reported.Failure.Exception.Value)
        Assert.Equal(2, disposals)
    }

[<Fact>]
let ``scheduler graph decisions count validate and type every node shape`` () =
    let leaf = Circuit.value 1

    let items =
        Circuit.items "decision-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 1 ])

    let asyncItems =
        Circuit.asyncSource "decision-async" "1.0.0" (fun (_: unit) ->
            { new IAsyncEnumerable<int> with
                member _.GetAsyncEnumerator(_cancellationToken) =
                    { new IAsyncEnumerator<int> with
                        member _.Current = 1
                        member _.MoveNextAsync() = ValueTask<bool>(false)
                        member _.DisposeAsync() = ValueTask() } })

    let resumable =
        { new IResumableCircuitSource<unit, int> with
            member _.ReadAsync(_, _, _) =
                ValueTask<CircuitSourcePage<int>>(CircuitSourcePage(Helpers.toArray [ 1 ], ValueNone, true)) }
        |> Circuit.source "decision-source" "1.0.0"

    let code =
        Circuit.code "decision-code" "1.0.0" (fun context value -> Helpers.success context value)

    let thenNode = leaf |> Circuit.thenStep code

    let dynamic =
        leaf |> Circuit.thenDynamic "decision-dynamic" "1.0.0" string 1 Circuit.value

    let attempted = code |> Circuit.attempt
    let recovered = code |> Circuit.recover "decision-recover" "1.0.0" (fun _ -> 0)
    let named = code |> Circuit.named "decision-named"

    let approval =
        Circuit.approval "decision-approval" "1.0.0" (fun (_: unit) -> ApprovalPrompt.Create("Review", "Approve"))

    let aggregate =
        items
        |> Circuit.aggregate "decision-aggregate" "1.0.0" (fun context values _ ->
            Response.succeed context values.Count |> Task.FromResult)

    let cases = Dictionary<string, Circuit<int, int>>()
    cases["one"] <- Circuit.value 1

    let branch =
        Circuit.branch "decision-branch" "1.0.0" string cases (ValueSome(Circuit.value 0))

    let merge = Circuit.merge "decision-merge" "1.0.0" 1 (Helpers.toArray [ code ])
    let loop = Circuit.loop "decision-loop" "1.0.0" 2 (fun value -> value < 1) code

    let nodes =
        [ leaf.Node
          items.Node
          asyncItems.Node
          resumable.Node
          code.Node
          thenNode.Node
          dynamic.Node
          attempted.Node
          recovered.Node
          named.Node
          approval.Node
          aggregate.Node
          branch.Node
          merge.Node
          loop.Node ]

    for node in nodes do
        Assert.True(SchedulerInternals.nodeCount node >= 1)
        Assert.NotNull(SchedulerInternals.outputType node)
        SchedulerInternals.validateGeneratedNode node

    let nodeIds = nodes |> List.map SchedulerInternals.nodeId
    Assert.Equal(nodes.Length, nodeIds.Length)
    Assert.Contains("then", nodeIds)
    Assert.Contains("decision-async", nodeIds)
    Assert.Contains("decision-source", nodeIds)
    Assert.Contains("decision-dynamic", nodeIds)
    Assert.Contains("attempt", nodeIds)
    Assert.Contains("decision-recover", nodeIds)
    Assert.Contains("decision-branch", nodeIds)
    Assert.Contains("decision-loop", nodeIds)

    let noOrder = Array.empty<int64> :> IReadOnlyList<int64>
    let laneKey = ItemKey.Create("item")

    let keyedOrdinal =
        SchedulerInternals.laneFromMetadata (ValueSome laneKey) (ValueSome 3L) noOrder

    let keyed =
        SchedulerInternals.laneFromMetadata (ValueSome laneKey) ValueNone noOrder

    let ordinal = SchedulerInternals.laneFromMetadata ValueNone (ValueSome 4L) noOrder
    let root = SchedulerInternals.laneFromMetadata ValueNone ValueNone noOrder
    Assert.Equal("k4:item;o3", keyedOrdinal.Identity)
    Assert.Equal("k4:item", keyed.Identity)
    Assert.Equal("o4", ordinal.Identity)
    Assert.Equal("root", root.Identity)

    Assert.Equal(2, SchedulerInternals.nodeCount thenNode.Node)
    Assert.Equal(2, SchedulerInternals.nodeCount attempted.Node)
    Assert.Equal(3, SchedulerInternals.nodeCount branch.Node)
    Assert.Equal(2, SchedulerInternals.nodeCount merge.Node)
    Assert.Equal(2, SchedulerInternals.nodeCount loop.Node)

    let invalidDynamic =
        match dynamic.Node with
        | CircuitGraph.Dynamic(id, version, _, handler, previous) ->
            CircuitGraph.Dynamic(id, version, 0, handler, previous)
        | _ -> failwith "Expected dynamic node."

    let invalidMerge =
        match merge.Node with
        | CircuitGraph.Merge(id, version, _, branches) -> CircuitGraph.Merge(id, version, 0, branches)
        | _ -> failwith "Expected merge node."

    let invalidLoop =
        match loop.Node with
        | CircuitGraph.Loop(id, version, _, handler) -> CircuitGraph.Loop(id, version, 0, handler)
        | _ -> failwith "Expected loop node."

    Assert.Throws<InvalidOperationException>(fun () -> SchedulerInternals.validateGeneratedNode invalidDynamic)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> SchedulerInternals.validateGeneratedNode invalidMerge)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> SchedulerInternals.validateGeneratedNode invalidLoop)
    |> ignore

[<Fact>]
let ``scheduler agent discovery traverses static graph containers`` () =
    let agent =
        AgentDefinition.Create(
            "decision.agent",
            "1.0.0",
            "Decision agent",
            "Echo input.",
            ValueNone,
            Seq.empty,
            Seq.empty,
            Seq.empty
        )

    let signature =
        Signature<int, int>
            .Create(
                "decision.signature",
                "1.0.0",
                "Decision",
                "Echo",
                CircuitJson.createOptions (),
                Seq.empty,
                Seq.empty
            )

    let agentCircuit = Circuit.agent agent signature
    let cases = Dictionary<string, Circuit<int, int>>()
    cases["agent"] <- agentCircuit

    let branch =
        Circuit.branch
            "agent-branch"
            "1.0.0"
            (fun _ -> "agent")
            cases
            (ValueSome(agentCircuit |> Circuit.named "fallback-agent"))

    let merged =
        Circuit.merge
            "agent-merge"
            "1.0.0"
            2
            (Helpers.toArray [ branch; Circuit.loop "agent-loop" "1.0.0" 1 (fun _ -> false) agentCircuit ])

    let ids = SchedulerInternals.agentIds merged.Node |> Seq.toArray
    Assert.Equal(3, ids.Length)
    Assert.All(ids, fun value -> Assert.Equal("decision.agent@1.0.0", value))

[<Fact>]
let ``checkpoint resume state round trips every durable collection`` () =
    let state = SchedulerInternals.emptyResumeState ()
    let now = DateTimeOffset.UtcNow

    state.Responses["success"] <-
        { Success = true
          ValueJson = "42"
          FailureCode = 0
          FailureMessage = null
          FailureOperationId = null
          FailureRequestId = null
          InputTokens = 1
          OutputTokens = 2
          Attempt = 1
          StartedAt = now
          CompletedAt = now
          IdempotencyKey = "success-key"
          SourceOrder = [| 1L; 2L |] }

    state.Responses["failure"] <-
        { Success = false
          ValueJson = null
          FailureCode = int CircuitFailureCode.Provider
          FailureMessage = "failed"
          FailureOperationId = "operation"
          FailureRequestId = "request"
          InputTokens = 3
          OutputTokens = 4
          Attempt = 2
          StartedAt = now
          CompletedAt = now
          IdempotencyKey = "failure-key"
          SourceOrder = Array.empty }

    state.DynamicFingerprints["dynamic"] <- "fingerprint"
    state.DynamicInputs["dynamic"] <- "1"
    state.SourceCursors["source"] <- "cursor"
    state.SourceCounts["source"] <- 2L
    state.SourcePageCounts["source"] <- 1
    state.SourceSnapshots["source"] <- [| "1"; "2" |]
    state.SourcePendingCursors["pending-null"] <- null
    state.SourcePendingCursors["pending-value"] <- "next"
    state.SourcePendingCompleted.Add("pending-value") |> ignore
    state.PendingApprovalIds.Add("approval") |> ignore

    state.PendingApprovalRequests["approval"] <-
        ApprovalRequest(
            "approval",
            "tool.one",
            ValueSome "{}",
            ValueSome(ApprovalPrompt("Review", "Approve", seq [ KeyValuePair("risk", "low") ]))
        )

    state.PendingApprovalRequests["approval-null"] <- ApprovalRequest("approval-null", "tool.two", ValueNone, ValueNone)

    state.AcceptedApprovals["accepted"] <- { Approved = true; Note = "approved" }
    state.AcceptedApprovals["accepted-null"] <- { Approved = false; Note = null }
    state.GeneratedNodeCount <- 1
    state.ApprovalRoundCount <- 2
    state.SessionAliases["leaf"] <- "group"
    use adapterDocument = JsonDocument.Parse("{\"provider\":true}")
    state.SerializedSessions["group"] <- struct ("agent@1.0.0", adapterDocument.RootElement.Clone())

    let payload =
        SchedulerInternals.writeResumeState state typeof<int> (box 7) Helpers.options

    let parsed = SchedulerInternals.parseResumeState payload

    let parsedOptions =
        SchedulerInternals.parseRunOptions payload Helpers.options.Services

    Assert.Equal(2, parsed.Responses.Count)
    Assert.True(parsed.Responses["success"].Success)
    Assert.False(parsed.Responses["failure"].Success)
    Assert.Equal("operation", parsed.Responses["failure"].FailureOperationId)
    Assert.Equal<int64[]>([| 1L; 2L |], parsed.Responses["success"].SourceOrder)
    Assert.Equal("fingerprint", parsed.DynamicFingerprints["dynamic"])
    Assert.Equal(2L, parsed.SourceCounts["source"])
    Assert.Null(parsed.SourcePendingCursors["pending-null"])
    Assert.True(parsed.SourcePendingCompleted.Contains("pending-value"))
    Assert.True(parsed.PendingApprovalRequests["approval"].Prompt.IsSome)
    Assert.True(parsed.PendingApprovalRequests["approval-null"].Prompt.IsNone)
    Assert.Equal("approved", parsed.AcceptedApprovals["accepted"].Note)
    Assert.Null(parsed.AcceptedApprovals["accepted-null"].Note)
    Assert.Equal(1, parsed.GeneratedNodeCount)
    Assert.Equal(2, parsed.ApprovalRoundCount)
    Assert.Equal("group", parsed.SessionAliases["leaf"])
    Assert.Equal(Helpers.options.MaxConcurrency, parsedOptions.MaxConcurrency)
    Assert.Same(Helpers.options.Services, parsedOptions.Services)

[<Fact>]
let ``checkpoint resume parser rejects malformed durable collection shapes`` () =
    let valid =
        SchedulerInternals.writeResumeState (SchedulerInternals.emptyResumeState ()) typeof<int> (box 7) Helpers.options

    let expectInvalid mutate expected =
        let root = JsonNode.Parse(valid.GetRawText()).AsObject()
        mutate root
        use document = JsonDocument.Parse(root.ToJsonString())

        let ex =
            Assert.Throws<JsonException>(fun () -> SchedulerInternals.parseResumeState document.RootElement |> ignore)

        Assert.Contains(expected, ex.Message)

    expectInvalid (fun root -> root["responses"] <- JsonValue.Create("wrong")) "responses"

    expectInvalid
        (fun root -> root["responses"] <- JsonArray(JsonNode.Parse("{\"key\":\"x\",\"success\":\"yes\"}")))
        "success"

    expectInvalid (fun root -> root["sourceCursors"].AsObject()["source"] <- JsonValue.Create(1)) "non-string"

    expectInvalid
        (fun root -> root["sourcePending"].AsObject()["source"] <- JsonValue.Create("wrong"))
        "pending source page"

    expectInvalid
        (fun root -> root["sourcePending"].AsObject()["source"] <- JsonNode.Parse("{\"completed\":true}"))
        "missing its cursor"

    expectInvalid
        (fun root -> root["sourcePending"].AsObject()["source"] <- JsonNode.Parse("{\"cursor\":1,\"completed\":true}"))
        "cursor must be"

    expectInvalid
        (fun root ->
            root["sourcePending"].AsObject()["source"] <- JsonNode.Parse("{\"cursor\":null,\"completed\":\"yes\"}"))
        "completed flag"

    expectInvalid
        (fun root -> root["sourceSnapshots"].AsObject()["source"] <- JsonValue.Create(1))
        "snapshots must be arrays"

    expectInvalid (fun root -> root["sourceCounts"].AsObject()["source"] <- JsonValue.Create(-1)) "cannot be negative"

    expectInvalid
        (fun root -> root["sourcePageCounts"].AsObject()["source"] <- JsonValue.Create(-1))
        "cannot be negative"

    expectInvalid
        (fun root -> root["pendingApprovalIds"] <- JsonArray(JsonValue.Create(1)))
        "identifiers must be strings"

    expectInvalid
        (fun root -> root["pendingApprovalRequests"].AsObject()["request"] <- JsonValue.Create("wrong"))
        "requests must be objects"

    expectInvalid
        (fun root -> root["acceptedApprovals"].AsObject()["request"] <- JsonValue.Create("wrong"))
        "decisions must be objects"

    expectInvalid (fun root -> root["generatedNodeCount"] <- JsonValue.Create(-1)) "counters cannot be negative"

    expectInvalid
        (fun root -> root["sessionAliases"].AsObject()["leaf"] <- JsonValue.Create(1))
        "aliases must be strings"

    expectInvalid
        (fun root -> root["sessions"].AsObject()["group"] <- JsonValue.Create("wrong"))
        "sessions must be objects"

    expectInvalid
        (fun root -> root["sessions"].AsObject()["group"] <- JsonNode.Parse("{\"agentId\":\"agent@1.0.0\"}"))
        "missing its adapter state"

[<Fact>]
let ``loop reports predicate cardinality and body failure edge cases`` () =
    task {
        let identity =
            Circuit.code "loop-edge-identity" "1.0.0" (fun context value -> Helpers.success context value)

        let throwingPredicate =
            Circuit.loop
                "loop-throwing-predicate"
                "1.0.0"
                2
                (fun _ -> raise (InvalidOperationException("predicate failed")))
                identity

        let! predicateFailure = Circuit.run Helpers.runtime throwingPredicate 0 Helpers.options CancellationToken.None

        Assert.False(predicateFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, predicateFailure.Failure.Code)
        Assert.Contains("predicate failed", string predicateFailure.Failure.Exception.Value)

        let manyBody =
            Circuit.items "loop-many-body" "1.0.0" (fun value -> Helpers.toArray [ value; value + 1 ])

        let manyLoop = Circuit.loop "loop-many" "1.0.0" 2 (fun _ -> true) manyBody
        let! cardinalityFailure = Circuit.run Helpers.runtime manyLoop 0 Helpers.options CancellationToken.None
        Assert.False(cardinalityFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, cardinalityFailure.Failure.Code)
        Assert.Contains("exactly one", string cardinalityFailure.Failure.Exception.Value)

        let controlled =
            CircuitFailure.Create(CircuitFailureCode.Validation, "body rejected")

        let failingBody =
            Circuit.code "loop-failed-body" "1.0.0" (fun context (_: int) ->
                Response.fail context controlled |> Task.FromResult)

        let failedLoop = Circuit.loop "loop-failed" "1.0.0" 2 (fun _ -> true) failingBody
        let! bodyFailure = Circuit.run Helpers.runtime failedLoop 0 Helpers.options CancellationToken.None
        Assert.False(bodyFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, bodyFailure.Failure.Code)

        let noIterations = Circuit.loop "loop-stop" "1.0.0" 2 (fun _ -> false) identity
        let! stopped = Circuit.run Helpers.runtime noIterations 9 Helpers.options CancellationToken.None
        Assert.True(stopped.IsSuccess)
        Assert.Equal(9, stopped.Value)
    }

[<Fact>]
let ``resumable source reports page limit size completion and provider failures`` () =
    task {
        let pageLimitSource =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, cursor, _) =
                    let next =
                        cursor
                        |> ValueOption.map (fun value -> value + "x")
                        |> ValueOption.defaultValue "x"

                    ValueTask<CircuitSourcePage<int>>(CircuitSourcePage(Helpers.toArray [ 1 ], ValueSome next, false)) }

        let strictPages =
            Helpers.options.WithLimits(
                Helpers.options.MaxDynamicDepth,
                Helpers.options.MaxDynamicNodes,
                Helpers.options.MaxApprovalRounds,
                Helpers.options.MaxSourcePageSize,
                1
            )

        let! pageLimit =
            Circuit.collect
                Helpers.runtime
                (Circuit.source "source-page-limit" "1.0.0" pageLimitSource)
                ()
                strictPages
                CancellationToken.None

        Assert.False(pageLimit.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, pageLimit.Failure.Code)
        Assert.Contains("page limit", pageLimit.Failure.Message)

        let oversizedSource =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    ValueTask<CircuitSourcePage<int>>(CircuitSourcePage(Helpers.toArray [ 1; 2 ], ValueNone, true)) }

        let strictSize =
            Helpers.options.WithLimits(
                Helpers.options.MaxDynamicDepth,
                Helpers.options.MaxDynamicNodes,
                Helpers.options.MaxApprovalRounds,
                1,
                Helpers.options.MaxSourcePages
            )

        let! oversized =
            Circuit.collect
                Helpers.runtime
                (Circuit.source "source-page-size" "1.0.0" oversizedSource)
                ()
                strictSize
                CancellationToken.None

        Assert.False(oversized.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, oversized.Failure.Code)
        Assert.Contains("2 items", oversized.Failure.Message)

        let emptySource =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    ValueTask<CircuitSourcePage<int>>(CircuitSourcePage(Helpers.toArray [], ValueNone, true)) }

        let! empty =
            Circuit.collect
                Helpers.runtime
                (Circuit.source "source-empty" "1.0.0" emptySource)
                ()
                Helpers.options
                CancellationToken.None

        Assert.True(empty.IsSuccess)
        Assert.Empty(empty.Value)

        let throwingSource =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    raise (InvalidOperationException("source provider failed")) }

        let! thrown =
            Circuit.collect
                Helpers.runtime
                (Circuit.source "source-throws" "1.0.0" throwingSource)
                ()
                Helpers.options
                CancellationToken.None

        Assert.False(thrown.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, thrown.Failure.Code)
        Assert.Contains("source provider failed", string thrown.Failure.Exception.Value)
    }

[<Fact>]
let ``checkpoint run options reject malformed persisted values`` () =
    let valid =
        SchedulerInternals.writeResumeState (SchedulerInternals.emptyResumeState ()) typeof<int> (box 7) Helpers.options

    let expectInvalid mutate =
        let root = JsonNode.Parse(valid.GetRawText()).AsObject()
        mutate (root["options"].AsObject())
        use document = JsonDocument.Parse(root.ToJsonString())

        Assert.ThrowsAny<Exception>(fun () ->
            SchedulerInternals.parseRunOptions document.RootElement Helpers.options.Services
            |> ignore)
        |> ignore

    expectInvalid (fun options -> options.Remove("tenantId") |> ignore)
    expectInvalid (fun options -> options["tenantId"] <- JsonValue.Create(1))
    expectInvalid (fun options -> options["userId"] <- JsonValue.Create(true))
    expectInvalid (fun options -> options["tags"] <- JsonArray())
    expectInvalid (fun options -> options["tags"].AsObject()["invalid"] <- JsonValue.Create(1))
    expectInvalid (fun options -> options["structuredOutputPolicy"] <- JsonValue.Create("native"))
    expectInvalid (fun options -> options["sensitiveDataMode"] <- JsonValue.Create("standard"))
    expectInvalid (fun options -> options["maxConcurrency"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["eventBufferCapacity"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxDynamicDepth"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxDynamicNodes"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxApprovalRounds"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxSourcePageSize"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxSourcePages"] <- JsonValue.Create(0))
    expectInvalid (fun options -> options["maxCheckpointBytes"] <- JsonValue.Create(100))
    expectInvalid (fun options -> options["disposalDrainMilliseconds"] <- JsonValue.Create(-2.0))

[<Fact>]
let ``checkpoint writer rejects aliases without serialized session boundaries`` () =
    let state = SchedulerInternals.emptyResumeState ()
    state.SessionAliases["leaf"] <- "missing-group"

    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            SchedulerInternals.writeResumeState state typeof<int> (box 7) Helpers.options
            |> ignore)

    Assert.Contains("checkpoint-safe adapter boundary", ex.Message)

[<Fact>]
let ``checkpoint response parser rejects malformed and duplicate response entries`` () =
    let valid =
        SchedulerInternals.writeResumeState (SchedulerInternals.emptyResumeState ()) typeof<int> (box 7) Helpers.options

    let successJson =
        """{"key":"entry","success":true,"value":42,"inputTokens":1,"outputTokens":2,"attempt":1,"startedAt":"2026-01-01T00:00:00+00:00","completedAt":"2026-01-01T00:00:01+00:00","idempotencyKey":"key","sourceOrder":[1]}"""

    let failureJson =
        """{"key":"entry","success":false,"failureCode":2,"failureMessage":"failed","inputTokens":1,"outputTokens":2,"attempt":1,"startedAt":"2026-01-01T00:00:00+00:00","completedAt":"2026-01-01T00:00:01+00:00","idempotencyKey":"key","sourceOrder":[]}"""

    let expectInvalid (entries: JsonArray) expected =
        let root = JsonNode.Parse(valid.GetRawText()).AsObject()
        root["responses"] <- entries
        use document = JsonDocument.Parse(root.ToJsonString())

        let ex =
            Assert.Throws<JsonException>(fun () -> SchedulerInternals.parseResumeState document.RootElement |> ignore)

        Assert.Contains(expected, ex.Message)

    let mutate (json: string) change =
        let entry = JsonNode.Parse(json).AsObject()
        change entry
        JsonArray(entry)

    expectInvalid (mutate successJson (fun entry -> entry.Remove("value") |> ignore)) "missing its value"

    expectInvalid
        (mutate successJson (fun entry -> entry["sourceOrder"] <- JsonArray(JsonValue.Create("wrong"))))
        "must be numbers"

    expectInvalid (mutate successJson (fun entry -> entry["inputTokens"] <- JsonValue.Create("one"))) "inputTokens"
    expectInvalid (mutate successJson (fun entry -> entry["outputTokens"] <- JsonValue.Create("two"))) "outputTokens"
    expectInvalid (mutate successJson (fun entry -> entry["attempt"] <- JsonValue.Create("one"))) "attempt"
    expectInvalid (mutate successJson (fun entry -> entry["startedAt"] <- JsonValue.Create(1))) "startedAt"
    expectInvalid (mutate successJson (fun entry -> entry["completedAt"] <- JsonValue.Create(1))) "completedAt"
    expectInvalid (mutate successJson (fun entry -> entry["idempotencyKey"] <- JsonValue.Create(1))) "idempotencyKey"
    expectInvalid (mutate failureJson (fun entry -> entry.Remove("failureCode") |> ignore)) "failureCode"
    expectInvalid (mutate failureJson (fun entry -> entry.Remove("failureMessage") |> ignore)) "failureMessage"

    expectInvalid (JsonArray(JsonNode.Parse(successJson), JsonNode.Parse(successJson))) "duplicated"

[<Fact>]
let ``checkpoint approval parser rejects malformed request and prompt fields`` () =
    let valid =
        SchedulerInternals.writeResumeState (SchedulerInternals.emptyResumeState ()) typeof<int> (box 7) Helpers.options

    let requestJson =
        """{"toolName":"tool.one","argumentsJson":"{}","prompt":{"title":"Review","message":"Approve","metadata":{"risk":"low"}}}"""

    let expectInvalid change expected =
        let root = JsonNode.Parse(valid.GetRawText()).AsObject()
        let request = JsonNode.Parse(requestJson).AsObject()
        change request
        root["pendingApprovalRequests"].AsObject()["request"] <- request
        use document = JsonDocument.Parse(root.ToJsonString())

        let ex =
            Assert.ThrowsAny<Exception>(fun () -> SchedulerInternals.parseResumeState document.RootElement |> ignore)

        Assert.Contains(expected, ex.Message)

    expectInvalid (fun request -> request["toolName"] <- JsonValue.Create(1)) "toolName"
    expectInvalid (fun request -> request.Remove("argumentsJson") |> ignore) "missing argumentsJson"
    expectInvalid (fun request -> request["argumentsJson"] <- JsonValue.Create(1)) "string or null"
    expectInvalid (fun request -> request.Remove("prompt") |> ignore) "missing its prompt"
    expectInvalid (fun request -> request["prompt"] <- JsonValue.Create("wrong")) "object or null"

    expectInvalid (fun request -> request["prompt"].AsObject()["title"] <- JsonValue.Create(1)) "title"

    expectInvalid (fun request -> request["prompt"].AsObject()["message"] <- JsonValue.Create(1)) "message"

    expectInvalid (fun request -> request["prompt"].AsObject()["metadata"] <- JsonArray()) "metadata"

    expectInvalid
        (fun request ->
            let prompt = request["prompt"].AsObject()
            let metadata = prompt["metadata"].AsObject()
            metadata["risk"] <- JsonValue.Create(1))
        "metadata values"

    use scalar = JsonDocument.Parse("1")

    let scalarEx =
        Assert.Throws<JsonException>(fun () -> SchedulerInternals.parseResumeState scalar.RootElement |> ignore)

    Assert.Contains("must be an object", scalarEx.Message)

module private ProjectionFixtures =
    let metadata runId path =
        let now = DateTimeOffset.UtcNow

        ResponseMetadata(
            ValueNone,
            ValueNone,
            Array.empty,
            runId,
            path,
            RunUsage(0, 0),
            ValueNone,
            1,
            now,
            now,
            "projection-test"
        )

    let output runId path value =
        let metadata = metadata runId path
        Response<int>.Create(Succeeded value, metadata)

    let terminal runId outcome =
        let metadata = metadata runId "terminal"
        Response<RunSummary>.Create(outcome, metadata)

    let successTerminal runId count =
        let now = DateTimeOffset.UtcNow
        let summary = RunSummary(count, count, 0, RunUsage(0, 0), now, now)
        terminal runId (Succeeded summary)

    let runStarted runId =
        RunStarted(
            RunInfo(
                runId,
                "projection-lineage",
                DefinitionId.Create("projection-test"),
                SemanticVersion.Parse("1.0.0"),
                "projection-fingerprint",
                DateTimeOffset.UtcNow
            )
        )

[<Fact>]
let ``stream lazily ignores structural events yields outputs and owns disposal`` () =
    task {
        let runId = RunId.New()
        let response = ProjectionFixtures.output runId "value" 42
        let node = NodeInfo("value", "value", ValueNone, 1, DateTimeOffset.UtcNow)

        let events =
            [| ProjectionFixtures.runStarted runId
               NodeStarted node
               OutputDelta(CircuitOutputDelta("value", ValueNone, "working", DateTimeOffset.UtcNow))
               OutputProduced(ValueNone, response)
               NodeCompleted(node, UntypedResponse(true, ValueNone, response.Metadata))
               RunCompleted(ProjectionFixtures.successTerminal runId 1) |]

        let runtime = ScriptedProjectionRuntime(events)

        let stream =
            Circuit.stream (runtime :> ICircuitRuntime) (Circuit.value 42) () RunOptions.Default CancellationToken.None

        Assert.Equal(0, runtime.Starts)

        let enumerator = stream.GetAsyncEnumerator()
        let! first = enumerator.MoveNextAsync().AsTask()
        Assert.True(first)
        Assert.Equal(42, enumerator.Current.Value)
        Assert.Equal(1, runtime.Starts)

        let! completed = enumerator.MoveNextAsync().AsTask()
        Assert.False(completed)
        let movesAtCompletion = runtime.EventMoves

        let! repeated = enumerator.MoveNextAsync().AsTask()
        Assert.False(repeated)
        Assert.Equal(movesAtCompletion, runtime.EventMoves)

        do! enumerator.DisposeAsync().AsTask()
        Assert.Equal(1, runtime.EventDisposals)
        Assert.Equal(1, runtime.Disposals)
    }

[<Fact>]
let ``stream converts approval and failed terminal events into typed failures`` () =
    task {
        let runId = RunId.New()
        let approval = ApprovalRequest("projection-request", "review", ValueSome "{}")
        let ignoredOutput = ProjectionFixtures.output runId "ignored" 99

        let approvalRuntime =
            ScriptedProjectionRuntime([| ApprovalRequested approval; OutputProduced(ValueNone, ignoredOutput) |])

        let approvalStream =
            Circuit.stream
                (approvalRuntime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        let approvalEnumerator = approvalStream.GetAsyncEnumerator()
        let! approvalAvailable = approvalEnumerator.MoveNextAsync().AsTask()
        Assert.True(approvalAvailable)
        Assert.False(approvalEnumerator.Current.IsSuccess)
        Assert.Equal(CircuitFailureCode.ApprovalRequired, approvalEnumerator.Current.Failure.Code)
        Assert.Equal(ValueSome approval.RequestId, approvalEnumerator.Current.Failure.RequestId)
        Assert.Equal(1, approvalRuntime.EventMoves)

        let! approvalComplete = approvalEnumerator.MoveNextAsync().AsTask()
        Assert.False(approvalComplete)
        Assert.Equal(1, approvalRuntime.EventMoves)
        do! approvalEnumerator.DisposeAsync().AsTask()

        let failure =
            CircuitFailure(
                CircuitFailureCode.Provider,
                "provider failed",
                ValueSome runId,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let failedRuntime =
            ScriptedProjectionRuntime([| RunCompleted(ProjectionFixtures.terminal runId (Failed failure)) |])

        let failedStream =
            Circuit.stream
                (failedRuntime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        let failedEnumerator = failedStream.GetAsyncEnumerator()
        let! failedAvailable = failedEnumerator.MoveNextAsync().AsTask()
        Assert.True(failedAvailable)
        Assert.Equal(CircuitFailureCode.Provider, failedEnumerator.Current.Failure.Code)
        let! failedComplete = failedEnumerator.MoveNextAsync().AsTask()
        Assert.False(failedComplete)
        do! failedEnumerator.DisposeAsync().AsTask()
    }

[<Fact>]
let ``stream disposal before initialization does not start a run`` () =
    task {
        let runtime = ScriptedProjectionRuntime(Array.empty)

        let stream =
            Circuit.stream (runtime :> ICircuitRuntime) (Circuit.value 1) () RunOptions.Default CancellationToken.None

        let enumerator = stream.GetAsyncEnumerator()
        do! enumerator.DisposeAsync().AsTask()
        Assert.Equal(0, runtime.Starts)
        Assert.Equal(0, runtime.EventDisposals)
        Assert.Equal(0, runtime.Disposals)
    }

[<Fact>]
let ``stream surfaces initialization and event failures while retaining explicit ownership`` () =
    task {
        let startFailureRuntime = ScriptedProjectionRuntime(Array.empty, failStart = true)

        let startFailureStream =
            Circuit.stream
                (startFailureRuntime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        let startFailureEnumerator = startFailureStream.GetAsyncEnumerator()

        let! startFailure =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                startFailureEnumerator.MoveNextAsync().AsTask() :> Task)

        Assert.Equal("scripted start failure", startFailure.Message)
        Assert.Equal(1, startFailureRuntime.Starts)
        do! startFailureEnumerator.DisposeAsync().AsTask()
        Assert.Equal(0, startFailureRuntime.EventDisposals)
        Assert.Equal(0, startFailureRuntime.Disposals)

        let eventFailureRuntime = ScriptedProjectionRuntime(Array.empty, failAt = 0)

        let eventFailureStream =
            Circuit.stream
                (eventFailureRuntime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        let eventFailureEnumerator = eventFailureStream.GetAsyncEnumerator()

        let! eventFailure =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                eventFailureEnumerator.MoveNextAsync().AsTask() :> Task)

        Assert.Equal("scripted event failure", eventFailure.Message)
        Assert.Equal(0, eventFailureRuntime.EventDisposals)
        Assert.Equal(0, eventFailureRuntime.Disposals)
        do! eventFailureEnumerator.DisposeAsync().AsTask()
        Assert.Equal(1, eventFailureRuntime.EventDisposals)
        Assert.Equal(1, eventFailureRuntime.Disposals)
    }

[<Fact>]
let ``collect classifies missing terminals and disposes runs after event failures`` () =
    task {
        let missingRuntime = ScriptedProjectionRuntime(Array.empty)

        let! missing =
            Circuit.collect
                (missingRuntime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        Assert.False(missing.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, missing.Failure.Code)
        Assert.Contains("without a terminal event", missing.Failure.Message)
        Assert.Equal(1, missingRuntime.EventDisposals)
        Assert.Equal(1, missingRuntime.Disposals)

        let throwingRuntime = ScriptedProjectionRuntime(Array.empty, failAt = 0)

        let! failure =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                Circuit.collect
                    (throwingRuntime :> ICircuitRuntime)
                    (Circuit.value 1)
                    ()
                    RunOptions.Default
                    CancellationToken.None
                :> Task)

        Assert.Equal("scripted event failure", failure.Message)
        Assert.Equal(1, throwingRuntime.EventDisposals)
        Assert.Equal(1, throwingRuntime.Disposals)
    }

[<Fact>]
let ``collect converts approval events to typed projection failures and stops reading`` () =
    task {
        let request = ApprovalRequest("collect-approval", "review", ValueSome "{}")
        let ignored = ProjectionFixtures.output (RunId.New()) "ignored" 42

        let runtime =
            ScriptedProjectionRuntime([| ApprovalRequested request; OutputProduced(ValueNone, ignored) |])

        let! collected =
            Circuit.collect (runtime :> ICircuitRuntime) (Circuit.value 1) () RunOptions.Default CancellationToken.None

        Assert.False(collected.IsSuccess)
        Assert.Equal(CircuitFailureCode.ApprovalRequired, collected.Failure.Code)
        Assert.Equal(ValueSome request.RequestId, collected.Failure.RequestId)
        Assert.Equal(1, runtime.EventMoves)
        Assert.Equal(1, runtime.EventDisposals)
        Assert.Equal(1, runtime.Disposals)
    }

[<Fact>]
let ``collectSourceOrder preserves failed terminal metadata`` () =
    task {
        let runId = RunId.New()

        let failure =
            CircuitFailure(
                CircuitFailureCode.Provider,
                "ordered projection failed",
                ValueSome runId,
                ValueSome "ordered-node",
                ValueNone,
                ValueNone
            )

        let terminal = ProjectionFixtures.terminal runId (Failed failure)
        let runtime = ScriptedProjectionRuntime([| RunCompleted terminal |])

        let! ordered =
            Circuit.collectSourceOrder
                (runtime :> ICircuitRuntime)
                (Circuit.value 1)
                ()
                RunOptions.Default
                CancellationToken.None

        Assert.False(ordered.IsSuccess)
        Assert.Same(failure, ordered.Failure)
        Assert.Same(terminal.Metadata, ordered.Metadata)
        Assert.Equal(1, runtime.EventDisposals)
        Assert.Equal(1, runtime.Disposals)
    }

[<Fact>]
let ``run enforces zero one and many output cardinality over projection events`` () =
    task {
        let definition = Circuit.value 0

        let execute events =
            let runtime = ScriptedProjectionRuntime(events)
            Circuit.run (runtime :> ICircuitRuntime) definition () RunOptions.Default CancellationToken.None

        let zeroRunId = RunId.New()
        let! zero = execute [| RunCompleted(ProjectionFixtures.successTerminal zeroRunId 0) |]
        Assert.False(zero.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cardinality, zero.Failure.Code)
        Assert.Contains("produced 0", zero.Failure.Message)

        let oneRunId = RunId.New()
        let oneResponse = ProjectionFixtures.output oneRunId "one" 1

        let! one =
            execute
                [| OutputProduced(ValueNone, oneResponse)
                   RunCompleted(ProjectionFixtures.successTerminal oneRunId 1) |]

        Assert.True(one.IsSuccess)
        Assert.Equal(1, one.Value)

        let manyRunId = RunId.New()
        let first = ProjectionFixtures.output manyRunId "first" 1
        let second = ProjectionFixtures.output manyRunId "second" 2

        let! many =
            execute
                [| OutputProduced(ValueNone, first)
                   OutputProduced(ValueNone, second)
                   RunCompleted(ProjectionFixtures.successTerminal manyRunId 2) |]

        Assert.False(many.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cardinality, many.Failure.Code)
        Assert.Contains("produced 2", many.Failure.Message)
    }

[<Fact>]
let ``circuit combinators reject null delegates definitions and collections at construction`` () =
    let nullCircuit = Unchecked.defaultof<Circuit<int, int>>
    let value = Circuit.value 1

    let expectNull parameter action =
        let error = Assert.Throws<ArgumentNullException>(Action action)
        Assert.Equal(parameter, error.ParamName)

    expectNull "circuit" (fun () -> Circuit.define "defined" "1.0.0" nullCircuit |> ignore)

    let agent =
        AgentDefinition.Create(
            "construction.agent",
            "1.0.0",
            "Construction agent",
            "Test constructor validation.",
            ValueNone,
            Seq.empty,
            Seq.empty,
            Seq.empty
        )

    let signature =
        Signature<int, int>
            .Create(
                "construction.signature",
                "1.0.0",
                "Construction",
                "Echo",
                CircuitJson.createOptions (),
                Seq.empty,
                Seq.empty
            )

    expectNull "agent" (fun () -> Circuit.agent Unchecked.defaultof<AgentDefinition> signature |> ignore)
    expectNull "signature" (fun () -> Circuit.agent agent Unchecked.defaultof<Signature<int, int>> |> ignore)

    expectNull "handler" (fun () ->
        Circuit.code "null-code" "1.0.0" Unchecked.defaultof<CircuitContext -> int -> Task<Response<int>>>
        |> ignore)

    expectNull "items" (fun () ->
        Circuit.items "null-items" "1.0.0" Unchecked.defaultof<int -> IReadOnlyList<int>>
        |> ignore)

    expectNull "key" (fun () ->
        Circuit.keyedItems "null-key" "1.0.0" Unchecked.defaultof<int -> string> (fun _ -> Helpers.toArray [ 1 ])
        |> ignore)

    expectNull "items" (fun () ->
        Circuit.keyedItems "null-keyed-items" "1.0.0" string Unchecked.defaultof<int -> IReadOnlyList<int>>
        |> ignore)

    expectNull "source" (fun () ->
        Circuit.source "null-source" "1.0.0" Unchecked.defaultof<IResumableCircuitSource<int, int>>
        |> ignore)

    expectNull "source" (fun () ->
        Circuit.asyncSource "null-async-source" "1.0.0" Unchecked.defaultof<int -> IAsyncEnumerable<int>>
        |> ignore)

    expectNull "previous" (fun () -> nullCircuit |> Circuit.thenStep value |> ignore)
    expectNull "next" (fun () -> value |> Circuit.thenStep nullCircuit |> ignore)

    expectNull "key" (fun () ->
        value
        |> Circuit.thenDynamic "null-dynamic-key" "1.0.0" Unchecked.defaultof<int -> string> 1 Circuit.value
        |> ignore)

    expectNull "factory" (fun () ->
        value
        |> Circuit.thenDynamic "null-dynamic-factory" "1.0.0" string 1 Unchecked.defaultof<int -> Circuit<int, int>>
        |> ignore)

    expectNull "previous" (fun () ->
        nullCircuit
        |> Circuit.thenDynamic "null-dynamic-previous" "1.0.0" string 1 Circuit.value
        |> ignore)

    expectNull "previous" (fun () -> Circuit.attempt nullCircuit |> ignore)

    expectNull "handler" (fun () ->
        value
        |> Circuit.recover "null-recovery" "1.0.0" Unchecked.defaultof<CircuitFailure -> int>
        |> ignore)

    expectNull "previous" (fun () ->
        nullCircuit
        |> Circuit.recover "null-recovery-previous" "1.0.0" (fun _ -> 0)
        |> ignore)

    let cases = Dictionary<string, Circuit<int, int>>()
    cases["one"] <- value

    expectNull "selector" (fun () ->
        Circuit.branch "null-selector" "1.0.0" Unchecked.defaultof<int -> string> cases ValueNone
        |> ignore)

    expectNull "cases" (fun () ->
        Circuit.branch
            "null-cases"
            "1.0.0"
            string
            Unchecked.defaultof<IReadOnlyDictionary<string, Circuit<int, int>>>
            ValueNone
        |> ignore)

    expectNull "branches" (fun () ->
        Circuit.merge "null-merge" "1.0.0" 1 Unchecked.defaultof<IReadOnlyList<Circuit<int, int>>>
        |> ignore)

    let emptyBranches =
        Array.empty<Circuit<int, int>> :> IReadOnlyList<Circuit<int, int>>

    let emptyError =
        Assert.Throws<ArgumentException>(fun () -> Circuit.merge "empty-merge" "1.0.0" 1 emptyBranches |> ignore)

    Assert.Equal("branches", emptyError.ParamName)

    expectNull "whileTrue" (fun () ->
        Circuit.loop "null-loop-predicate" "1.0.0" 1 Unchecked.defaultof<int -> bool> value
        |> ignore)

    expectNull "body" (fun () -> Circuit.loop "null-loop-body" "1.0.0" 1 (fun _ -> false) nullCircuit |> ignore)

    expectNull "prompt" (fun () ->
        Circuit.approval "null-approval" "1.0.0" Unchecked.defaultof<int -> ApprovalPrompt>
        |> ignore)

    expectNull "handler" (fun () ->
        value
        |> Circuit.aggregate
            "null-aggregate"
            "1.0.0"
            Unchecked.defaultof<
                CircuitContext -> IReadOnlyList<Response<int>> -> CancellationToken -> Task<Response<int>>
             >
        |> ignore)

    expectNull "previous" (fun () ->
        nullCircuit
        |> Circuit.aggregate "null-aggregate-previous" "1.0.0" (fun context _ _ -> Helpers.success context 0)
        |> ignore)

    expectNull "circuit" (fun () -> Circuit.named "null-named" nullCircuit |> ignore)
    expectNull "circuit" (fun () -> Circuit.validate nullCircuit |> ignore)

[<Fact>]
let ``circuit graph checkpointability and cardinality compose across branch and merge matrices`` () =
    let value = Circuit.value 1

    let asyncValue =
        Circuit.asyncSource "graph-async" "1.0.0" (fun (_: int) ->
            { new IAsyncEnumerable<int> with
                member _.GetAsyncEnumerator(_cancellationToken) =
                    { new IAsyncEnumerator<int> with
                        member _.Current = 0
                        member _.MoveNextAsync() = ValueTask<bool>(false)
                        member _.DisposeAsync() = ValueTask() } })

    let codecPipeline = value |> Circuit.thenStep value
    let nonCheckpointablePipeline = value |> Circuit.thenStep asyncValue
    Assert.Equal(CircuitCheckpointability.CodecDependent, codecPipeline.Checkpointability)
    Assert.Equal(CircuitCheckpointability.NotCheckpointable, nonCheckpointablePipeline.Checkpointability)
    Assert.Equal(CircuitCardinality.Many, nonCheckpointablePipeline.Graph.Cardinality)

    let emptyCases = Dictionary<string, Circuit<int, int>>()
    let emptyBranch = Circuit.branch "empty-branch" "1.0.0" string emptyCases ValueNone
    Assert.Equal(CircuitCheckpointability.Checkpointable, emptyBranch.Checkpointability)
    Assert.Equal(CircuitCardinality.ExactlyOne, emptyBranch.Graph.Cardinality)
    Assert.Single(emptyBranch.Graph.Nodes) |> ignore

    let codecCases = Dictionary<string, Circuit<int, int>>()
    codecCases["value"] <- value
    let codecBranch = Circuit.branch "codec-branch" "1.0.0" string codecCases ValueNone
    Assert.Equal(CircuitCheckpointability.CodecDependent, codecBranch.Checkpointability)

    let asyncCases = Dictionary<string, Circuit<int, int>>()
    asyncCases["async"] <- asyncValue

    let asyncBranch =
        Circuit.branch "async-branch" "1.0.0" string asyncCases (ValueSome value)

    Assert.Equal(CircuitCheckpointability.NotCheckpointable, asyncBranch.Checkpointability)
    Assert.Equal(CircuitCardinality.Many, asyncBranch.Graph.Cardinality)
    Assert.Equal(3, asyncBranch.Graph.Nodes.Count)

    let singleMerge = Circuit.merge "single-merge" "1.0.0" 1 (Helpers.toArray [ value ])
    Assert.Equal(CircuitCardinality.ExactlyOne, singleMerge.Graph.Cardinality)
    Assert.Equal(CircuitCheckpointability.CodecDependent, singleMerge.Checkpointability)

    let manyMerge =
        Circuit.merge "many-merge" "1.0.0" 2 (Helpers.toArray [ value; asyncValue ])

    Assert.Equal(CircuitCardinality.Many, manyMerge.Graph.Cardinality)
    Assert.Equal(CircuitCheckpointability.NotCheckpointable, manyMerge.Checkpointability)
    Assert.Equal(3, manyMerge.Graph.Nodes.Count)

    let defined = Circuit.define "defined-graph" "2.0.0" manyMerge
    Assert.Equal("defined-graph", defined.Id.Value)
    Assert.Equal("2.0.0", defined.Version.ToString())
    Assert.False(StringComparer.Ordinal.Equals(manyMerge.Fingerprint, defined.Fingerprint))
    Assert.Equal(defined.Fingerprint, defined.Graph.Fingerprint)
    Assert.Equal(manyMerge.Checkpointability, defined.Checkpointability)

module private ResumeFixtures =
    let checkpoint definitionId version fingerprint options payload =
        CircuitCheckpoint<int>(
            definitionId,
            version,
            fingerprint,
            "resume-matrix-lineage",
            DateTimeOffset.UtcNow,
            payload,
            options
        )

    let terminalFailure (run: CircuitRun<int>) =
        task {
            let! events = Helpers.collectEvents run

            let terminal =
                events
                |> Array.choose (function
                    | RunCompleted response -> Some response
                    | _ -> None)
                |> Array.exactlyOne

            do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
            Assert.False(terminal.IsSuccess)
            return terminal.Failure
        }

[<Fact>]
let ``agent definitions reject null and duplicate metadata values`` () =
    let create metadata =
        AgentDefinition.Create(
            "metadata-agent",
            "1.0.0",
            "Metadata agent",
            "Validate metadata",
            ValueNone,
            Seq.empty,
            Seq.empty,
            metadata
        )
        |> ignore

    let nullValue =
        Assert.Throws<ArgumentNullException>(fun () -> create [ KeyValuePair("key", Unchecked.defaultof<string>) ])

    Assert.Equal("metadata", nullValue.ParamName)

    let duplicate =
        Assert.Throws<ArgumentException>(fun () -> create [ KeyValuePair("key", "one"); KeyValuePair("key", "two") ])

    Assert.Equal("metadata", duplicate.ParamName)
    Assert.Contains("Duplicate metadata keys", duplicate.Message)

[<Fact>]
let ``source and dynamic factories report null runtime products`` () =
    task {
        let seed =
            Circuit.code "null-dynamic-seed" "1.0.0" (fun context (_: unit) -> Helpers.success context 1)

        let nullDynamic =
            seed
            |> Circuit.thenDynamic "null-dynamic-product" "1.0.0" string 1 (fun _ ->
                Unchecked.defaultof<Circuit<int, int>>)

        let! dynamicFailure = Circuit.collect Helpers.runtime nullDynamic () Helpers.options CancellationToken.None

        Assert.False(dynamicFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.GeneratedGraphIntegrity, dynamicFailure.Failure.Code)
        Assert.Contains("returned null", string dynamicFailure.Failure.Exception.Value)

        let nullPageSource =
            { new IResumableCircuitSource<unit, int> with
                member _.ReadAsync(_, _, _) =
                    ValueTask<CircuitSourcePage<int>>(Unchecked.defaultof<CircuitSourcePage<int>>) }

        let nullPage = Circuit.source "null-source-page" "1.0.0" nullPageSource
        let! pageFailure = Circuit.collect Helpers.runtime nullPage () Helpers.options CancellationToken.None
        Assert.False(pageFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, pageFailure.Failure.Code)
        Assert.True(pageFailure.Failure.Exception.IsSome)

        let nullAsync =
            Circuit.asyncSource "null-async-enumerable" "1.0.0" (fun (_: unit) ->
                Unchecked.defaultof<IAsyncEnumerable<int>>)

        let! asyncFailure = Circuit.collect Helpers.runtime nullAsync () Helpers.options CancellationToken.None
        Assert.False(asyncFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, asyncFailure.Failure.Code)
        Assert.True(asyncFailure.Failure.Exception.IsSome)
    }

[<Fact>]
let ``lane and response constructors reject invalid structural inputs`` () =
    let key = ItemKey.Create("lane")
    Assert.False(key.Equals("lane"))

    let nullPage =
        Assert.Throws<ArgumentNullException>(fun () -> CircuitSourcePage<int>(null, ValueNone, true) |> ignore)

    Assert.Equal("items", nullPage.ParamName)

    let now = DateTimeOffset.UtcNow

    let nullSourceOrder =
        Assert.Throws<ArgumentNullException>(fun () ->
            ResponseMetadata(
                ValueNone,
                ValueNone,
                null,
                RunId.New(),
                "node",
                RunUsage(0, 0),
                ValueNone,
                1,
                now,
                now,
                "key"
            )
            |> ignore)

    Assert.Equal("sourceOrder", nullSourceOrder.ParamName)

    let nullMetadata =
        Assert.Throws<ArgumentNullException>(fun () ->
            Response<int>.Create(Succeeded 1, Unchecked.defaultof<ResponseMetadata>)
            |> ignore)

    Assert.Equal("metadata", nullMetadata.ParamName)

    let nullContext = Unchecked.defaultof<CircuitContext>

    let successContext =
        Assert.Throws<ArgumentNullException>(fun () -> Response.succeed nullContext 1 |> ignore)

    Assert.Equal("context", successContext.ParamName)

    let failure = CircuitFailure.Create(CircuitFailureCode.Provider, "failed")

    let failureContext =
        Assert.Throws<ArgumentNullException>(fun () -> Response.fail nullContext failure |> ignore)

    Assert.Equal("context", failureContext.ParamName)

    let context =
        CircuitContext(RunId.New(), "node", ValueNone, "key", RunOptions.Default, CancellationToken.None)

    let nullFailure =
        Assert.Throws<ArgumentNullException>(fun () ->
            Response.fail context Unchecked.defaultof<CircuitFailure> |> ignore)

    Assert.Equal("failure", nullFailure.ParamName)

[<Fact>]
let ``run and approval option constructors reject invalid host inputs`` () =
    let nullServices =
        Assert.Throws<ArgumentNullException>(fun () -> ResumeOptions(Unchecked.defaultof<IServiceProvider>) |> ignore)

    Assert.Equal("services", nullServices.ParamName)

    let nullMetadata =
        Assert.Throws<ArgumentNullException>(fun () ->
            ApprovalPrompt("Title", "Message", Unchecked.defaultof<IEnumerable<KeyValuePair<string, string>>>)
            |> ignore)

    Assert.Equal("metadata", nullMetadata.ParamName)

    let invalidMetadata =
        [ [ KeyValuePair("", "value") ]
          [ KeyValuePair("key", null) ]
          [ KeyValuePair("key", "one"); KeyValuePair("key", "two") ] ]

    for metadata in invalidMetadata do
        let failure =
            Assert.ThrowsAny<ArgumentException>(fun () -> ApprovalPrompt("Title", "Message", metadata) |> ignore)

        Assert.Equal("metadata", failure.ParamName)

    let blankTitle =
        Assert.Throws<ArgumentException>(fun () -> ApprovalPrompt.Create(" ", "Message") |> ignore)

    Assert.Equal("title", blankTitle.ParamName)

    let nullMessage =
        Assert.Throws<ArgumentNullException>(fun () -> ApprovalPrompt.Create("Title", null) |> ignore)

    Assert.Equal("message", nullMessage.ParamName)

    let blankRequest =
        Assert.Throws<ArgumentException>(fun () -> ApprovalRequest(" ", "tool", ValueNone) |> ignore)

    Assert.Equal("requestId", blankRequest.ParamName)

    let blankTool =
        Assert.Throws<ArgumentException>(fun () -> ApprovalRequest("request", " ", ValueNone) |> ignore)

    Assert.Equal("toolName", blankTool.ParamName)

    let blankResponse =
        Assert.Throws<ArgumentException>(fun () -> ApprovalResponse(" ", true, null) |> ignore)

    Assert.Equal("requestId", blankResponse.ParamName)

    let blankNote =
        Assert.Throws<ArgumentException>(fun () -> ApprovalResponse("request", true, " ") |> ignore)

    Assert.Equal("note", blankNote.ParamName)

[<Fact>]
let ``checkpoint envelopes and live run handles reject malformed lifecycle operations`` () =
    task {
        let parse (text: string) =
            use document = JsonDocument.Parse(text)
            CircuitCheckpoint<int>.Deserialize(document.RootElement) |> ignore

        let nonObject = Assert.Throws<ArgumentException>(fun () -> parse "[]")
        Assert.Equal("state", nonObject.ParamName)
        Assert.Contains("JSON object", nonObject.Message)

        let missingFormat = Assert.Throws<ArgumentException>(fun () -> parse "{}")
        Assert.Equal("state", missingFormat.ParamName)
        Assert.Contains("formatVersion", missingFormat.Message)

        let wrongKind =
            Assert.Throws<ArgumentException>(fun () -> parse "{\"formatVersion\":\"one\"}")

        Assert.Equal("state", wrongKind.ParamName)
        Assert.Contains("wrong JSON kind", wrongKind.Message)

        let unsupported =
            Assert.Throws<ArgumentOutOfRangeException>(fun () -> parse "{\"formatVersion\":2}")

        Assert.Equal("state", unsupported.ParamName)
        Assert.Contains("Unsupported checkpoint format", unsupported.Message)

        let! run = Circuit.start Helpers.runtime (Circuit.value 1) () Helpers.options CancellationToken.None

        let nullResponse =
            Assert.Throws<ArgumentNullException>(fun () ->
                run.RespondAsync(Unchecked.defaultof<ApprovalResponse>, CancellationToken.None)
                |> ignore)

        Assert.Equal("response", nullResponse.ParamName)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        Assert.Throws<ObjectDisposedException>(fun () -> run.Events |> ignore) |> ignore

        Assert.Throws<ObjectDisposedException>(fun () -> run.CreateCheckpointAsync(CancellationToken.None) |> ignore)
        |> ignore

        Assert.Throws<ObjectDisposedException>(fun () ->
            run.RespondAsync(ApprovalResponse.Create("disposed", true), CancellationToken.None)
            |> ignore)
        |> ignore
    }

[<Fact>]
let ``runtime start rejects null circuit and options`` () =
    let runtime = Helpers.runtime
    let definition: Circuit<int, int> = Circuit.value 1

    let nullCircuit =
        Assert.Throws<ArgumentNullException>(fun () ->
            runtime.StartAsync(Unchecked.defaultof<Circuit<int, int>>, 0, Helpers.options, CancellationToken.None)
            |> ignore)

    Assert.Equal("circuit", nullCircuit.ParamName)

    let nullOptions =
        Assert.Throws<ArgumentNullException>(fun () ->
            runtime.StartAsync(definition, 0, Unchecked.defaultof<RunOptions>, CancellationToken.None)
            |> ignore)

    Assert.Equal("options", nullOptions.ParamName)

    let nullRuntime = Unchecked.defaultof<ICircuitRuntime>

    let nullStartRuntime =
        Assert.Throws<ArgumentNullException>(fun () ->
            Circuit.start nullRuntime definition 0 Helpers.options CancellationToken.None
            |> ignore)

    Assert.Equal("runtime", nullStartRuntime.ParamName)

    let nullResumeRuntime =
        Assert.Throws<ArgumentNullException>(fun () ->
            Circuit.resume
                nullRuntime
                definition
                Unchecked.defaultof<CircuitCheckpoint<int>>
                ResumeOptions.Default
                CancellationToken.None
            |> ignore)

    Assert.Equal("runtime", nullResumeRuntime.ParamName)

    let nullResumeOptions =
        Assert.Throws<ArgumentNullException>(fun () ->
            Circuit.resume
                runtime
                definition
                Unchecked.defaultof<CircuitCheckpoint<int>>
                Unchecked.defaultof<ResumeOptions>
                CancellationToken.None
            |> ignore)

    Assert.Equal("options", nullResumeOptions.ParamName)

[<Fact>]
let ``resume rejects null arguments and each exact definition identity mismatch`` () =
    task {
        let runtime = Helpers.runtime

        let definition: Circuit<int, int> =
            Circuit.define "resume-matrix" "1.0.0" (Circuit.value 7)

        let payload =
            SchedulerInternals.writeResumeState
                (SchedulerInternals.emptyResumeState ())
                typeof<int>
                (box 7)
                Helpers.options

        let valid =
            ResumeFixtures.checkpoint
                definition.Id
                definition.Version
                definition.Fingerprint
                (ValueSome Helpers.options)
                payload

        let! nullCircuit =
            Assert.ThrowsAsync<ArgumentNullException>(fun () ->
                runtime.ResumeAsync(
                    Unchecked.defaultof<Circuit<int, int>>,
                    valid,
                    ResumeOptions.Default,
                    CancellationToken.None
                )
                :> Task)

        Assert.Equal("circuit", nullCircuit.ParamName)

        let! nullCheckpoint =
            Assert.ThrowsAsync<ArgumentNullException>(fun () ->
                runtime.ResumeAsync(
                    definition,
                    Unchecked.defaultof<CircuitCheckpoint<int>>,
                    ResumeOptions.Default,
                    CancellationToken.None
                )
                :> Task)

        Assert.Equal("checkpoint", nullCheckpoint.ParamName)

        let! nullOptions =
            Assert.ThrowsAsync<ArgumentNullException>(fun () ->
                runtime.ResumeAsync(definition, valid, Unchecked.defaultof<ResumeOptions>, CancellationToken.None)
                :> Task)

        Assert.Equal("resumeOptions", nullOptions.ParamName)

        let mismatches =
            [| ResumeFixtures.checkpoint
                   (DefinitionId.Create("other-definition"))
                   definition.Version
                   definition.Fingerprint
                   (ValueSome Helpers.options)
                   payload
               ResumeFixtures.checkpoint
                   definition.Id
                   (SemanticVersion.Parse("2.0.0"))
                   definition.Fingerprint
                   (ValueSome Helpers.options)
                   payload
               ResumeFixtures.checkpoint
                   definition.Id
                   definition.Version
                   "different-fingerprint"
                   (ValueSome Helpers.options)
                   payload |]

        for checkpoint in mismatches do
            let! run = runtime.ResumeAsync(definition, checkpoint, ResumeOptions.Default, CancellationToken.None)
            let! failure = ResumeFixtures.terminalFailure run
            Assert.Equal(CircuitFailureCode.CheckpointMismatch, failure.Code)
            Assert.Contains("exact Circuit definition", failure.Message)
    }

[<Fact>]
let ``resume maps input and serialized session admission failures before leaf execution`` () =
    task {
        let definition: Circuit<int, int> =
            Circuit.define "resume-state-matrix" "1.0.0" (Circuit.value 7)

        let validPayload =
            SchedulerInternals.writeResumeState
                (SchedulerInternals.emptyResumeState ())
                typeof<int>
                (box 7)
                Helpers.options

        let wrongInputRoot = JsonNode.Parse(validPayload.GetRawText()).AsObject()
        wrongInputRoot["inputType"] <- JsonValue.Create(typeof<string>.AssemblyQualifiedName)
        use wrongInputDocument = JsonDocument.Parse(wrongInputRoot.ToJsonString())

        let wrongInput =
            ResumeFixtures.checkpoint
                definition.Id
                definition.Version
                definition.Fingerprint
                ValueNone
                wrongInputDocument.RootElement

        let! wrongInputRun =
            Helpers.runtime.ResumeAsync(definition, wrongInput, ResumeOptions.Default, CancellationToken.None)

        let! wrongInputFailure = ResumeFixtures.terminalFailure wrongInputRun
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, wrongInputFailure.Code)
        Assert.Contains("malformed", wrongInputFailure.Message)
        Assert.Contains("input type", string wrongInputFailure.Exception.Value)

        let unknownSessionState = SchedulerInternals.emptyResumeState ()
        use adapterDocument = JsonDocument.Parse("{}")

        unknownSessionState.SerializedSessions["unknown-agent|lane"] <-
            struct ("unknown.agent@1.0.0", adapterDocument.RootElement.Clone())

        let unknownSessionPayload =
            SchedulerInternals.writeResumeState unknownSessionState typeof<int> (box 7) Helpers.options

        let unknownSession =
            ResumeFixtures.checkpoint
                definition.Id
                definition.Version
                definition.Fingerprint
                (ValueSome Helpers.options)
                unknownSessionPayload

        let! unknownSessionRun =
            Helpers.runtime.ResumeAsync(definition, unknownSession, ResumeOptions.Default, CancellationToken.None)

        let! unknownSessionFailure = ResumeFixtures.terminalFailure unknownSessionRun
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, unknownSessionFailure.Code)
        Assert.Contains("does not match an agent node", string unknownSessionFailure.Exception.Value)

        let agent =
            AgentDefinition.Create(
                "resume.session.agent",
                "1.0.0",
                "Resume agent",
                "Resume session admission test.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<int, int>
                .Create(
                    "resume.session.signature",
                    "1.0.0",
                    "Resume",
                    "Echo",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let agentCircuit = Circuit.agent agent signature
        let agentPath = agentCircuit.Id.Value + "/" + agentCircuit.Id.Value + "|lane"
        let wrongAgentState = SchedulerInternals.emptyResumeState ()

        wrongAgentState.SerializedSessions[agentPath] <-
            struct ("different.agent@1.0.0", adapterDocument.RootElement.Clone())

        let wrongAgentPayload =
            SchedulerInternals.writeResumeState wrongAgentState typeof<int> (box 7) Helpers.options

        let wrongAgent =
            ResumeFixtures.checkpoint
                agentCircuit.Id
                agentCircuit.Version
                agentCircuit.Fingerprint
                (ValueSome Helpers.options)
                wrongAgentPayload

        let! wrongAgentRun =
            Helpers.runtime.ResumeAsync(agentCircuit, wrongAgent, ResumeOptions.Default, CancellationToken.None)

        let! wrongAgentFailure = ResumeFixtures.terminalFailure wrongAgentRun
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, wrongAgentFailure.Code)
        Assert.Contains("different agent definition", string wrongAgentFailure.Exception.Value)

        let session =
            CircuitSession(
                "admission-session",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let restoredSelection =
            SchedulerInternals.selectEffectiveSession (ValueSome session) ValueNone

        let configuredSelection =
            SchedulerInternals.selectEffectiveSession ValueNone (ValueSome session)

        Assert.Same(session, restoredSelection.Value)
        Assert.Same(session, configuredSelection.Value)
        Assert.True((SchedulerInternals.selectEffectiveSession ValueNone ValueNone).IsNone)

        match SchedulerInternals.planSessionAgentAdmission None "agent@1.0.0" with
        | SchedulerInternals.RegisterSessionAgent -> ()
        | plan -> failwith $"Expected agent registration, got {plan}."

        match SchedulerInternals.planSessionAgentAdmission (Some "agent@1.0.0") "agent@1.0.0" with
        | SchedulerInternals.ReuseSessionAgent -> ()
        | plan -> failwith $"Expected agent reuse, got {plan}."

        match SchedulerInternals.planSessionAgentAdmission (Some "other@1.0.0") "agent@1.0.0" with
        | SchedulerInternals.RejectSessionAgentSharing existing -> Assert.Equal("other@1.0.0", existing)
        | plan -> failwith $"Expected cross-agent rejection, got {plan}."

        match SchedulerInternals.planSerializedSessionAdmission None "agent@1.0.0" with
        | SchedulerInternals.RejectUnknownSerializedSession -> ()
        | plan -> failwith $"Expected unknown-session rejection, got {plan}."

        match SchedulerInternals.planSerializedSessionAdmission (Some agent) "different@1.0.0" with
        | SchedulerInternals.RejectSerializedSessionOwner actual -> Assert.Equal("resume.session.agent@1.0.0", actual)
        | plan -> failwith $"Expected session-owner rejection, got {plan}."

        match SchedulerInternals.planSerializedSessionAdmission (Some agent) "resume.session.agent@1.0.0" with
        | SchedulerInternals.DeserializeSerializedSession actual -> Assert.Same(agent, actual)
        | plan -> failwith $"Expected session deserialization, got {plan}."

        match SchedulerInternals.planSessionAliasAdmission (Some session) (Some agent) with
        | SchedulerInternals.BindSessionAlias(actualSession, actualAgent) ->
            Assert.Same(session, actualSession)
            Assert.Same(agent, actualAgent)
        | plan -> failwith $"Expected session alias binding, got {plan}."

        match SchedulerInternals.planSessionAliasAdmission None (Some agent) with
        | SchedulerInternals.RejectSessionAlias -> ()
        | plan -> failwith $"Expected missing-session alias rejection, got {plan}."

        match SchedulerInternals.planSessionAliasAdmission (Some session) None with
        | SchedulerInternals.RejectSessionAlias -> ()
        | plan -> failwith $"Expected missing-agent alias rejection, got {plan}."

        let nullSessionState = SchedulerInternals.emptyResumeState ()

        nullSessionState.SerializedSessions[agentPath] <-
            struct ("resume.session.agent@1.0.0", adapterDocument.RootElement.Clone())

        nullSessionState.SessionAliases[agentPath] <- agentPath

        let nullSessionPayload =
            SchedulerInternals.writeResumeState nullSessionState typeof<int> (box 7) Helpers.options

        let nullSessionCheckpoint =
            ResumeFixtures.checkpoint
                agentCircuit.Id
                agentCircuit.Version
                agentCircuit.Fingerprint
                (ValueSome Helpers.options)
                nullSessionPayload

        let nullSessionRuntime = AliasedSessionRuntime(false, restoreNullSession = true)

        let! nullSessionRun =
            (nullSessionRuntime :> ICircuitRuntime)
                .ResumeAsync(agentCircuit, nullSessionCheckpoint, ResumeOptions.Default, CancellationToken.None)

        let! nullSessionFailure = ResumeFixtures.terminalFailure nullSessionRun
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, nullSessionFailure.Code)
        Assert.Contains("restored to null", string nullSessionFailure.Exception.Value)
        Assert.Equal(0, nullSessionRuntime.Executions)
    }

[<Fact>]
let ``branch selection helper classifies exact default missing and selector exceptions`` () =
    let cases = Dictionary<string, Circuit<string, string>>(StringComparer.Ordinal)
    cases["exact"] <- Circuit.value "matched"

    let handler (definition: Circuit<string, string>) =
        match definition.Node with
        | CircuitGraph.Branch(_, _, value) -> value
        | _ -> failwith "Expected branch node."

    let withDefault =
        Circuit.branch "selection-default" "1.0.0" (fun value -> value) cases (ValueSome(Circuit.value "default"))

    match SchedulerInternals.selectBranch (handler withDefault) (box "exact") with
    | SchedulerInternals.ExactBranch(key, _) -> Assert.Equal("exact", key)
    | decision -> failwith $"Expected exact branch, got {decision}."

    match SchedulerInternals.selectBranch (handler withDefault) (box "other") with
    | SchedulerInternals.DefaultBranch _ -> ()
    | decision -> failwith $"Expected default branch, got {decision}."

    let withoutDefault = Circuit.branch "selection-missing" "1.0.0" id cases ValueNone

    match SchedulerInternals.selectBranch (handler withoutDefault) (box "missing") with
    | SchedulerInternals.MissingBranch key -> Assert.Equal("missing", key)
    | decision -> failwith $"Expected missing branch, got {decision}."

    let throwing =
        Circuit.branch
            "selection-throws"
            "1.0.0"
            (fun (_: string) -> raise (InvalidOperationException("selection failed")))
            cases
            ValueNone

    match SchedulerInternals.selectBranch (handler throwing) (box "input") with
    | SchedulerInternals.BranchSelectionFailed error -> Assert.Equal("selection failed", error.Message)
    | decision -> failwith $"Expected selector failure, got {decision}."

    let runId = RunId.New()
    let child = (Circuit.value "planned").Node

    match SchedulerInternals.planBranchExecution runId "root/branch" (SchedulerInternals.ExactBranch("key", child)) with
    | SchedulerInternals.EvaluateBranch(_, path) -> Assert.Equal("root/branch[key]", path)
    | plan -> failwith $"Expected exact evaluation plan, got {plan}."

    match SchedulerInternals.planBranchExecution runId "root/branch" (SchedulerInternals.DefaultBranch child) with
    | SchedulerInternals.EvaluateBranch(_, path) -> Assert.Equal("root/branch[default]", path)
    | plan -> failwith $"Expected default evaluation plan, got {plan}."

    match SchedulerInternals.planBranchExecution runId "root/branch" (SchedulerInternals.MissingBranch "absent") with
    | SchedulerInternals.EmitBranchFailure failure ->
        Assert.Equal(CircuitFailureCode.Engine, failure.Code)
        Assert.Equal(ValueSome "root/branch", failure.OperationId)
        Assert.Contains("absent", failure.Message)
    | plan -> failwith $"Expected missing failure plan, got {plan}."

    let selectionError = InvalidOperationException("selection failed")

    match
        SchedulerInternals.planBranchExecution
            runId
            "root/branch"
            (SchedulerInternals.BranchSelectionFailed selectionError)
    with
    | SchedulerInternals.EmitBranchFailure failure ->
        Assert.Equal(CircuitFailureCode.Engine, failure.Code)
        Assert.Same(selectionError, failure.Exception.Value)
        Assert.Contains("selector failed", failure.Message, StringComparison.OrdinalIgnoreCase)
    | plan -> failwith $"Expected selector failure plan, got {plan}."

[<Fact>]
let ``typed handler response erasure preserves succeeded values and controlled failures`` () =
    let runId = RunId.New()
    let metadata = ProjectionFixtures.metadata runId "erasure"
    let succeeded = Response<int>.Create(Succeeded 42, metadata)

    match SchedulerInternals.eraseHandlerResponse succeeded with
    | SchedulerInternals.HandlerSucceeded value -> Assert.Equal(42, unbox<int> value)
    | SchedulerInternals.HandlerFailed failure -> failwith failure.Message

    let expected = CircuitFailure.Create(CircuitFailureCode.Validation, "controlled")
    let failed = Response<int>.Create(Failed expected, metadata)

    match SchedulerInternals.eraseHandlerResponse failed with
    | SchedulerInternals.HandlerFailed actual -> Assert.Same(expected, actual)
    | SchedulerInternals.HandlerSucceeded value -> failwith $"Expected controlled failure, got {value}."

    let successfulErased: SchedulerInternals.ErasedResponse =
        { Value = box 42
          Failure = ValueNone
          Metadata = metadata
          Typed = succeeded }

    match SchedulerInternals.planRecovery successfulErased with
    | SchedulerInternals.PassThroughRecovery actual -> Assert.Same(successfulErased, actual)
    | plan -> failwith $"Expected recovery pass-through, got {plan}."

    let failedErased: SchedulerInternals.ErasedResponse =
        { Value = null
          Failure = ValueSome expected
          Metadata = metadata
          Typed = failed }

    match SchedulerInternals.planRecovery failedErased with
    | SchedulerInternals.InvokeRecovery actual -> Assert.Same(expected, actual)
    | plan -> failwith $"Expected recovery invocation, got {plan}."

    match SchedulerInternals.invokeRecoveryHandler (fun (_: CircuitFailure) -> box 7) expected with
    | SchedulerInternals.HandlerExecutionSucceeded(value, failure) ->
        Assert.Equal(7, unbox<int> value)
        Assert.Equal(ValueNone, failure)
    | result -> failwith $"Expected recovery success, got {result}."

    let recoveryError = InvalidOperationException("recovery failed")

    match SchedulerInternals.invokeRecoveryHandler (fun (_: CircuitFailure) -> raise recoveryError) expected with
    | SchedulerInternals.HandlerExecutionFailed error -> Assert.Same(recoveryError, error)
    | result -> failwith $"Expected recovery failure, got {result}."

[<Fact>]
let ``code and aggregate handlers preserve asynchronous success failure null fault and cancellation outcomes`` () =
    task {
        let asyncSuccess =
            Circuit.code "async-code-success" "1.0.0" (fun context value ->
                task {
                    do! Task.Yield()
                    return Response.succeed context (value + 1)
                })

        let! succeeded = Circuit.run Helpers.runtime asyncSuccess 4 Helpers.options CancellationToken.None
        Assert.True(succeeded.IsSuccess)
        Assert.Equal(5, succeeded.Value)

        let controlled =
            CircuitFailure.Create(CircuitFailureCode.Validation, "async rejection")

        let asyncFailure: Circuit<int, int> =
            Circuit.code "async-code-failure" "1.0.0" (fun context _ ->
                task {
                    do! Task.Yield()
                    return Response.fail context controlled
                })

        let! failed = Circuit.run Helpers.runtime asyncFailure 0 Helpers.options CancellationToken.None
        Assert.False(failed.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, failed.Failure.Code)

        let nullCode: Circuit<int, int> =
            Circuit.code "null-code-response" "1.0.0" (fun _ _ -> Task.FromResult(Unchecked.defaultof<Response<int>>))

        let! nullResponse = Circuit.run Helpers.runtime nullCode 0 Helpers.options CancellationToken.None
        Assert.False(nullResponse.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, nullResponse.Failure.Code)
        Assert.Contains("returned null", string nullResponse.Failure.Exception.Value)

        let synchronousThrow: Circuit<int, int> =
            Circuit.code "sync-code-throw" "1.0.0" (fun _ _ ->
                raise (InvalidOperationException("synchronous code failure")))

        let! synchronousFailure = Circuit.run Helpers.runtime synchronousThrow 0 Helpers.options CancellationToken.None

        Assert.False(synchronousFailure.IsSuccess)
        Assert.Contains("synchronous code failure", string synchronousFailure.Failure.Exception.Value)

        let faultedCode: Circuit<int, int> =
            Circuit.code "faulted-code" "1.0.0" (fun _ _ ->
                Task.FromException<Response<int>>(InvalidOperationException("faulted code failure")))

        let! faulted = Circuit.run Helpers.runtime faultedCode 0 Helpers.options CancellationToken.None
        Assert.False(faulted.IsSuccess)
        Assert.Contains("faulted code failure", string faulted.Failure.Exception.Value)

        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        use cancellation = new CancellationTokenSource()

        let cancelledCode: Circuit<int, int> =
            Circuit.code "cancelled-code" "1.0.0" (fun context _ ->
                task {
                    entered.TrySetResult(()) |> ignore
                    do! Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                    return Response.succeed context 1
                })

        let cancelledTask =
            Circuit.run Helpers.runtime cancelledCode 0 Helpers.options cancellation.Token

        do! entered.Task
        cancellation.Cancel()
        let! cancelled = cancelledTask
        Assert.False(cancelled.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelled.Failure.Code)

        let source =
            Circuit.items "async-aggregate-items" "1.0.0" (fun (_: unit) -> Helpers.toArray [ 2; 3 ])

        let asyncAggregate =
            source
            |> Circuit.aggregate "async-aggregate-success" "1.0.0" (fun context responses _ ->
                task {
                    do! Task.Yield()
                    return Response.succeed context (responses |> Seq.sumBy _.Value)
                })

        let! aggregateSuccess = Circuit.run Helpers.runtime asyncAggregate () Helpers.options CancellationToken.None

        Assert.True(aggregateSuccess.IsSuccess)
        Assert.Equal(5, aggregateSuccess.Value)

        let nullAggregate: Circuit<unit, int> =
            source
            |> Circuit.aggregate "null-aggregate-response" "1.0.0" (fun _ _ _ ->
                Task.FromResult(Unchecked.defaultof<Response<int>>))

        let! aggregateNull = Circuit.run Helpers.runtime nullAggregate () Helpers.options CancellationToken.None

        Assert.False(aggregateNull.IsSuccess)
        Assert.Equal(CircuitFailureCode.Engine, aggregateNull.Failure.Code)
        Assert.Contains("aggregate handler failed", aggregateNull.Failure.Message, StringComparison.OrdinalIgnoreCase)
    }

[<Fact>]
let ``completed checkpoint replays cached decisions across composite node kinds`` () =
    task {
        let mutable sourceCalls = 0
        let mutable codeCalls = 0
        let mutable recoveryCalls = 0
        let mutable aggregateCalls = 0
        let mutable selectorCalls = 0
        let mutable dynamicBuilds = 0

        let source id values =
            Circuit.items id "1.0.0" (fun (_: unit) ->
                Interlocked.Increment(&sourceCalls) |> ignore
                values |> List.toArray :> IReadOnlyList<int>)

        let successfulCode =
            Circuit.code "cached-code" "1.0.0" (fun context (_: unit) ->
                Interlocked.Increment(&codeCalls) |> ignore
                Helpers.success context 20)

        let controlledFailure =
            CircuitFailure.Create(CircuitFailureCode.Provider, "cached failure")

        let recovered =
            Circuit.code "cached-failure" "1.0.0" (fun context (_: unit) ->
                Interlocked.Increment(&codeCalls) |> ignore
                Response.fail context controlledFailure |> Task.FromResult)
            |> Circuit.recover "cached-recovery" "1.0.0" (fun failure ->
                Interlocked.Increment(&recoveryCalls) |> ignore
                int failure.Code)

        let aggregated =
            source "cached-aggregate-items" [ 3; 4 ]
            |> Circuit.aggregate "cached-aggregate" "1.0.0" (fun context responses _ ->
                Interlocked.Increment(&aggregateCalls) |> ignore
                responses |> Seq.sumBy _.Value |> Response.succeed context |> Task.FromResult)

        let cases = Dictionary<string, Circuit<unit, int>>(StringComparer.Ordinal)
        cases["selected"] <- Circuit.value 40

        let selected =
            Circuit.branch
                "cached-branch"
                "1.0.0"
                (fun () ->
                    Interlocked.Increment(&selectorCalls) |> ignore
                    "selected")
                cases
                ValueNone

        let dynamic =
            Circuit.code "cached-dynamic-input" "1.0.0" (fun context (_: unit) ->
                Interlocked.Increment(&codeCalls) |> ignore
                Helpers.success context 50)
            |> Circuit.thenDynamic "cached-dynamic" "1.0.0" string 1 (fun value ->
                Interlocked.Increment(&dynamicBuilds) |> ignore
                Circuit.value value)

        let branches =
            [| Circuit.value 10
               successfulCode
               recovered
               source "cached-items" [ 30; 31 ]
               aggregated
               selected
               dynamic |]
            :> IReadOnlyList<Circuit<unit, int>>

        let definition = Circuit.merge "cached-merge" "1.0.0" 3 branches
        let! run = Circuit.start Helpers.runtime definition () Helpers.options CancellationToken.None
        let! initialEvents = Helpers.collectEvents run

        let initialOutputs =
            initialEvents
            |> Array.choose (function
                | OutputProduced(_, response) when response.IsSuccess -> Some response.Value
                | _ -> None)
            |> Array.sort

        let expectedOutputs =
            [| 7; 10; 20; 30; 31; 40; 50; int CircuitFailureCode.Provider |] |> Array.sort

        Assert.Equal<int[]>(expectedOutputs, initialOutputs)

        let! saved = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        Assert.Equal(2, sourceCalls)
        Assert.Equal(3, codeCalls)
        Assert.Equal(1, recoveryCalls)
        Assert.Equal(1, aggregateCalls)
        Assert.Equal(1, selectorCalls)
        Assert.Equal(1, dynamicBuilds)

        let! resumed =
            Circuit.resume Helpers.runtime definition saved.Value ResumeOptions.Default CancellationToken.None

        let! resumedEvents = Helpers.collectEvents resumed

        let resumedOutputs =
            resumedEvents
            |> Array.choose (function
                | OutputProduced(_, response) when response.IsSuccess -> Some response.Value
                | _ -> None)
            |> Array.sort

        Assert.Equal<int[]>(initialOutputs, resumedOutputs)
        Assert.Equal(2, sourceCalls)
        Assert.Equal(3, codeCalls)
        Assert.Equal(1, recoveryCalls)
        Assert.Equal(1, aggregateCalls)
        Assert.Equal(2, selectorCalls)
        Assert.Equal(3, dynamicBuilds)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``completed checkpoint restores approval and loop terminal nodes without repeating work`` () =
    task {
        let mutable prompts = 0

        let approval =
            Circuit.approval "cached-approval" "1.0.0" (fun (_: unit) ->
                Interlocked.Increment(&prompts) |> ignore
                ApprovalPrompt.Create("Review", "Approve"))

        let! approvalRun = Circuit.start Helpers.runtime approval () Helpers.options CancellationToken.None
        let approvalEnumerator = approvalRun.Events.GetAsyncEnumerator()
        let mutable approvalOutput: ApprovalResponse option = None

        while approvalOutput.IsNone do
            let! more = approvalEnumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match approvalEnumerator.Current with
            | ApprovalRequested request ->
                let! accepted =
                    approvalRun
                        .RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None)
                        .AsTask()

                Assert.True(accepted.IsSuccess)
            | OutputProduced(_, response) when response.IsSuccess -> approvalOutput <- Some response.Value
            | _ -> ()

        Assert.True(approvalOutput.Value.Approved)
        let! approvalCheckpoint = approvalRun.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(approvalCheckpoint.IsSuccess)
        do! approvalEnumerator.DisposeAsync().AsTask()
        do! (approvalRun :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumedApproval =
            Circuit.resume
                Helpers.runtime
                approval
                approvalCheckpoint.Value
                ResumeOptions.Default
                CancellationToken.None

        let! resumedApprovalEvents = Helpers.collectEvents resumedApproval

        Assert.DoesNotContain(
            resumedApprovalEvents,
            fun event ->
                match event with
                | ApprovalRequested _ -> true
                | _ -> false
        )

        let replayedApproval =
            resumedApprovalEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.exactlyOne

        Assert.True(replayedApproval.Approved)
        Assert.Equal(1, prompts)
        do! (resumedApproval :> IAsyncDisposable).DisposeAsync().AsTask()

        let mutable increments = 0
        let mutable predicates = 0

        let increment =
            Circuit.code "cached-loop-increment" "1.0.0" (fun context value ->
                Interlocked.Increment(&increments) |> ignore
                Helpers.success context (value + 1))

        let loop =
            Circuit.loop
                "cached-loop"
                "1.0.0"
                5
                (fun value ->
                    Interlocked.Increment(&predicates) |> ignore
                    value < 2)
                increment

        let! loopRun = Circuit.start Helpers.runtime loop 0 Helpers.options CancellationToken.None
        let! loopEvents = Helpers.collectEvents loopRun

        let initialLoopOutput =
            loopEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.exactlyOne

        Assert.Equal(2, initialLoopOutput)
        let! loopCheckpoint = loopRun.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(loopCheckpoint.IsSuccess)
        do! (loopRun :> IAsyncDisposable).DisposeAsync().AsTask()
        Assert.Equal(2, increments)
        Assert.Equal(3, predicates)

        let! resumedLoop =
            Circuit.resume Helpers.runtime loop loopCheckpoint.Value ResumeOptions.Default CancellationToken.None

        let! resumedLoopEvents = Helpers.collectEvents resumedLoop

        let replayedLoopOutput =
            resumedLoopEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response.Value
                | _ -> None)
            |> Array.exactlyOne

        Assert.Equal(2, replayedLoopOutput)
        Assert.Equal(2, increments)
        Assert.Equal(3, predicates)
        do! (resumedLoop :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``failed loop checkpoint replays terminal when predicate changes`` () =
    task {
        let mutable predicates = 0
        let mutable bodies = 0

        let controlled =
            CircuitFailure.Create(CircuitFailureCode.Validation, "loop body rejected")

        let body =
            Circuit.code "cached-failed-loop-body" "1.0.0" (fun context (_: int) ->
                Interlocked.Increment(&bodies) |> ignore
                Response.fail context controlled |> Task.FromResult)

        let definition =
            Circuit.loop "cached-failed-loop" "1.0.0" 3 (fun _ -> Interlocked.Increment(&predicates) = 1) body

        let! first = Circuit.start Helpers.runtime definition 0 Helpers.options CancellationToken.None
        let! firstEvents = Helpers.collectEvents first

        let initial =
            firstEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.False(initial.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, initial.Failure.Code)
        Assert.Equal(1, predicates)
        Assert.Equal(1, bodies)

        let! saved = first.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        do! (first :> IAsyncDisposable).DisposeAsync().AsTask()

        let! resumed =
            Circuit.resume Helpers.runtime definition saved.Value ResumeOptions.Default CancellationToken.None

        let! resumedEvents = Helpers.collectEvents resumed

        let replayed =
            resumedEvents
            |> Array.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Array.exactlyOne

        Assert.False(replayed.IsSuccess)
        Assert.Equal(initial.Failure.Code, replayed.Failure.Code)
        Assert.Equal(initial.Failure.Message, replayed.Failure.Message)
        Assert.Equal(initial.Failure.RequestId, replayed.Failure.RequestId)
        Assert.Equal(initial.Metadata.NodePath, replayed.Metadata.NodePath)
        Assert.Equal(initial.Metadata.StartedAt, replayed.Metadata.StartedAt)
        Assert.Equal(initial.Metadata.CompletedAt, replayed.Metadata.CompletedAt)
        Assert.Equal(1, predicates)
        Assert.Equal(1, bodies)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``dynamic scheduler reports upstream duplicate depth node and handler failures`` () =
    task {
        let controlledFailure =
            CircuitFailure.Create(CircuitFailureCode.Provider, "upstream failed")

        let mutable upstreamFactories = 0

        let failedUpstream: Circuit<unit, int> =
            Circuit.code "dynamic-failed-upstream" "1.0.0" (fun context _ ->
                Response.fail context controlledFailure |> Task.FromResult)

        let propagated =
            failedUpstream
            |> Circuit.thenDynamic "dynamic-after-failure" "1.0.0" string 1 (fun value ->
                Interlocked.Increment(&upstreamFactories) |> ignore
                Circuit.value value)

        let! upstreamResult = Circuit.collect Helpers.runtime propagated () Helpers.options CancellationToken.None

        Assert.True(upstreamResult.IsSuccess)
        let propagatedFailure = Assert.Single(upstreamResult.Value)
        Assert.False(propagatedFailure.IsSuccess)
        Assert.Equal(CircuitFailureCode.Provider, propagatedFailure.Failure.Code)
        Assert.Equal(0, upstreamFactories)

        let duplicateInputs =
            [| Circuit.value 1; Circuit.value 1 |] :> IReadOnlyList<Circuit<unit, int>>

        let duplicate =
            Circuit.merge "duplicate-dynamic-inputs" "1.0.0" 2 duplicateInputs
            |> Circuit.thenDynamic "duplicate-dynamic" "1.0.0" (fun _ -> "same") 2 Circuit.value

        let! duplicateResult = Circuit.collect Helpers.runtime duplicate () Helpers.options CancellationToken.None

        Assert.False(duplicateResult.IsSuccess)
        Assert.Equal(CircuitFailureCode.GeneratedGraphIntegrity, duplicateResult.Failure.Code)
        Assert.Contains("dynamic Circuit", duplicateResult.Failure.Message, StringComparison.OrdinalIgnoreCase)

        let innerSeed =
            Circuit.code "inner-dynamic-seed" "1.0.0" (fun context value -> Helpers.success context value)

        let nested =
            Circuit.code "outer-dynamic-seed" "1.0.0" (fun context (_: unit) -> Helpers.success context 1)
            |> Circuit.thenDynamic "outer-dynamic" "1.0.0" string 1 (fun _ ->
                innerSeed
                |> Circuit.thenDynamic "inner-dynamic" "1.0.0" string 1 (fun value -> Circuit.value value))

        let depthOptions =
            Helpers.options.WithLimits(
                1,
                Helpers.options.MaxDynamicNodes,
                Helpers.options.MaxApprovalRounds,
                Helpers.options.MaxSourcePageSize,
                Helpers.options.MaxSourcePages
            )

        let! depthResult = Circuit.collect Helpers.runtime nested () depthOptions CancellationToken.None

        Assert.False(depthResult.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, depthResult.Failure.Code)
        Assert.Contains("depth limit", depthResult.Failure.Message, StringComparison.OrdinalIgnoreCase)

        let generatedBranches =
            [| Circuit.value 1; Circuit.value 2 |] :> IReadOnlyList<Circuit<int, int>>

        let tooManyNodes =
            Circuit.code "node-limit-seed" "1.0.0" (fun context (_: unit) -> Helpers.success context 1)
            |> Circuit.thenDynamic "node-limit-dynamic" "1.0.0" string 1 (fun _ ->
                Circuit.merge "generated-node-limit-merge" "1.0.0" 1 generatedBranches)

        let nodeOptions =
            Helpers.options.WithLimits(
                Helpers.options.MaxDynamicDepth,
                1,
                Helpers.options.MaxApprovalRounds,
                Helpers.options.MaxSourcePageSize,
                Helpers.options.MaxSourcePages
            )

        let! nodeResult = Circuit.collect Helpers.runtime tooManyNodes () nodeOptions CancellationToken.None

        Assert.False(nodeResult.IsSuccess)
        Assert.Equal(CircuitFailureCode.ResourceLimit, nodeResult.Failure.Code)
        Assert.Contains("generated-node limit", nodeResult.Failure.Message, StringComparison.OrdinalIgnoreCase)

        let keyFailure =
            Circuit.code "key-failure-seed" "1.0.0" (fun context (_: unit) -> Helpers.success context 1)
            |> Circuit.thenDynamic
                "key-failure-dynamic"
                "1.0.0"
                (fun _ -> raise (InvalidOperationException("key failed")))
                1
                Circuit.value

        let! keyResult = Circuit.collect Helpers.runtime keyFailure () Helpers.options CancellationToken.None

        Assert.False(keyResult.IsSuccess)
        Assert.Equal(CircuitFailureCode.GeneratedGraphIntegrity, keyResult.Failure.Code)
        Assert.Contains("key failed", string keyResult.Failure.Exception.Value)

        let factoryFailure =
            Circuit.code "factory-failure-seed" "1.0.0" (fun context (_: unit) -> Helpers.success context 1)
            |> Circuit.thenDynamic "factory-failure-dynamic" "1.0.0" string 1 (fun _ ->
                raise (InvalidOperationException("factory failed")))

        let! factoryResult = Circuit.collect Helpers.runtime factoryFailure () Helpers.options CancellationToken.None

        Assert.False(factoryResult.IsSuccess)
        Assert.Equal(CircuitFailureCode.GeneratedGraphIntegrity, factoryResult.Failure.Code)
        Assert.Contains("factory failed", string factoryResult.Failure.Exception.Value)
    }
