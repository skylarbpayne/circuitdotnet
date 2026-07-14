module Tutorial

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

type Ticket = { Id: string; Kind: string }

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
        Task.FromException<RunResult<'Output>>(
            InvalidOperationException("This chapter uses graph approvals and code leaves.")
        )

    override _.SerializeSessionCoreAsync(_, _, _runOptions, _) = ValueTask<JsonElement>()
    override _.DeserializeSessionCoreAsync(_, _, _runOptions, _) = ValueTask<CircuitSession>()

let tickets: IReadOnlyList<Ticket> =
    [| { Id = "ticket-1"; Kind = "billing" }
       { Id = "ticket-2"; Kind = "security" }
       { Id = "ticket-3"; Kind = "general" } |]

let source = Circuit.keyedItems "tickets" "1.0.0" _.Id (fun (_: unit) -> tickets)

let child ticket =
    if ticket.Kind = "security" then
        Circuit.approval "security-review" "1.0.0" (fun (_: Ticket) ->
            ApprovalPrompt.Create("Security review", ticket.Id))
        |> Circuit.thenStep (
            Circuit.code "record-decision" "1.0.0" (fun context decision ->
                Task.FromResult(Response.succeed context $"{ticket.Id}:approved={decision.Approved}"))
        )
    else
        Circuit.value $"{ticket.Id}:automatic"

let pipeline =
    source
    |> Circuit.thenDynamic "lane-route" "1.0.0" _.Id 3 child
    |> Circuit.define "approval-pipeline" "1.0.0"

[<EntryPoint>]
let main args =
    let approve = not (args |> Array.contains "--reject")

    task {
        let runtime = CodeOnlyRuntime() :> ICircuitRuntime
        let! run = Circuit.start runtime pipeline () (RunOptions.Default.WithMaxConcurrency(3)) CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable terminal = false
        let outputs = ResizeArray<string>()

        try
            while not terminal do
                let! more = enumerator.MoveNextAsync().AsTask()

                if not more then
                    terminal <- true
                else
                    match enumerator.Current with
                    | OutputProduced(_, response) when response.IsSuccess ->
                        outputs.Add response.Value
                        printfn "completed lane: %s" response.Value
                    | ApprovalRequested request ->
                        printfn "review lane paused; %d other lane(s) already completed" outputs.Count

                        let! accepted =
                            run
                                .RespondAsync(
                                    ApprovalResponse(request.RequestId, approve, "tutorial operator"),
                                    CancellationToken.None
                                )
                                .AsTask()

                        if not accepted.IsSuccess then
                            eprintfn "%s" accepted.Failure.Message

                        let! replay =
                            run
                                .RespondAsync(
                                    ApprovalResponse(request.RequestId, approve, "replay"),
                                    CancellationToken.None
                                )
                                .AsTask()

                        printfn "single-use replay accepted: %b" replay.IsSuccess
                    | RunCompleted _ -> terminal <- true
                    | _ -> ()

            return if outputs.Count = 3 then 0 else 1
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }
    |> fun work -> work.GetAwaiter().GetResult()
