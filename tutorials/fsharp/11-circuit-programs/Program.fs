module Tutorial

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

type Ticket = { Id: string; Subject: string }
type Classified = { Id: string; Category: string }

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

let classify =
    Circuit.code "classify-ticket" "1.0.0" (fun context ticket ->
        let category =
            if ticket.Subject.Contains("password", StringComparison.OrdinalIgnoreCase) then
                "identity"
            else
                "general"

        Task.FromResult(Response.succeed context { Id = ticket.Id; Category = category }))

let format =
    Circuit.code "format-ticket" "1.0.0" (fun context classified ->
        Task.FromResult(Response.succeed context $"{classified.Id}:{classified.Category}"))

let circuit =
    classify
    |> Circuit.thenStep format
    |> Circuit.define "static-support-circuit" "1.0.0"

[<EntryPoint>]
let main _ =
    task {
        let runtime = CodeOnlyRuntime() :> ICircuitRuntime

        let ticket =
            { Id = "ticket-1"
              Subject = "Password reset" }

        let! response = Circuit.run runtime circuit ticket RunOptions.Default CancellationToken.None

        if response.IsSuccess then
            printfn "%s" response.Value
            return 0
        else
            eprintfn "%s" response.Failure.Message
            return 1
    }
    |> fun work -> work.GetAwaiter().GetResult()
