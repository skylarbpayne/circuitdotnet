module Tutorial

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

type Ticket =
    { Id: string
      Kind: string
      DelayMs: int }

type CodeOnlyRuntime() =
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
        Task.FromException<RunResult<'Output>>(
            InvalidOperationException("This offline tutorial uses code leaves only.")
        )

    override _.SerializeSessionCoreAsync(_agent, _session, _runOptions, _cancellationToken) = ValueTask<JsonElement>()

    override _.DeserializeSessionCoreAsync(_agent, _state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>()

let tickets: IReadOnlyList<Ticket> =
    [| { Id = "ticket-1"
         Kind = "billing"
         DelayMs = 80 }
       { Id = "ticket-2"
         Kind = "controlled-failure"
         DelayMs = 10 }
       { Id = "ticket-3"
         Kind = "security"
         DelayMs = 20 } |]

let source =
    Circuit.keyedItems "tickets" "1.0.0" (fun ticket -> ticket.Id) (fun (_: unit) -> tickets)

let processTicket (ticket: Ticket) =
    Circuit.code ($"process-{ticket.Kind}") "1.0.0" (fun context value ->
        task {
            do! Task.Delay(value.DelayMs, context.CancellationToken)

            if value.Kind = "controlled-failure" then
                return
                    Response.fail
                        context
                        (CircuitFailure.Create(CircuitFailureCode.Provider, "controlled tutorial failure"))
            else
                return Response.succeed context ($"{value.Id}:{value.Kind}:ok")
        })

let buildChild (ticket: Ticket) =
    match ticket.Kind with
    | "security" ->
        processTicket ticket
        |> Circuit.thenStep (
            Circuit.code "security-audit" "1.0.0" (fun context value ->
                Task.FromResult(Response.succeed context (value + ":audited")))
        )
    | "controlled-failure" ->
        processTicket ticket
        |> Circuit.recover "recover-provider" "1.0.0" (fun failure -> $"{ticket.Id}:recovered:{failure.Code}")
        |> Circuit.thenStep (Circuit.value $"{ticket.Id}:recovery-complete")
    | _ -> processTicket ticket

let pipeline =
    source
    |> Circuit.thenDynamic "route-ticket" "1.0.0" (fun ticket -> ticket.Id) 2 buildChild
    |> Circuit.define "support-ticket-pipeline" "1.0.0"

[<EntryPoint>]
let main _ =
    task {
        let runtime = new CodeOnlyRuntime() :> ICircuitRuntime
        let options = RunOptions.Default.WithMaxConcurrency(2)

        let! collected = Circuit.collect runtime pipeline () options CancellationToken.None

        match collected.Outcome with
        | Failed failure ->
            eprintfn "Pipeline failed: %s" failure.Message
            return 1
        | Succeeded responses ->
            printfn "collect (completion order):"
            responses |> Seq.iter (fun response -> printfn "  %s" response.Value)

            let! ordered = Circuit.collectSourceOrder runtime pipeline () options CancellationToken.None
            printfn "collectSourceOrder: %s" (ordered.Value |> Seq.map _.Value |> String.concat ", ")

            printfn "stream:"
            let stream = Circuit.stream runtime pipeline () options CancellationToken.None
            let streamEnumerator = stream.GetAsyncEnumerator()

            try
                let mutable reading = true

                while reading do
                    let! more = streamEnumerator.MoveNextAsync().AsTask()
                    reading <- more

                    if more then
                        printfn "  %s" streamEnumerator.Current.Value
            finally
                streamEnumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            let! exactlyOne = Circuit.run runtime pipeline () options CancellationToken.None
            printfn "run enforces cardinality: %O" exactlyOne.Failure.Code

            let! live = Circuit.start runtime pipeline () options CancellationToken.None
            let events = live.Events.GetAsyncEnumerator()
            let mutable produced = 0

            try
                let mutable reading = true

                while reading do
                    let! more = events.MoveNextAsync().AsTask()
                    reading <- more

                    if more then
                        match events.Current with
                        | OutputProduced _ -> produced <- produced + 1
                        | RunCompleted _ -> reading <- false
                        | _ -> ()
            finally
                events.DisposeAsync().AsTask().GetAwaiter().GetResult()
                (live :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

            printfn "start observed %d structural outputs" produced
            return 0
    }
    |> fun work -> work.GetAwaiter().GetResult()
