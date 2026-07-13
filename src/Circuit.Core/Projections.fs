#nowarn "3511"

namespace Circuit.Core

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

module private ProjectionHelpers =
    let failure code (metadata: ResponseMetadata) message =
        CircuitFailure(code, message, ValueSome metadata.RunId, ValueSome metadata.NodePath, ValueNone, ValueNone)

    let approvalFailure (request: ApprovalRequest) (metadata: ResponseMetadata) =
        CircuitFailure(
            CircuitFailureCode.ApprovalRequired,
            "The Circuit projection encountered an approval request. Use Circuit.start or configure an approval host.",
            ValueSome metadata.RunId,
            ValueSome metadata.NodePath,
            ValueSome request.RequestId,
            ValueNone
        )

    type StreamEnumerator<'Input, 'Output>
        (
            runtime: ICircuitRuntime,
            circuit: Circuit<'Input, 'Output>,
            input: 'Input,
            options: RunOptions,
            cancellationToken: CancellationToken
        ) =
        let mutable run: CircuitRun<'Output> option = None
        let mutable events: IAsyncEnumerator<CircuitEvent<'Output>> option = None
        let mutable current = Unchecked.defaultof<Response<'Output>>
        let mutable complete = false

        let initialize () =
            task {
                if run.IsNone then
                    let! value = runtime.StartAsync(circuit, input, options, cancellationToken)
                    run <- Some value
                    // The scheduler owns the supplied cancellation token and always publishes a
                    // terminal response. Keep consuming with an uncancelled reader so callers see
                    // that typed terminal instead of an out-of-band MoveNext cancellation.
                    events <- Some(value.Events.GetAsyncEnumerator(CancellationToken.None))
            }

        interface IAsyncEnumerator<Response<'Output>> with
            member _.Current = current

            member _.MoveNextAsync() =
                ValueTask<bool>(
                    task {
                        if complete then
                            return false
                        else
                            do! initialize ()
                            let mutable searching = true
                            let mutable found = false

                            while searching do
                                let eventEnumerator = events.Value
                                let! more = eventEnumerator.MoveNextAsync().AsTask()

                                if not more then
                                    searching <- false
                                    complete <- true
                                else
                                    match eventEnumerator.Current with
                                    | OutputProduced(_, response) ->
                                        current <- response
                                        found <- true
                                        searching <- false
                                    | ApprovalRequested request ->
                                        let now = DateTimeOffset.UtcNow

                                        let metadata =
                                            ResponseMetadata(
                                                ValueNone,
                                                ValueNone,
                                                Array.empty,
                                                run.Value.RunId,
                                                circuit.Id.Value,
                                                RunUsage(0, 0),
                                                ValueNone,
                                                1,
                                                now,
                                                now,
                                                "projection"
                                            )

                                        current <-
                                            Response<'Output>.Create(Failed(approvalFailure request metadata), metadata)

                                        found <- true
                                        searching <- false
                                        complete <- true
                                    | RunCompleted response ->
                                        match response.Outcome with
                                        | Failed failure ->
                                            current <- Response<'Output>.Create(Failed failure, response.Metadata)
                                            found <- true
                                        | Succeeded _ -> ()

                                        searching <- false
                                        complete <- true
                                    | _ -> ()

                            return found
                    }
                )

            member _.DisposeAsync() =
                ValueTask(
                    task {
                        match events with
                        | Some value -> do! value.DisposeAsync().AsTask()
                        | None -> ()

                        match run with
                        | Some value -> do! (value :> IAsyncDisposable).DisposeAsync().AsTask()
                        | None -> ()
                    }
                )

    type StreamEnumerable<'Input, 'Output>(runtime, circuit, input, options, cancellationToken) =
        interface IAsyncEnumerable<Response<'Output>> with
            member _.GetAsyncEnumerator(enumerationCancellationToken) =
                let token =
                    if enumerationCancellationToken.CanBeCanceled then
                        enumerationCancellationToken
                    else
                        cancellationToken

                StreamEnumerator<'Input, 'Output>(runtime, circuit, input, options, token)
                :> IAsyncEnumerator<Response<'Output>>

