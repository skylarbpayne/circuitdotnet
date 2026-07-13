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

let buildChild (ticket: Ticket) =
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
    |> Circuit.recover "recover-ticket" "1.0.0" (fun failure -> $"{ticket.Id}:recovered:{failure.Code}")

let pipeline =
    source
    |> Circuit.thenDynamic "route-ticket" "1.0.0" (fun ticket -> ticket.Id) 2 buildChild
    |> Circuit.define "support-ticket-pipeline" "1.0.0"

[<EntryPoint>]
let main _ =
    task {
        let runtime = new CodeOnlyRuntime() :> ICircuitRuntime

        let! result =
            Circuit.collect runtime pipeline () (RunOptions.Default.WithMaxConcurrency(2)) CancellationToken.None

        match result.Outcome with
        | Failed failure ->
            eprintfn "Pipeline failed: %s" failure.Message
            return 1
        | Succeeded responses ->
            for response in responses do
                printfn "%s" response.Value

            return 0
    }
    |> fun work -> work.GetAwaiter().GetResult()
