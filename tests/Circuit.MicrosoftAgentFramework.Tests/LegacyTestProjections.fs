namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Circuit.Core

/// Test-only migration helpers: retained adapter regression tests execute agent leaves as
/// one-node Circuits without restoring the removed public direct-agent runtime API.
[<AutoOpen>]
module internal LegacyTestProjections =
    type ICircuitRuntime with
        member runtime.RunAsync<'Input, 'Output>
            (
                agent: AgentDefinition,
                signature: Signature<'Input, 'Output>,
                input: 'Input,
                options: RunOptions,
                cancellationToken: CancellationToken
            ) =
            task {
                let startedAt = DateTimeOffset.UtcNow
                let! response = Circuit.run runtime (Circuit.agent agent signature) input options cancellationToken

                let result =
                    if response.IsSuccess then
                        CircuitResult<'Output>.Success(response.Value)
                    else
                        CircuitResult<'Output>.Error(response.Failure)

                return
                    RunResult<'Output>(
                        response.Metadata.RunId,
                        result,
                        response.Metadata.Usage,
                        response.Metadata.Session,
                        startedAt,
                        response.Metadata.CompletedAt
                    )
            }

        member runtime.RunStreamingAsync<'Input, 'Output>
            (
                agent: AgentDefinition,
                signature: Signature<'Input, 'Output>,
                input: 'Input,
                options: RunOptions,
                cancellationToken: CancellationToken
            ) : IAsyncEnumerable<RunEvent<'Output>> =
            let source =
                Circuit.start runtime (Circuit.agent agent signature) input options cancellationToken

            { new IAsyncEnumerable<RunEvent<'Output>> with
                member _.GetAsyncEnumerator(enumerationToken) =
                    let mutable run: CircuitRun<'Output> option = None
                    let mutable inner: IAsyncEnumerator<CircuitEvent<'Output>> option = None
                    let mutable current = Unchecked.defaultof<RunEvent<'Output>>
                    let mutable sequence = -1L
                    let mutable output: Response<'Output> option = None

                    let initialize () =
                        task {
                            if run.IsNone then
                                let! value = source
                                run <- Some value
                                inner <- Some(value.Events.GetAsyncEnumerator(enumerationToken))
                        }

                    { new IAsyncEnumerator<RunEvent<'Output>> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            ValueTask<bool>(
                                task {
                                    do! initialize ()
                                    let mutable found = false
                                    let mutable more = true

                                    while more && not found do
                                        let! available = inner.Value.MoveNextAsync().AsTask()
                                        more <- available

                                        if available then
                                            let now = DateTimeOffset.UtcNow
                                            let runId = run.Value.RunId

                                            let make kind text value failure approval operationId =
                                                sequence <- sequence + 1L

                                                RunEvent<'Output>(
                                                    sequence,
                                                    runId,
                                                    now,
                                                    kind,
                                                    operationId,
                                                    text,
                                                    value,
                                                    failure,
                                                    approval
                                                )

                                            match inner.Value.Current with
                                            | RunStarted _ ->
                                                current <-
                                                    make
                                                        RunEventKind.RunStarted
                                                        ValueNone
                                                        ValueNone
                                                        ValueNone
                                                        ValueNone
                                                        ValueNone

                                                found <- true
                                            | OutputDelta delta ->
                                                current <-
                                                    make
                                                        RunEventKind.OutputDelta
                                                        (ValueSome delta.Text)
                                                        ValueNone
                                                        ValueNone
                                                        ValueNone
                                                        (ValueSome delta.NodePath)

                                                found <- true
                                            | ApprovalRequested request ->
                                                current <-
                                                    make
                                                        RunEventKind.ApprovalRequested
                                                        ValueNone
                                                        ValueNone
                                                        ValueNone
                                                        (ValueSome request)
                                                        (ValueSome request.RequestId)

                                                found <- true
                                            | OutputProduced(_, response) -> output <- Some response
                                            | RunCompleted terminal ->
                                                match terminal.Outcome, output with
                                                | Succeeded _, Some response when response.IsSuccess ->
                                                    current <-
                                                        make
                                                            RunEventKind.RunCompleted
                                                            ValueNone
                                                            (ValueSome response.Value)
                                                            ValueNone
                                                            ValueNone
                                                            ValueNone

                                                    current.RuntimeUsage <- terminal.Metadata.Usage
                                                    current.RuntimeSession <- response.Metadata.Session
                                                | Succeeded _, Some response ->
                                                    current <-
                                                        make
                                                            RunEventKind.RunFailed
                                                            ValueNone
                                                            ValueNone
                                                            (ValueSome response.Failure)
                                                            ValueNone
                                                            ValueNone

                                                    current.RuntimeUsage <- terminal.Metadata.Usage
                                                | Failed failure, _ ->
                                                    current <-
                                                        make
                                                            RunEventKind.RunFailed
                                                            ValueNone
                                                            ValueNone
                                                            (ValueSome failure)
                                                            ValueNone
                                                            ValueNone

                                                    current.RuntimeUsage <- terminal.Metadata.Usage
                                                | _ ->
                                                    let failure =
                                                        CircuitFailure.Create(
                                                            CircuitFailureCode.Cardinality,
                                                            "The migrated streaming regression expected one output."
                                                        )

                                                    current <-
                                                        make
                                                            RunEventKind.RunFailed
                                                            ValueNone
                                                            ValueNone
                                                            (ValueSome failure)
                                                            ValueNone
                                                            ValueNone

                                                found <- true
                                            | _ -> ()

                                    return found
                                }
                            )

                        member _.DisposeAsync() =
                            ValueTask(
                                task {
                                    match inner with
                                    | Some value -> do! value.DisposeAsync().AsTask()
                                    | None -> ()

                                    match run with
                                    | Some value -> do! (value :> IAsyncDisposable).DisposeAsync().AsTask()
                                    | None -> ()
                                }
                            ) } }
