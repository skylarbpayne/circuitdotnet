namespace Circuit.FSharp.Tests

open System
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Xunit

type private NoopRuntime() =
    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _ct: CancellationToken
            ) =
            raise (NotSupportedException())

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())

        member _.SerializeSessionAsync(_agent, _session, _ct) = raise (NotSupportedException())
        member _.DeserializeSessionAsync(_agent, _state, _ct) = raise (NotSupportedException())

type private FailureRuntime(childRunId: RunId, failure: CircuitFailure) =
    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _ct: CancellationToken
            ) =
            Task.FromResult(
                RunResult(
                    childRunId,
                    CircuitResult<'Output>.Error failure,
                    RunUsage(0, 0),
                    ValueNone,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
            )

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())

        member _.SerializeSessionAsync(_agent, _session, _ct) = raise (NotSupportedException())
        member _.DeserializeSessionAsync(_agent, _state, _ct) = raise (NotSupportedException())

module CircuitProgramTests =
    [<Fact>]
    let ``bind short-circuits on circuit failure`` () =
        let mutable binderCalled = false
        let runtime = NoopRuntime() :> ICircuitRuntime

        let failure =
            CircuitFailure(CircuitFailureCode.Workflow, "boom", ValueNone, ValueNone, ValueNone, ValueNone)

        let program =
            circuit.Bind(
                Circuit.fail failure,
                fun (_: int) ->
                    binderCalled <- true
                    circuit.Return 42
            )

        let result =
            Circuit.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.False(binderCalled)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.Equal(result.RunId, result.Result.Failure.RunId.Value)

    [<Fact>]
    let ``parallel preserves declaration order and respects max concurrency`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable active = 0
        let mutable maxActive = 0
        let gate = obj ()

        let work (delayMs: int) value =
            Circuit.code $"code-{value}" (fun ct ->
                task {
                    let activeNow = Interlocked.Increment &active

                    lock gate (fun () -> maxActive <- max maxActive activeNow)

                    try
                        do! Task.Delay(delayMs, ct)
                        return value
                    finally
                        Interlocked.Decrement &active |> ignore
                })

        let program = Circuit.``parallel`` 2 [ work 80 1; work 10 2; work 0 3 ]

        let result =
            Circuit.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal<int list>([ 1; 2; 3 ], result.Result.Value)
        Assert.True(maxActive <= 2, $"Expected max concurrency <= 2, got {maxActive}.")
        Assert.True(maxActive >= 2, "Expected at least two concurrent operations.")

    [<Fact>]
    let ``parallel cancels siblings after first failure`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let siblingCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let failure =
            CircuitFailure(CircuitFailureCode.Workflow, "broken", ValueNone, ValueNone, ValueNone, ValueNone)

        let waitingProgram =
            Circuit.code "waiting" (fun ct ->
                task {
                    try
                        do! Task.Delay(Timeout.Infinite, ct)
                        return 1
                    with :? OperationCanceledException ->
                        siblingCancelled.TrySetResult() |> ignore
                        return raise (OperationCanceledException(ct))
                })

        let failingProgram = Circuit.fail failure
        let program = Circuit.``parallel`` 2 [ waitingProgram; failingProgram ]

        let result =
            Circuit.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.True(siblingCancelled.Task.Wait(1000), "Expected the sibling operation to observe cancellation.")

    [<Fact>]
    let ``parallel returns a cancelled result when cancellation is requested before start`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable started = false

        let program =
            Circuit.``parallel``
                2
                [ Circuit.code "never.one" (fun _ ->
                      started <- true
                      Task.FromResult 1)
                  Circuit.code "never.two" (fun _ ->
                      started <- true
                      Task.FromResult 2) ]

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let result = Circuit.run runtime RunOptions.Default cts.Token program |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.False(started)

    [<Fact>]
    let ``parallel returns a cancelled result when cancellation arrives while waiting for the semaphore`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let firstCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let firstStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable secondStarted = false

        let first =
            Circuit.code "first" (fun ct ->
                task {
                    firstStarted.TrySetResult() |> ignore

                    try
                        do! Task.Delay(Timeout.Infinite, ct)
                        return 1
                    with :? OperationCanceledException ->
                        firstCancelled.TrySetResult() |> ignore
                        return raise (OperationCanceledException(ct))
                })

        let second =
            Circuit.code "second" (fun _ ->
                secondStarted <- true
                Task.FromResult 2)

        use cts = new CancellationTokenSource()

        let runTask =
            Circuit.run runtime RunOptions.Default cts.Token (Circuit.``parallel`` 1 [ first; second ])

        Assert.True(firstStarted.Task.Wait(1000), "Expected the first child to start before cancellation.")
        cts.Cancel()

        let result = runTask.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.True(firstCancelled.Task.Wait(1000), "Expected the running child to observe cancellation.")
        Assert.False(secondStarted)

    [<Fact>]
    let ``parallel returns a cancelled result after started children race to observe cancellation`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let allStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let allCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable started = 0
        let mutable cancelled = 0

        let makeChild name value =
            Circuit.code name (fun ct ->
                task {
                    if Interlocked.Increment(&started) = 2 then
                        allStarted.TrySetResult() |> ignore

                    try
                        do! Task.Delay(Timeout.Infinite, ct)
                        return value
                    with :? OperationCanceledException ->
                        if Interlocked.Increment(&cancelled) = 2 then
                            allCancelled.TrySetResult() |> ignore

                        return raise (OperationCanceledException(ct))
                })

        use cts = new CancellationTokenSource()

        let runTask =
            Circuit.run
                runtime
                RunOptions.Default
                cts.Token
                (Circuit.``parallel`` 2 [ makeChild "child.one" 1; makeChild "child.two" 2 ])

        Assert.True(allStarted.Task.Wait(1000), "Expected both children to start before cancellation.")
        cts.Cancel()

        let result = runTask.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.True(allCancelled.Task.Wait(1000), "Expected started children to drain after cancellation.")

    [<Fact>]
    let ``try finally runs compensation on failure`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable compensated = false

        let failure =
            CircuitFailure(CircuitFailureCode.Workflow, "boom", ValueNone, ValueNone, ValueNone, ValueNone)

        let program =
            circuit.TryFinally(Circuit.fail failure, fun () -> compensated <- true)

        let result =
            Circuit.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.True(compensated)

    [<Fact>]
    let ``call failure is rewritten to the shared root run correlation`` () =
        let childRunId = RunId.New()

        let childFailure =
            CircuitFailure(
                CircuitFailureCode.Provider,
                "provider failed",
                ValueSome childRunId,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let runtime = FailureRuntime(childRunId, childFailure) :> ICircuitRuntime

        let agent = AgentDefinition.create "agent.test" "1.0.0" "Agent" "Do the thing"

        let signature =
            Signature.create<int, int> "signature.test" "1.0.0" "Description" "Instructions"

        let program = Circuit.call agent signature 7

        let result =
            Circuit.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(result.RunId, result.Result.Failure.RunId.Value)
        Assert.NotEqual(childRunId, result.Result.Failure.RunId.Value)
        Assert.True(result.Result.Failure.OperationId.IsSome)
