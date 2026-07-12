module Circuit.Tutorial.HumanReview

open System
open System.ComponentModel.DataAnnotations
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI
open OpenAI.Chat

[<AllowNullLiteral>]
type TicketInput() =
    [<property: Required; StringLength(120, MinimumLength = 3)>]
    member val Subject = "" with get, set

    [<property: Required; StringLength(2000, MinimumLength = 10)>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type DraftOutput() =
    [<property: Required; StringLength(500, MinimumLength = 10)>]
    member val SuggestedReply = "" with get, set

let runAsync (runtime: IWorkflowRuntime) approved cancellationToken =
    task {
        let drafter =
            AgentDefinition.create
                "support.review-drafter"
                "1.0.0"
                "Response drafter"
                "Draft a concise and safe response to the support ticket."

        let signature =
            Signature.create<TicketInput, DraftOutput>
                "support.review-draft"
                "1.0.0"
                "Reviewed support draft"
                "Return one suggestedReply as structured output."

        let draftStep = Workflow.agent "draft.response" drafter signature

        let reviewStep =
            Workflow.request "review.response" (fun (draft: DraftOutput) ->
                ApprovalPrompt.Create("Review drafted response", draft.SuggestedReply))

        let decisionStep =
            Workflow.code "record.decision" (fun _ (response: ApprovalResponse) -> Task.FromResult response.Approved)

        let definition =
            Workflow.define "support.human-review" "1.0.0" draftStep
            |> Workflow.thenStep reviewStep
            |> Workflow.thenStep decisionStep

        let issues = Workflow.validate definition

        if not (Seq.isEmpty issues) then
            for issue in issues do
                eprintfn "Workflow validation failed at %s (%s): %s" issue.NodeId issue.Code issue.Message

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
                            printfn "Approval requested by step: %s" request.ToolName
                            printfn "Draft content is sensitive; inspect it only in an authorized review UI."

                            try
                                do!
                                    run
                                        .RespondAsync(
                                            ApprovalResponse.Create("not-the-request-token", approved),
                                            cancellationToken
                                        )
                                        .AsTask()

                                eprintfn "Unexpectedly accepted a mismatched approval token."
                            with _ ->
                                printfn "Mismatched token rejected."

                            let response =
                                ApprovalResponse(request.RequestId, approved, "tutorial operator decision")

                            do! run.RespondAsync(response, cancellationToken).AsTask()

                            try
                                do! run.RespondAsync(response, cancellationToken).AsTask()
                                eprintfn "Unexpectedly accepted a reused approval token."
                            with _ ->
                                printfn "Reused token rejected."
                        | RunEventKind.RunCompleted ->
                            printfn "Terminal decision: %s" (if event.Value.Value then "approved" else "rejected")
                            exitCode <- 0
                            terminal <- true
                        | RunEventKind.RunFailed ->
                            let failure = event.Failure.Value
                            eprintfn "Workflow failed (%O): %s" failure.Code failure.Message
                            terminal <- true
                        | _ -> ()

                return exitCode
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<EntryPoint>]
let main args =
    let decision =
        match args with
        | [| "--approve" |] -> Some true
        | [| "--reject" |] -> Some false
        | _ -> None

    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    match decision with
    | None ->
        eprintfn "Run this chapter with exactly one decision: --approve or --reject."
        2
    | Some _ when String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) ->
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    | Some approved ->
        try
            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> IWorkflowRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60.0))

            runAsync runtime approved timeout.Token
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The workflow timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration, provider, or approval continuation failed."
            1
