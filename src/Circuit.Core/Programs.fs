#nowarn "3511"

namespace Circuit.Core

open System
open System.Threading
open System.Threading.Tasks

module internal CircuitPrograms =
    type SessionAggregation =
        | NoSession
        | SingleSession of CircuitSession
        | ConflictingSessions

    type ProgramState =
        { Runtime: ICircuitRuntime
          Options: RunOptions
          RootRunId: RunId
          StartedAt: DateTimeOffset
          CancellationToken: CancellationToken
          ChildCancellationToken: CancellationToken
          NextOperationIndex: int ref
          UsageGate: obj
          MutableUsage: int ref * int ref
          SessionGate: obj
          MutableSession: SessionAggregation ref }

    type ProgramOutcome<'T> =
        | ProgramSuccess of 'T
        | ProgramFailure of CircuitFailure

    type ProgramExpr<'T> = ProgramState -> Task<ProgramOutcome<'T>>

    let private createFailure code runId operationId message requestId innerException =
        CircuitFailure(code, message, ValueSome runId, operationId, requestId, innerException)

    let private cancelledFailure runId operationId message innerException =
        createFailure CircuitFailureCode.Cancelled runId operationId message ValueNone innerException

    let private workflowFailure runId operationId message innerException =
        createFailure CircuitFailureCode.Workflow runId operationId message ValueNone innerException

    let private nextOperationId (state: ProgramState) =
        lock state.UsageGate (fun () ->
            let index = !(state.NextOperationIndex) + 1
            state.NextOperationIndex := index
            $"op-{index:D4}")

    let private addUsage (state: ProgramState) (usage: RunUsage) =
        lock state.UsageGate (fun () ->
            let inputTokens, outputTokens = state.MutableUsage
            inputTokens := !(inputTokens) + usage.InputTokens
            outputTokens := !(outputTokens) + usage.OutputTokens)

    let private recordSession (state: ProgramState) (session: CircuitSession voption) =
        match session with
        | ValueNone -> ()
        | ValueSome session ->
            lock state.SessionGate (fun () ->
                match !(state.MutableSession) with
                | NoSession -> state.MutableSession := SingleSession session
                | SingleSession existing when existing.Id = session.Id -> ()
                | SingleSession _
                | ConflictingSessions -> state.MutableSession := ConflictingSessions)

    let private finalizeFailure (state: ProgramState) operationId (failure: CircuitFailure) =
        if state.CancellationToken.IsCancellationRequested then
            ProgramFailure(cancelledFailure state.RootRunId operationId "The circuit run was cancelled." ValueNone)
        else
            ProgramFailure(
                createFailure
                    failure.Code
                    state.RootRunId
                    (match operationId with
                     | ValueSome value -> ValueSome value
                     | ValueNone -> failure.OperationId)
                    failure.Message
                    failure.RequestId
                    failure.Exception
            )

    let private ensureActive (state: ProgramState) operationId =
        if state.CancellationToken.IsCancellationRequested then
            ProgramFailure(cancelledFailure state.RootRunId operationId "The circuit run was cancelled." ValueNone)
        else
            ProgramSuccess()

    let succeed value : ProgramExpr<'T> =
        fun _ -> Task.FromResult(ProgramSuccess value)

    let fail (failure: CircuitFailure) : ProgramExpr<'T> =
        if isNull (box failure) then
            nullArg "failure"

        fun state -> Task.FromResult(finalizeFailure state ValueNone failure)

    let delay generator : ProgramExpr<'T> =
        if isNull (box generator) then
            nullArg "generator"

        fun state ->
            task {
                try
                    let program = generator ()
                    return! program state
                with ex when
                    state.CancellationToken.IsCancellationRequested
                    || (ex :? OperationCanceledException) ->
                    return
                        ProgramFailure(
                            cancelledFailure state.RootRunId ValueNone "The circuit run was cancelled." (ValueSome ex)
                        )
            }

    let bind (program: ProgramExpr<'T>) (binder: 'T -> ProgramExpr<'U>) : ProgramExpr<'U> =
        if isNull (box binder) then
            nullArg "binder"

        fun state ->
            task {
                let! outcome = program state

                match outcome with
                | ProgramFailure failure -> return ProgramFailure failure
                | ProgramSuccess value ->
                    try
                        return! (binder value) state
                    with ex when
                        state.CancellationToken.IsCancellationRequested
                        || (ex :? OperationCanceledException) ->
                        return
                            ProgramFailure(
                                cancelledFailure
                                    state.RootRunId
                                    ValueNone
                                    "The circuit run was cancelled."
                                    (ValueSome ex)
                            )
            }

    let combine first second = bind first (fun () -> second)

    let tryWith (body: ProgramExpr<'T>) (handler: exn -> ProgramExpr<'T>) : ProgramExpr<'T> =
        if isNull (box handler) then
            nullArg "handler"

        fun state ->
            task {
                try
                    return! body state
                with
                | ex when
                    state.CancellationToken.IsCancellationRequested
                    || (ex :? OperationCanceledException)
                    ->
                    return
                        ProgramFailure(
                            cancelledFailure state.RootRunId ValueNone "The circuit run was cancelled." (ValueSome ex)
                        )
                | ex ->
                    let next = handler ex
                    return! next state
            }

    let tryFinally (body: ProgramExpr<'T>) (compensation: unit -> unit) : ProgramExpr<'T> =
        if isNull (box compensation) then
            nullArg "compensation"

        fun state ->
            task {
                try
                    return! body state
                finally
                    compensation ()
            }

    let using (resource: 'T :> IDisposable) (binder: 'T -> ProgramExpr<'U>) =
        tryFinally (binder resource) (fun () ->
            if not (isNull (box resource)) then
                resource.Dispose())

    let code name operation : ProgramExpr<'T> =
        if String.IsNullOrWhiteSpace name then
            invalidArg "name" "name cannot be blank."

        if isNull (box operation) then
            nullArg "operation"

        fun state ->
            task {
                let operationId = ValueSome(nextOperationId state)

                match ensureActive state operationId with
                | ProgramFailure failure -> return ProgramFailure failure
                | ProgramSuccess _ ->
                    try
                        let! value = operation state.ChildCancellationToken

                        if state.CancellationToken.IsCancellationRequested then
                            return
                                ProgramFailure(
                                    cancelledFailure
                                        state.RootRunId
                                        operationId
                                        "The circuit run was cancelled."
                                        ValueNone
                                )
                        else
                            return ProgramSuccess value
                    with
                    | :? OperationCanceledException as ex when state.CancellationToken.IsCancellationRequested ->
                        return
                            ProgramFailure(
                                cancelledFailure
                                    state.RootRunId
                                    operationId
                                    "The circuit run was cancelled."
                                    (ValueSome ex)
                            )
                    | ex ->
                        return
                            ProgramFailure(
                                workflowFailure
                                    state.RootRunId
                                    operationId
                                    $"Circuit code step '{name}' failed."
                                    (ValueSome ex)
                            )
            }

    let call (agent: AgentDefinition) (signature: Signature<'Input, 'Output>) (input: 'Input) : ProgramExpr<'Output> =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        fun state ->
            task {
                let operationId = ValueSome(nextOperationId state)

                match ensureActive state operationId with
                | ProgramFailure failure -> return ProgramFailure failure
                | ProgramSuccess _ ->
                    let! result =
                        state.Runtime.RunAsync(agent, signature, input, state.Options, state.ChildCancellationToken)

                    addUsage state result.Usage
                    recordSession state result.Session

                    if state.CancellationToken.IsCancellationRequested then
                        return
                            ProgramFailure(
                                cancelledFailure state.RootRunId operationId "The circuit run was cancelled." ValueNone
                            )
                    elif result.Result.IsSuccess then
                        return ProgramSuccess result.Result.Value
                    else
                        return
                            ProgramFailure(
                                (finalizeFailure state operationId result.Result.Failure
                                 |> function
                                     | ProgramFailure failure -> failure
                                     | _ -> invalidOp "unreachable")
                            )
            }

    let parallelPrograms maxConcurrency (programs: ProgramExpr<'T> list) : ProgramExpr<'T list> =
        if maxConcurrency < 1 then
            invalidArg "maxConcurrency" "maxConcurrency must be at least 1."

        if isNull (box programs) then
            nullArg "programs"

        fun state ->
            task {
                let programArray = programs |> List.toArray

                if programArray.Length = 0 then
                    return ProgramSuccess []
                else
                    use linkedCts =
                        CancellationTokenSource.CreateLinkedTokenSource(state.CancellationToken)

                    use semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency)
                    let results = Array.zeroCreate<'T> programArray.Length
                    let mutable firstFailure: (int * CircuitFailure) option = None
                    let failureGate = obj ()

                    let recordFailure index failure =
                        lock failureGate (fun () ->
                            match firstFailure with
                            | Some _ -> false
                            | None ->
                                firstFailure <- Some(index, failure)
                                true)

                    let runProgram index program =
                        task {
                            let mutable semaphoreEntered = false

                            try
                                try
                                    do! semaphore.WaitAsync(linkedCts.Token)
                                    semaphoreEntered <- true
                                with :? OperationCanceledException when
                                    state.CancellationToken.IsCancellationRequested
                                    || linkedCts.IsCancellationRequested ->
                                    return ()

                                match firstFailure with
                                | Some _ when not state.CancellationToken.IsCancellationRequested -> return ()
                                | _ ->
                                    try
                                        let childState =
                                            { state with
                                                ChildCancellationToken = linkedCts.Token }

                                        let! outcome = program childState

                                        match outcome with
                                        | ProgramSuccess value -> results[index] <- value
                                        | ProgramFailure failure ->
                                            if recordFailure index failure then
                                                linkedCts.Cancel()
                                    with :? OperationCanceledException when
                                        state.CancellationToken.IsCancellationRequested
                                        || linkedCts.IsCancellationRequested ->
                                        return ()
                            finally
                                if semaphoreEntered then
                                    semaphore.Release() |> ignore
                        }

                    let tasks =
                        programArray |> Array.mapi (fun index program -> runProgram index program)

                    try
                        let! _ = Task.WhenAll tasks
                        ()
                    with :? OperationCanceledException when
                        state.CancellationToken.IsCancellationRequested
                        || linkedCts.IsCancellationRequested ->
                        ()

                    if state.CancellationToken.IsCancellationRequested then
                        return
                            ProgramFailure(
                                cancelledFailure state.RootRunId ValueNone "The circuit run was cancelled." ValueNone
                            )
                    else
                        match firstFailure with
                        | Some(_, failure) -> return ProgramFailure failure
                        | None -> return ProgramSuccess(Array.toList results)
            }

    let run
        (runtime: ICircuitRuntime)
        (options: RunOptions)
        (cancellationToken: CancellationToken)
        (program: ProgramExpr<'T>)
        =
        if isNull (box runtime) then
            nullArg "runtime"

        if isNull (box options) then
            nullArg "options"

        let startedAt = DateTimeOffset.UtcNow
        let rootRunId = RunId.New()
        let nextOperationIndex = ref 0
        let inputTokens = ref 0
        let outputTokens = ref 0
        let sessionState = ref NoSession

        let state =
            { Runtime = runtime
              Options = options
              RootRunId = rootRunId
              StartedAt = startedAt
              CancellationToken = cancellationToken
              ChildCancellationToken = cancellationToken
              NextOperationIndex = nextOperationIndex
              UsageGate = obj ()
              MutableUsage = inputTokens, outputTokens
              SessionGate = obj ()
              MutableSession = sessionState }

        task {
            let! outcome = program state
            let usage = RunUsage(!inputTokens, !outputTokens)

            let session =
                match !sessionState with
                | SingleSession session -> ValueSome session
                | NoSession
                | ConflictingSessions -> ValueNone

            let result =
                match outcome with
                | ProgramSuccess value -> CircuitResult<'T>.Success value
                | ProgramFailure failure -> CircuitResult<'T>.Error failure

            return RunResult(rootRunId, result, usage, session, startedAt, DateTimeOffset.UtcNow)
        }
