module Circuit.Tutorial.Checkpoints

open System
open System.ComponentModel.DataAnnotations
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI
open OpenAI.Chat

[<AllowNullLiteral>]
type TicketInput() =
    [<property: Required; StringLength(120)>]
    member val Subject = "" with get, set

    [<property: Required; StringLength(2000)>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type DraftOutput() =
    [<property: Required; StringLength(500)>]
    member val SuggestedReply = "" with get, set

let buildDefinition () =
    let drafter =
        AgentDefinition.create
            "support.checkpoint-drafter"
            "1.0.0"
            "Response drafter"
            "Draft a concise and safe response to the support ticket."

    let signature =
        Signature.create<TicketInput, DraftOutput>
            "support.checkpoint-draft"
            "1.0.0"
            "Checkpointed support draft"
            "Return one suggestedReply as structured output."

    let draftStep = Workflow.agent "draft.response" drafter signature

    let reviewStep =
        Workflow.request "review.response" (fun (draft: DraftOutput) ->
            ApprovalPrompt.Create("Review drafted response", draft.SuggestedReply))

    let decisionStep =
        Workflow.code "record.decision" (fun _ (response: ApprovalResponse) -> Task.FromResult response.Approved)

    Workflow.define "support.checkpoint-review" "1.0.0" draftStep
    |> Workflow.thenStep reviewStep
    |> Workflow.thenStep decisionStep

let createAsync (runtime: IWorkflowRuntime) path cancellationToken =
    task {
        let definition = buildDefinition ()
        let issues = Workflow.validate definition

        if not (Seq.isEmpty issues) then
            eprintfn "The checked-in workflow definition is invalid."
            return 1
        else
            let ticket =
                TicketInput(
                    Subject = "Password reset email never arrived",
                    Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
                )

            let! run = Workflow.start runtime definition ticket WorkflowRunOptions.Default cancellationToken

            let enumerator = run.Events.GetAsyncEnumerator(cancellationToken)

            try
                let mutable paused = false
                let mutable failed = false

                while not paused && not failed do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if not moved then
                        failed <- true
                    else
                        match enumerator.Current.Kind with
                        | RunEventKind.ApprovalRequested -> paused <- true
                        | RunEventKind.RunFailed -> failed <- true
                        | _ -> ()

                if failed then
                    eprintfn "Workflow did not reach the approval checkpoint."
                    return 1
                else
                    let! checkpoint = run.CreateCheckpointAsync(cancellationToken).AsTask()
                    let fullPath = Path.GetFullPath(path)
                    let directory = Path.GetDirectoryName(fullPath)
                    Directory.CreateDirectory(directory) |> ignore

                    let temporaryPath =
                        Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp")

                    try
                        File.WriteAllText(temporaryPath, checkpoint.Serialize().GetRawText())

                        if not (OperatingSystem.IsWindows()) then
                            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

                        File.Move(temporaryPath, fullPath, true)
                    finally
                        if File.Exists(temporaryPath) then
                            File.Delete(temporaryPath)

                    printfn "Checkpoint written atomically to: %s" fullPath
                    printfn "Stop this process, then use the documented resume command."
                    return 0
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let resumeAsync (runtime: IWorkflowRuntime) path approved cancellationToken =
    task {
        let fullPath = Path.GetFullPath(path)

        if not (File.Exists(fullPath)) then
            eprintfn "Checkpoint file not found: %s" fullPath
            return 2
        elif FileInfo(fullPath).Length > 1_048_576L then
            eprintfn "Checkpoint exceeds this tutorial's 1 MiB input limit."
            return 2
        else
            try
                try
                    use document = JsonDocument.Parse(File.ReadAllText(fullPath))
                    let checkpoint = WorkflowCheckpoint<bool>.Deserialize(document.RootElement)
                    let definition = buildDefinition ()
                    let issues = Workflow.validate definition

                    if not (Seq.isEmpty issues) then
                        eprintfn "The checked-in workflow definition is invalid."
                        return 1
                    else
                        let! run = Workflow.resume runtime definition checkpoint cancellationToken
                        let enumerator = run.Events.GetAsyncEnumerator(cancellationToken)

                        try
                            let mutable terminal = false
                            let mutable exitCode = 1

                            while not terminal do
                                let! moved = enumerator.MoveNextAsync().AsTask()

                                if not moved then
                                    terminal <- true
                                else
                                    let event = enumerator.Current

                                    match event.Kind with
                                    | RunEventKind.ApprovalRequested ->
                                        let request = event.Approval.Value
                                        printfn "Restored the pending review; checkpoint contents are not displayed."

                                        do!
                                            run
                                                .RespondAsync(
                                                    ApprovalResponse(
                                                        request.RequestId,
                                                        approved,
                                                        "tutorial operator decision"
                                                    ),
                                                    cancellationToken
                                                )
                                                .AsTask()
                                    | RunEventKind.RunCompleted ->
                                        printfn
                                            "Resumed workflow completed: %s"
                                            (if event.Value.Value then "approved" else "rejected")

                                        exitCode <- 0
                                        terminal <- true
                                    | RunEventKind.RunFailed ->
                                        let failure = event.Failure.Value
                                        eprintfn "Resume failed (%O): %s" failure.Code failure.Message
                                        terminal <- true
                                    | _ -> ()

                            return exitCode
                        finally
                            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                            (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
                with
                | :? JsonException
                | :? ArgumentException ->
                    eprintfn "The checkpoint envelope is malformed, unsupported, or incompatible."
                    return 1
            finally
                if File.Exists(fullPath) then
                    File.Delete(fullPath)
    }

[<EntryPoint>]
let main args =
    let command =
        match args with
        | [| "create"; path |] -> Some(path, None)
        | [| "resume"; path; "--approve" |] -> Some(path, Some true)
        | [| "resume"; path; "--reject" |] -> Some(path, Some false)
        | _ -> None

    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    match command with
    | None ->
        eprintfn "Usage: create <path> | resume <path> --approve|--reject"
        2
    | Some _ when String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) ->
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    | Some(path, decision) ->
        try
            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> IWorkflowRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60.0))

            match decision with
            | None -> createAsync runtime path timeout.Token
            | Some approved -> resumeAsync runtime path approved timeout.Token
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The workflow timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration, provider, checkpoint, or workflow operation failed."
            1
