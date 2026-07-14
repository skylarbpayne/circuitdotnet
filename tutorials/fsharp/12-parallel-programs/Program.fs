module Tutorial

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

type Ticket = { Id: string; DelayMs: int }

type CodeOnlyRuntime() =
    inherit CircuitRuntime()

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
        Task.FromException<RunResult<'Output>>(InvalidOperationException("This chapter uses code leaves only."))

    override _.SerializeSessionCoreAsync(_, _, _runOptions, _) = ValueTask<JsonElement>()
    override _.DeserializeSessionCoreAsync(_, _, _runOptions, _) = ValueTask<CircuitSession>()

let tickets: IReadOnlyList<Ticket> =
    [| { Id = "ticket-1"; DelayMs = 80 }
       { Id = "ticket-2"; DelayMs = 10 }
       { Id = "ticket-3"; DelayMs = 25 } |]

let source = Circuit.keyedItems "tickets" "1.0.0" _.Id (fun (_: unit) -> tickets)

let processTicket =
    Circuit.code "process-ticket" "1.0.0" (fun context ticket ->
        task {
            do! Task.Delay(ticket.DelayMs, context.CancellationToken)
            return Response.succeed context ticket.Id
        })

let handoff =
    Circuit.code "completion-handoff" "1.0.0" (fun context id ->
        printfn "downstream accepted %s" id
        Task.FromResult(Response.succeed context id))

let pipeline =
    source
    |> Circuit.thenStep processTicket
    |> Circuit.thenStep handoff
    |> Circuit.define "bounded-pipeline" "1.0.0"

[<EntryPoint>]
let main _ =
    task {
        let runtime = CodeOnlyRuntime() :> ICircuitRuntime

        let! result =
            Circuit.collect runtime pipeline () (RunOptions.Default.WithMaxConcurrency(2)) CancellationToken.None

        if result.IsSuccess then
            printfn "completion order: %s" (result.Value |> Seq.map _.Value |> String.concat ", ")
            return 0
        else
            eprintfn "%s" result.Failure.Message
            return 1
    }
    |> fun work -> work.GetAwaiter().GetResult()