/// Execution projections over the unified Circuit event-stream kernel.
module Circuit =
    /// <summary>Assigns an explicit root identity and semantic version.</summary>
    let define id version circuit =
        CircuitDefinition.define id version circuit

    /// <summary>Creates a typed agent-leaf Circuit.</summary>
    let agent agent signature = CircuitDefinition.agent agent signature

    /// <summary>Creates a durable trusted-code Circuit leaf.</summary>
    let code id version handler =
        CircuitDefinition.code id version handler

    /// <summary>Creates a serialized immutable constant Circuit.</summary>
    let value value = CircuitDefinition.value value

    /// <summary>Creates a finite source keyed by source ordinal.</summary>
    let items id version values =
        CircuitDefinition.items id version values

    /// <summary>Creates a finite source with caller-defined stable keys.</summary>
    let keyedItems id version key values =
        CircuitDefinition.keyedItems id version key values

    /// <summary>Creates a durable cursor-aware source.</summary>
    let source id version value =
        CircuitDefinition.source id version value

    /// <summary>Creates a non-checkpointable asynchronous source.</summary>
    let asyncSource id version value =
        CircuitDefinition.asyncSource id version value

    /// <summary>Pipelines successful lane outputs into the next Circuit.</summary>
    let thenStep next previous =
        CircuitDefinition.thenStep next previous

    /// <summary>Builds a validated dynamic child Circuit for each successful lane.</summary>
    let thenDynamic id version key maximum factory previous =
        CircuitDefinition.thenDynamic id version key maximum factory previous

    /// <summary>Captures lane failures as response values.</summary>
    let attempt previous = CircuitDefinition.attempt previous

    /// <summary>Maps lane failures into replacement values.</summary>
    let recover id version handler previous =
        CircuitDefinition.recover id version handler previous

    /// <summary>Selects one named Circuit branch for each input.</summary>
    let branch id version selector cases fallback =
        CircuitDefinition.branch id version selector cases fallback

    /// <summary>Merges bounded independent Circuit branches.</summary>
    let merge id version maximum branches =
        CircuitDefinition.merge id version maximum branches

    /// <summary>Repeats a Circuit body under a bounded predicate.</summary>
    let loop id version maximum predicate body =
        CircuitDefinition.loop id version maximum predicate body

    /// <summary>Creates a host-approval pause.</summary>
    let approval id version prompt =
        CircuitDefinition.approval id version prompt

    /// <summary>Aggregates all lane responses into a new typed output.</summary>
    let aggregate id version handler previous =
        CircuitDefinition.aggregate id version handler previous

    /// <summary>Adds a stable name segment to a Circuit graph.</summary>
    let named id circuit = CircuitDefinition.named id circuit
    /// <summary>Validates a Circuit definition without executing it.</summary>
    let validate circuit = CircuitDefinition.validate circuit

    /// Starts the full live protocol.
    let start (runtime: ICircuitRuntime) (circuit: Circuit<'Input, 'Output>) input options cancellationToken =
        if isNull (box runtime) then
            nullArg "runtime"

        runtime.StartAsync(circuit, input, options, cancellationToken)

    /// Resumes the full live protocol from an exact-definition checkpoint and rebinds process-local dependencies.
    let resume
        (runtime: ICircuitRuntime)
        (circuit: Circuit<'Input, 'Output>)
        checkpoint
        (options: ResumeOptions)
        cancellationToken
        =
        if isNull (box runtime) then
            nullArg "runtime"

        if isNull (box options) then
            nullArg "options"

        runtime.ResumeAsync(circuit, checkpoint, options, cancellationToken)

    /// Collects root responses in completion order.
    let collect (runtime: ICircuitRuntime) (circuit: Circuit<'Input, 'Output>) input options cancellationToken =
        task {
            let! run = runtime.StartAsync(circuit, input, options, cancellationToken)
            let results = ResizeArray<Response<'Output>>()
            let mutable terminal: Response<RunSummary> voption = ValueNone
            let mutable approval: ApprovalRequest voption = ValueNone

            try
                // Cancellation is delivered to the scheduler. The reader stays alive long enough
                // to receive its typed Cancelled terminal response.
                let enumerator = run.Events.GetAsyncEnumerator(CancellationToken.None)
                let mutable more = true

                while more && approval.IsNone do
                    let! available = enumerator.MoveNextAsync().AsTask()
                    more <- available

                    if available then
                        match enumerator.Current with
                        | OutputProduced(_, response) -> results.Add response
                        | ApprovalRequested request -> approval <- ValueSome request
                        | RunCompleted response -> terminal <- ValueSome response
                        | _ -> ()

                do! enumerator.DisposeAsync().AsTask()

                let result =
                    match approval, terminal with
                    | ValueSome request, _ ->
                        let now = DateTimeOffset.UtcNow

                        let metadata =
                            ResponseMetadata(
                                ValueNone,
                                ValueNone,
                                Array.empty,
                                run.RunId,
                                circuit.Id.Value,
                                RunUsage(0, 0),
                                ValueNone,
                                1,
                                now,
                                now,
                                "projection"
                            )

                        Response<IReadOnlyList<Response<'Output>>>
                            .Create(Failed(ProjectionHelpers.approvalFailure request metadata), metadata)
                    | ValueNone, ValueSome completed ->
                        match completed.Outcome with
                        | Failed failure ->
                            Response<IReadOnlyList<Response<'Output>>>.Create(Failed failure, completed.Metadata)
                        | Succeeded _ ->
                            Response<IReadOnlyList<Response<'Output>>>
                                .Create(
                                    Succeeded(results.ToArray() :> IReadOnlyList<Response<'Output>>),
                                    completed.Metadata
                                )
                    | _ ->
                        let now = DateTimeOffset.UtcNow

                        let metadata =
                            ResponseMetadata(
                                ValueNone,
                                ValueNone,
                                Array.empty,
                                run.RunId,
                                circuit.Id.Value,
                                RunUsage(0, 0),
                                ValueNone,
                                1,
                                now,
                                now,
                                "projection"
                            )

                        Response<IReadOnlyList<Response<'Output>>>
                            .Create(
                                Failed(
                                    ProjectionHelpers.failure
                                        CircuitFailureCode.Engine
                                        metadata
                                        "The Circuit event stream ended without a terminal event."
                                ),
                                metadata
                            )

                do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
                return result
            with ex ->
                do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
                return raise ex
        }

    /// Collects root responses and resequences them lexicographically by the full hierarchical source order.
    let collectSourceOrder
        (runtime: ICircuitRuntime)
        (circuit: Circuit<'Input, 'Output>)
        input
        options
        cancellationToken
        =
        task {
            let! collected = collect runtime circuit input options cancellationToken

            match collected.Outcome with
            | Failed failure ->
                return Response<IReadOnlyList<Response<'Output>>>.Create(Failed failure, collected.Metadata)
            | Succeeded values ->
                let ordered =
                    values
                    |> Seq.sortWith (fun left right ->
                        compare (left.Metadata.SourceOrder |> Seq.toList) (right.Metadata.SourceOrder |> Seq.toList))
                    |> Seq.toArray

                return
                    Response<IReadOnlyList<Response<'Output>>>
                        .Create(Succeeded(ordered :> IReadOnlyList<Response<'Output>>), collected.Metadata)
        }

    /// Runs a Circuit that must emit exactly one root response.
    let run (runtime: ICircuitRuntime) (circuit: Circuit<'Input, 'Output>) input options cancellationToken =
        task {
            let! collected = collect runtime circuit input options cancellationToken

            match collected.Outcome with
            | Failed failure -> return Response<'Output>.Create(Failed failure, collected.Metadata)
            | Succeeded values when values.Count = 1 -> return values[0]
            | Succeeded values ->
                let failure =
                    ProjectionHelpers.failure
                        CircuitFailureCode.Cardinality
                        collected.Metadata
                        $"Circuit.run requires exactly one root output, but the Circuit produced {values.Count}."

                return Response<'Output>.Create(Failed failure, collected.Metadata)
        }

    /// Streams completed root responses in completion order and owns the live run.
    let stream (runtime: ICircuitRuntime) (circuit: Circuit<'Input, 'Output>) input options cancellationToken =
        ProjectionHelpers.StreamEnumerable<'Input, 'Output>(runtime, circuit, input, options, cancellationToken)
        :> IAsyncEnumerable<Response<'Output>>
