module Circuit.Core.Tests.SchedulerTests

open System
open System.Collections.Generic
open System.Text.Json
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

type private AliasedSessionRuntime(blockExecution: bool, ?throwExecutor: bool, ?throwCodec: bool) =
    inherit CircuitRuntime()

    let started =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable active = 0
    let mutable maximum = 0
    let throwExecutor = defaultArg throwExecutor false
    let throwCodec = defaultArg throwCodec false

    member _.Started = started.Task
    member _.Release() = release.TrySetResult(()) |> ignore
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
            _onSession,
            cancellationToken
        ) =
        task {
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
        ValueTask<CircuitSession>(
            CircuitSession(
                state.GetProperty("id").GetString(),
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueSome "test",
                ValueSome "definition",
                ValueSome(box "restored")
            )
        )

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
