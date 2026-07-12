namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
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

type private DelegateRuntime
    (
        runAsync: AgentDefinition -> obj -> obj -> RunOptions -> CancellationToken -> obj,
        ?serializeSessionAsync: AgentDefinition -> CircuitSession -> CancellationToken -> JsonElement,
        ?deserializeSessionAsync: AgentDefinition -> JsonElement -> CancellationToken -> CircuitSession
    ) =
    let serializeSessionAsync =
        defaultArg serializeSessionAsync (fun _agent session _ct ->
            failwithf "Unexpected serialize request for %s" session.Id)

    let deserializeSessionAsync =
        defaultArg deserializeSessionAsync (fun _agent _state _ct -> failwith "Unexpected deserialize request")

    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                agent: AgentDefinition,
                signature: Signature<'Input, 'Output>,
                input: 'Input,
                options: RunOptions,
                ct: CancellationToken
            ) =
            Task.FromResult(runAsync agent (box signature) (box input) options ct :?> RunResult<'Output>)

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())

        member _.SerializeSessionAsync(agent, session, ct) =
            ValueTask<JsonElement>(serializeSessionAsync agent session ct)

        member _.DeserializeSessionAsync(agent, state, ct) =
            ValueTask<CircuitSession>(deserializeSessionAsync agent state ct)

type private FaultingRuntime(message: string) =
    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _ct: CancellationToken
            ) =
            Task.FromException<RunResult<'Output>>(InvalidOperationException(message))

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())
        member _.SerializeSessionAsync(_agent, _session, _ct) = raise (NotSupportedException())
        member _.DeserializeSessionAsync(_agent, _state, _ct) = raise (NotSupportedException())

type private CancelledRuntime(token: CancellationToken) =
    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _ct: CancellationToken
            ) =
            Task.FromCanceled<RunResult<'Output>>(token)

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())
        member _.SerializeSessionAsync(_agent, _session, _ct) = raise (NotSupportedException())
        member _.DeserializeSessionAsync(_agent, _state, _ct) = raise (NotSupportedException())

