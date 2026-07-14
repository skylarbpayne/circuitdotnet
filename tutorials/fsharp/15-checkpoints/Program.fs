module Tutorial

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp

type Ticket =
    { Id: string
      Review: bool
      DelayMs: int }

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
    [| { Id = "ticket-review"
         Review = true
         DelayMs = 0 }
       { Id = "ticket-active"
         Review = false
         DelayMs = 3000 } |]

let source =
    Circuit.keyedItems "checkpoint-tickets" "1.0.0" _.Id (fun (_: unit) -> tickets)

let child ticket =
    if ticket.Review then
        Circuit.approval "durable-review" "1.0.0" (fun (_: Ticket) ->
            ApprovalPrompt.Create("Durable review", ticket.Id))
        |> Circuit.thenStep (
            Circuit.code "review-result" "1.0.0" (fun context decision ->
                Task.FromResult(Response.succeed context $"{ticket.Id}:approved={decision.Approved}"))
        )
    else
        Circuit.code "slow-active-lane" "1.0.0" (fun context value ->
            task {
                do! Task.Delay(value.DelayMs, context.CancellationToken)
                return Response.succeed context $"{value.Id}:completed"
            })

let pipeline =
    source
    |> Circuit.thenDynamic "checkpoint-route" "1.0.0" _.Id 2 child
    |> Circuit.define "active-lane-checkpoint" "1.0.0"

let writeAtomic path (state: JsonElement) =
    let fullPath = Path.GetFullPath path
    Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
    let temporary = fullPath + $".{Guid.NewGuid():N}.tmp"

    try
        File.WriteAllText(temporary, state.GetRawText())
        File.Move(temporary, fullPath, true)
    finally
        if File.Exists temporary then
            File.Delete temporary

let createAsync runtime path =
    task {
        let! run = Circuit.start runtime pipeline () (RunOptions.Default.WithMaxConcurrency(2)) CancellationToken.None
        let enumerator = run.Events.GetAsyncEnumerator()

        try
            let mutable paused = false

            while not paused do
                let! more = enumerator.MoveNextAsync().AsTask()

                if not more then
                    failwith "Run ended before the review lane paused."

                match enumerator.Current with
                | ApprovalRequested request ->
                    printfn "Paused request %s while another lane remains active." request.RequestId
                    paused <- true
                | _ -> ()

            let! checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()

            if checkpoint.IsSuccess then
                writeAtomic path (checkpoint.Value.Serialize())
                printfn "Checkpoint written to %s; start a second process with resume." (Path.GetFullPath path)
                return 0
            else
                eprintfn "%s" checkpoint.Failure.Message
                return 1
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let resumeAsync runtime path approved =
    task {
        let fullPath = Path.GetFullPath path

        if not (File.Exists fullPath) then
            eprintfn "Checkpoint not found: %s" fullPath
            return 2
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText fullPath)
                let checkpoint = CircuitCheckpoint<string>.Deserialize(document.RootElement)
                let! run = Circuit.resume runtime pipeline checkpoint ResumeOptions.Default CancellationToken.None
                let enumerator = run.Events.GetAsyncEnumerator()

                try
                    let mutable terminal = false
                    let mutable failed = false

                    while not terminal do
                        let! more = enumerator.MoveNextAsync().AsTask()

                        if not more then
                            terminal <- true
                        else
                            match enumerator.Current with
                            | ApprovalRequested request ->
                                printfn "Restored approval %s from the serialized checkpoint." request.RequestId

                                let! accepted =
                                    run
                                        .RespondAsync(
                                            ApprovalResponse(request.RequestId, approved, "second process"),
                                            CancellationToken.None
                                        )
                                        .AsTask()

                                if not accepted.IsSuccess then
                                    failed <- true
                            | OutputProduced(_, response) when response.IsSuccess -> printfn "%s" response.Value
                            | RunCompleted response ->
                                failed <- failed || not response.IsSuccess
                                terminal <- true
                            | _ -> ()

                    return if failed then 1 else 0
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                    (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
            finally
                if File.Exists fullPath then
                    File.Delete fullPath
    }

[<EntryPoint>]
let main args =
    let runtime = CodeOnlyRuntime() :> ICircuitRuntime

    match args with
    | [| "create"; path |] -> createAsync runtime path |> _.GetAwaiter().GetResult()
    | [| "resume"; path; "--approve" |] -> resumeAsync runtime path true |> _.GetAwaiter().GetResult()
    | [| "resume"; path; "--reject" |] -> resumeAsync runtime path false |> _.GetAwaiter().GetResult()
    | _ ->
        eprintfn "Usage: create <path> | resume <path> --approve|--reject"
        2