module ProgramsTests =
    let private createFailure code message runId operationId requestId innerException =
        CircuitFailure(code, message, runId, operationId, requestId, innerException)

    let private createAgent () =
        AgentDefinition.Create(
            "agent.test",
            "1.0.0",
            "Agent",
            "Do the thing.",
            ValueNone,
            Seq.empty,
            Seq.empty,
            Seq.empty
        )

    let private createSignature () =
        Signature<int, int>
            .Create(
                "signature.test",
                "1.0.0",
                "Signature",
                "Return an integer.",
                CircuitJson.createOptions (),
                Seq.empty,
                Seq.empty
            )

    let private createSession id =
        CircuitSession(
            id,
            Dictionary<string, string>(seq { KeyValuePair("scope", "test") }, StringComparer.Ordinal)
            :> IReadOnlyDictionary<string, string>,
            ValueNone,
            ValueNone,
            ValueNone
        )

    let private createResult<'T> runId usage session (result: CircuitResult<'T>) : RunResult<'T> =
        RunResult(runId, result, usage, session, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)

    let private faultedProgram<'T> message : CircuitPrograms.ProgramExpr<'T> =
        fun _ -> Task.FromException<CircuitPrograms.ProgramOutcome<'T>>(InvalidOperationException(message))

    let private cancelledProgram<'T> (token: CancellationToken) : CircuitPrograms.ProgramExpr<'T> =
        fun _ -> Task.FromCanceled<CircuitPrograms.ProgramOutcome<'T>>(token)

    [<Fact>]
    let ``bind short circuits on circuit failure`` () =
        let mutable binderCalled = false
        let runtime = NoopRuntime() :> ICircuitRuntime

        let failure =
            createFailure CircuitFailureCode.Workflow "boom" ValueNone ValueNone ValueNone ValueNone

        let program =
            CircuitPrograms.bind (CircuitPrograms.fail failure) (fun (_: int) ->
                binderCalled <- true
                CircuitPrograms.succeed 42)

        let result =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.False(binderCalled)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.Equal(result.RunId, result.Result.Failure.RunId.Value)

    [<Fact>]
    let ``delay and trywith can recover from thrown exceptions`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let program =
            CircuitPrograms.tryWith
                (CircuitPrograms.delay (fun () -> raise (InvalidOperationException("boom"))))
                (fun ex -> CircuitPrograms.succeed ex.Message)

        let result =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("boom", result.Result.Value)

    [<Fact>]
    let ``try finally and using dispose resources on both success and failure paths`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable compensationCount = 0
        let mutable disposed = false

        let resource =
            { new IDisposable with
                member _.Dispose() = disposed <- true }

        let failing =
            CircuitPrograms.tryFinally
                (CircuitPrograms.fail (
                    createFailure CircuitFailureCode.Workflow "boom" ValueNone ValueNone ValueNone ValueNone
                ))
                (fun () -> compensationCount <- compensationCount + 1)

        let successful =
            CircuitPrograms.using resource (fun _ ->
                CircuitPrograms.tryFinally (CircuitPrograms.succeed 7) (fun () ->
                    compensationCount <- compensationCount + 1))

        let failedResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None failing
            |> _.Result

        let successResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None successful
            |> _.Result

        Assert.False(failedResult.Result.IsSuccess)
        Assert.True(successResult.Result.IsSuccess)
        Assert.Equal(7, successResult.Result.Value)
        Assert.Equal(2, compensationCount)
        Assert.True(disposed)

    [<Fact>]
    let ``code wraps thrown exceptions as workflow failures with operation ids`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let program =
            CircuitPrograms.code "explode" (fun _ -> raise (InvalidOperationException("explode")))

        let result =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.Equal(result.RunId, result.Result.Failure.RunId.Value)
        Assert.True(result.Result.Failure.OperationId.IsSome)
        Assert.Equal("Circuit code step 'explode' failed.", result.Result.Failure.Message)
        Assert.IsType<InvalidOperationException>(result.Result.Failure.Exception.Value)

    [<Fact>]
    let ``call returns cancelled without invoking the runtime when the root token is already cancelled`` () =
        let mutable invoked = false

        let runtime =
            DelegateRuntime(fun _agent _signature _input _options _ct ->
                invoked <- true

                let result: RunResult<int> =
                    createResult (RunId.New()) (RunUsage(0, 0)) ValueNone (CircuitResult<int>.Success 42)

                box result)
            :> ICircuitRuntime

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                cts.Token
                (CircuitPrograms.call (createAgent ()) (createSignature ()) 7)
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.False(invoked)

    [<Fact>]
    let ``call failure is rewritten to the shared root run correlation`` () =
        let childRunId = RunId.New()

        let childFailure =
            createFailure
                CircuitFailureCode.Provider
                "provider failed"
                (ValueSome childRunId)
                ValueNone
                ValueNone
                ValueNone

        let runtime =
            DelegateRuntime(fun _agent _signature _input _options _ct ->
                let result: RunResult<int> =
                    createResult childRunId (RunUsage(0, 0)) ValueNone (CircuitResult<int>.Error childFailure)

                box result)
            :> ICircuitRuntime

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.call (createAgent ()) (createSignature ()) 7)
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(result.RunId, result.Result.Failure.RunId.Value)
        Assert.NotEqual(childRunId, result.Result.Failure.RunId.Value)
        Assert.True(result.Result.Failure.OperationId.IsSome)

    [<Fact>]
    let ``call aggregates usage and preserves a shared session across successful calls`` () =
        let mutable callIndex = 0
        let session = createSession "session-1"

        let runtime =
            DelegateRuntime(fun _agent _signature input _options _ct ->
                callIndex <- callIndex + 1

                let result: RunResult<int> =
                    createResult
                        (RunId.New())
                        (RunUsage(callIndex, callIndex + 1))
                        (ValueSome session)
                        (CircuitResult<int>.Success(unbox<int> input + callIndex))

                box result)
            :> ICircuitRuntime

        let first = CircuitPrograms.call (createAgent ()) (createSignature ()) 2
        let second = CircuitPrograms.call (createAgent ()) (createSignature ()) 3

        let program =
            CircuitPrograms.bind first (fun firstValue ->
                CircuitPrograms.bind second (fun secondValue -> CircuitPrograms.succeed (firstValue + secondValue)))

        let result =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal(8, result.Result.Value)
        Assert.Equal(3, result.Usage.InputTokens)
        Assert.Equal(5, result.Usage.OutputTokens)
        Assert.True(result.Session.IsSome)
        Assert.Equal("session-1", result.Session.Value.Id)

    [<Fact>]
    let ``call drops conflicting sessions while keeping the successful value`` () =
        let mutable callIndex = 0

        let runtime =
            DelegateRuntime(fun _agent _signature input _options _ct ->
                callIndex <- callIndex + 1
                let session = createSession $"session-{callIndex}"

                let result: RunResult<int> =
                    createResult
                        (RunId.New())
                        (RunUsage(0, 0))
                        (ValueSome session)
                        (CircuitResult<int>.Success(unbox<int> input + callIndex))

                box result)
            :> ICircuitRuntime

        let program =
            CircuitPrograms.bind (CircuitPrograms.call (createAgent ()) (createSignature ()) 1) (fun _ ->
                CircuitPrograms.call (createAgent ()) (createSignature ()) 2)

        let result =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None program
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal(4, result.Result.Value)
        Assert.False(result.Session.IsSome)

    [<Fact>]
    let ``call and parallel propagate raw task failures and linked cancellation`` () =
        let failingRuntime = FaultingRuntime("runtime task failed") :> ICircuitRuntime

        let callFailure =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    failingRuntime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.call (createAgent ()) (createSignature ()) 7)
                |> _.Result
                |> ignore)

        Assert.Equal("runtime task failed", callFailure.InnerException.Message)

        let runtime = NoopRuntime() :> ICircuitRuntime

        let childCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use cancelled = new CancellationTokenSource()

        let waitingRaw: CircuitPrograms.ProgramExpr<int> =
            fun state ->
                task {
                    try
                        do! Task.Delay(Timeout.Infinite, state.ChildCancellationToken)
                        return CircuitPrograms.ProgramSuccess 1
                    with :? OperationCanceledException ->
                        childCancelled.TrySetResult() |> ignore
                        return raise (OperationCanceledException(state.ChildCancellationToken))
                }

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.parallelPrograms
                    2
                    [ waitingRaw
                      CircuitPrograms.fail (
                          createFailure CircuitFailureCode.Workflow "broken" ValueNone ValueNone ValueNone ValueNone
                      ) ])
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.True(childCancelled.Task.Wait(1000), "Expected linked cancellation to reach the raw child task.")

        cancelled.Cancel()

        let directCancelled =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (cancelledProgram<int> cancelled.Token)
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(directCancelled.InnerException) |> ignore

    [<Fact>]
    let ``parallel preserves declaration order and respects max concurrency`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable active = 0
        let mutable maxActive = 0
        let gate = obj ()

        let work (delayMs: int) value =
            CircuitPrograms.code $"code-{value}" (fun ct ->
                task {
                    let activeNow = Interlocked.Increment &active
                    lock gate (fun () -> maxActive <- max maxActive activeNow)

                    try
                        do! Task.Delay(delayMs, ct)
                        return value
                    finally
                        Interlocked.Decrement &active |> ignore
                })

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.parallelPrograms 2 [ work 80 1; work 10 2; work 0 3 ])
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal<int list>([ 1; 2; 3 ], result.Result.Value)
        Assert.True(maxActive <= 2, $"Expected max concurrency <= 2, got {maxActive}.")
        Assert.True(maxActive >= 2, "Expected at least two concurrent operations.")

    [<Fact>]
    let ``parallel returns success for an empty program list`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.parallelPrograms 2 [])
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Empty(result.Result.Value)

    [<Fact>]
    let ``parallel cancels siblings after first failure`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let siblingCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let failure =
            createFailure CircuitFailureCode.Workflow "broken" ValueNone ValueNone ValueNone ValueNone

        let waitingProgram =
            CircuitPrograms.code "waiting" (fun ct ->
                task {
                    try
                        do! Task.Delay(Timeout.Infinite, ct)
                        return 1
                    with :? OperationCanceledException ->
                        siblingCancelled.TrySetResult() |> ignore
                        return raise (OperationCanceledException(ct))
                })

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.parallelPrograms 2 [ waitingProgram; CircuitPrograms.fail failure ])
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.True(siblingCancelled.Task.Wait(1000), "Expected the sibling operation to observe cancellation.")

    [<Fact>]
    let ``parallel returns a cancelled result when cancellation is requested before start`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable started = false

        let program =
            CircuitPrograms.parallelPrograms
                2
                [ CircuitPrograms.code "never.one" (fun _ ->
                      started <- true
                      Task.FromResult 1)
                  CircuitPrograms.code "never.two" (fun _ ->
                      started <- true
                      Task.FromResult 2) ]

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let result =
            CircuitPrograms.run runtime RunOptions.Default cts.Token program |> _.Result

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
            CircuitPrograms.code "first" (fun ct ->
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
            CircuitPrograms.code "second" (fun _ ->
                secondStarted <- true
                Task.FromResult 2)

        use cts = new CancellationTokenSource()

        let runTask =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                cts.Token
                (CircuitPrograms.parallelPrograms 1 [ first; second ])

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
            CircuitPrograms.code name (fun ct ->
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
            CircuitPrograms.run
                runtime
                RunOptions.Default
                cts.Token
                (CircuitPrograms.parallelPrograms 2 [ makeChild "child.one" 1; makeChild "child.two" 2 ])

        Assert.True(allStarted.Task.Wait(1000), "Expected both children to start before cancellation.")
        cts.Cancel()

        let result = runTask.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)
        Assert.True(allCancelled.Task.Wait(1000), "Expected started children to drain after cancellation.")

    [<Fact>]
    let ``delay bind and trywith cover successful and cancelled control flow`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable handlerCalled = false

        let successful =
            CircuitPrograms.bind (CircuitPrograms.delay (fun () -> CircuitPrograms.succeed 3)) (fun value ->
                CircuitPrograms.succeed (value * 2))

        let successfulResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None successful
            |> _.Result

        Assert.True(successfulResult.Result.IsSuccess)
        Assert.Equal(6, successfulResult.Result.Value)

        let cancelledProgram =
            CircuitPrograms.tryWith (CircuitPrograms.delay (fun () -> raise (OperationCanceledException()))) (fun _ ->
                handlerCalled <- true
                CircuitPrograms.succeed 99)

        let cancelledResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None cancelledProgram
            |> _.Result

        Assert.False(cancelledResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelledResult.Result.Failure.Code)
        Assert.False(handlerCalled)

    [<Fact>]
    let ``bind using and call cover cancellation and null resource branches`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let cancelledBind =
            CircuitPrograms.bind (CircuitPrograms.succeed 1) (fun _ -> raise (OperationCanceledException()))

        let cancelledBindResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None cancelledBind
            |> _.Result

        Assert.False(cancelledBindResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelledBindResult.Result.Failure.Code)

        let nullResourceProgram =
            CircuitPrograms.using Unchecked.defaultof<IDisposable> (fun _ -> CircuitPrograms.succeed 5)

        let nullResourceResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None nullResourceProgram
            |> _.Result

        Assert.True(nullResourceResult.Result.IsSuccess)
        Assert.Equal(5, nullResourceResult.Result.Value)

        use cts = new CancellationTokenSource()

        let cancellingRuntime =
            DelegateRuntime(fun _agent _signature _input _options _ct ->
                cts.Cancel()

                let result: RunResult<int> =
                    createResult (RunId.New()) (RunUsage(0, 0)) ValueNone (CircuitResult<int>.Success 42)

                box result)
            :> ICircuitRuntime

        let cancelledCall =
            CircuitPrograms.run
                cancellingRuntime
                RunOptions.Default
                cts.Token
                (CircuitPrograms.call (createAgent ()) (createSignature ()) 7)
            |> _.Result

        Assert.False(cancelledCall.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelledCall.Result.Failure.Code)

    [<Fact>]
    let ``parallel cancels queued work when a sibling fails before it can start`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable thirdStarted = false

        let first =
            CircuitPrograms.code "first" (fun ct ->
                task {
                    do! Task.Delay(50, ct)
                    return 1
                })

        let second =
            CircuitPrograms.fail (
                createFailure CircuitFailureCode.Workflow "boom" ValueNone ValueNone ValueNone ValueNone
            )

        let third =
            CircuitPrograms.code "third" (fun _ ->
                thirdStarted <- true
                Task.FromResult 3)

        let result =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.parallelPrograms 2 [ first; second; third ])
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.False(thirdStarted)

    [<Fact>]
    let ``code returns cancelled after late root cancellation and treats local cancellation as a workflow failure`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        use lateCancel = new CancellationTokenSource()

        let cancelledAfterAwait =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                lateCancel.Token
                (CircuitPrograms.code "late-cancel" (fun _ ->
                    lateCancel.Cancel()
                    Task.FromResult 5))
            |> _.Result

        Assert.False(cancelledAfterAwait.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelledAfterAwait.Result.Failure.Code)

        let localCancellation =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.code "local-cancel" (fun _ ->
                    Task.FromException<int>(OperationCanceledException("local"))))
            |> _.Result

        Assert.False(localCancellation.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, localCancellation.Result.Failure.Code)

        Assert.IsType<OperationCanceledException>(localCancellation.Result.Failure.Exception.Value)
        |> ignore

    [<Fact>]
    let ``bind and trywith propagate non cancellation exceptions from user callbacks`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let bindException =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.bind (CircuitPrograms.succeed 1) (fun _ ->
                        raise (InvalidOperationException("binder failed"))))
                |> _.Result
                |> ignore)

        Assert.Equal("binder failed", bindException.InnerException.Message)

        let handlerException =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.tryWith
                        (CircuitPrograms.delay (fun () -> raise (InvalidOperationException("body failed"))))
                        (fun _ -> raise (InvalidOperationException("handler failed"))))
                |> _.Result
                |> ignore)

        Assert.Equal("handler failed", handlerException.InnerException.Message)

    [<Fact>]
    let ``raw faulted program tasks exercise bind trywith tryfinally and delay await branches`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime

        let bindSourceFailure =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.bind (faultedProgram<int> "source task failed") (fun value ->
                        CircuitPrograms.succeed (value + 1)))
                |> _.Result
                |> ignore)

        Assert.Equal("source task failed", bindSourceFailure.InnerException.Message)

        let bindBoundFailure =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.bind (CircuitPrograms.succeed 1) (fun _ ->
                        faultedProgram<int> "bound task failed"))
                |> _.Result
                |> ignore)

        Assert.Equal("bound task failed", bindBoundFailure.InnerException.Message)

        let recovered =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.tryWith (faultedProgram<string> "body task failed") (fun ex ->
                    CircuitPrograms.succeed ex.Message))
            |> _.Result

        Assert.True(recovered.Result.IsSuccess)
        Assert.Equal("body task failed", recovered.Result.Value)

        let mutable compensated = false

        let tryFinallyFailure =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.tryFinally (faultedProgram<int> "finally task failed") (fun () ->
                        compensated <- true))
                |> _.Result
                |> ignore)

        Assert.True(compensated)
        Assert.Equal("finally task failed", tryFinallyFailure.InnerException.Message)

        let delayedTaskFailure =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.delay (fun () -> faultedProgram<int> "delayed task failed"))
                |> _.Result
                |> ignore)

        Assert.Equal("delayed task failed", delayedTaskFailure.InnerException.Message)

    [<Fact>]
    let ``cancelled tasks exercise delay bind trywith tryfinally and call cancellation branches`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let delayedCancelled =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.delay (fun () -> cancelledProgram<int> cancelled.Token))
            |> _.Result

        Assert.False(delayedCancelled.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, delayedCancelled.Result.Failure.Code)

        let boundCancelled =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.bind (CircuitPrograms.succeed 1) (fun _ -> cancelledProgram<int> cancelled.Token))
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(boundCancelled.InnerException) |> ignore

        let recoveredCancelled =
            CircuitPrograms.run
                runtime
                RunOptions.Default
                CancellationToken.None
                (CircuitPrograms.tryWith (cancelledProgram<string> cancelled.Token) (fun _ ->
                    CircuitPrograms.succeed "ok"))
            |> _.Result

        Assert.False(recoveredCancelled.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, recoveredCancelled.Result.Failure.Code)

        let finallyCancelled =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    runtime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.tryFinally (cancelledProgram<int> cancelled.Token) ignore)
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(finallyCancelled.InnerException) |> ignore

        let cancelledRuntime = CancelledRuntime(cancelled.Token) :> ICircuitRuntime

        let callCancelled =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run
                    cancelledRuntime
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.call (createAgent ()) (createSignature ()) 7)
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(callCancelled.InnerException) |> ignore

    [<Fact>]
    let ``delay trywith and tryfinally cover success failure and faulted task branches`` () =
        let runtime = NoopRuntime() :> ICircuitRuntime
        let mutable handlerCalled = false
        let mutable compensated = false

        let passthrough =
            CircuitPrograms.tryWith (CircuitPrograms.succeed 4) (fun _ ->
                handlerCalled <- true
                CircuitPrograms.succeed 0)

        let passthroughResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None passthrough
            |> _.Result

        Assert.True(passthroughResult.Result.IsSuccess)
        Assert.Equal(4, passthroughResult.Result.Value)
        Assert.False(handlerCalled)

        let delayedFailure =
            CircuitPrograms.delay (fun () ->
                CircuitPrograms.fail (
                    createFailure CircuitFailureCode.Tool "tool failed" ValueNone ValueNone ValueNone ValueNone
                ))

        let delayedFailureResult =
            CircuitPrograms.run runtime RunOptions.Default CancellationToken.None delayedFailure
            |> _.Result

        Assert.False(delayedFailureResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, delayedFailureResult.Result.Failure.Code)

        let faulting =
            CircuitPrograms.tryFinally
                (CircuitPrograms.delay (fun () -> raise (InvalidOperationException("boom"))))
                (fun () -> compensated <- true)

        let ex =
            Assert.Throws<AggregateException>(fun () ->
                CircuitPrograms.run runtime RunOptions.Default CancellationToken.None faulting
                |> _.Result
                |> ignore)

        Assert.True(compensated)
        Assert.IsType<InvalidOperationException>(ex.InnerException)

    [<Fact>]
    let ``program helpers validate null and blank arguments`` () =
        let agent = createAgent ()
        let signature = createSignature ()

        let nullFailure =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.fail Unchecked.defaultof<CircuitFailure> |> ignore)

        Assert.Equal("failure", nullFailure.ParamName)

        let nullDelay =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.delay Unchecked.defaultof<unit -> _> |> ignore)

        Assert.Equal("generator", nullDelay.ParamName)

        let nullBinder =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.bind (CircuitPrograms.succeed 1) Unchecked.defaultof<int -> _>
                |> ignore)

        Assert.Equal("binder", nullBinder.ParamName)

        let nullHandler =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.tryWith (CircuitPrograms.succeed 1) Unchecked.defaultof<exn -> _>
                |> ignore)

        Assert.Equal("handler", nullHandler.ParamName)

        let nullCompensation =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.tryFinally (CircuitPrograms.succeed 1) Unchecked.defaultof<unit -> unit>
                |> ignore)

        Assert.Equal("compensation", nullCompensation.ParamName)

        let blankCodeName =
            Assert.Throws<ArgumentException>(fun () -> CircuitPrograms.code " " (fun _ -> Task.FromResult 1) |> ignore)

        Assert.Equal("name", blankCodeName.ParamName)

        let nullOperation =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.code "ok" Unchecked.defaultof<CancellationToken -> Task<int>>
                |> ignore)

        Assert.Equal("operation", nullOperation.ParamName)

        let nullAgent =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.call Unchecked.defaultof<AgentDefinition> signature 1 |> ignore)

        Assert.Equal("agent", nullAgent.ParamName)

        let nullSignature =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.call agent Unchecked.defaultof<Signature<int, int>> 1 |> ignore)

        Assert.Equal("signature", nullSignature.ParamName)

        let invalidConcurrency =
            Assert.Throws<ArgumentException>(fun () -> CircuitPrograms.parallelPrograms 0 [] |> ignore)

        Assert.Equal("maxConcurrency", invalidConcurrency.ParamName)

        let nullPrograms =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.parallelPrograms 1 Unchecked.defaultof<CircuitPrograms.ProgramExpr<int> list>
                |> ignore)

        Assert.Equal("programs", nullPrograms.ParamName)

        let runtime = NoopRuntime() :> ICircuitRuntime

        let nullRuntime =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.run
                    Unchecked.defaultof<ICircuitRuntime>
                    RunOptions.Default
                    CancellationToken.None
                    (CircuitPrograms.succeed 1)
                |> ignore)

        Assert.Equal("runtime", nullRuntime.ParamName)

        let nullOptions =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitPrograms.run
                    runtime
                    Unchecked.defaultof<RunOptions>
                    CancellationToken.None
                    (CircuitPrograms.succeed 1)
                |> ignore)

        Assert.Equal("options", nullOptions.ParamName)
