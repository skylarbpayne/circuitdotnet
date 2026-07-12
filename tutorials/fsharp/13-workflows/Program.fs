module Circuit.Tutorial.Workflows

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
type Classification() =
    [<property: Required; StringLength(40, MinimumLength = 3)>]
    member val Category = "" with get, set

    [<property: Range(1, 5)>]
    member val Urgency = 1 with get, set

[<AllowNullLiteral>]
type DraftInput() =
    [<property: Required; StringLength(40, MinimumLength = 3)>]
    member val Category = "" with get, set

    [<property: Range(1, 5)>]
    member val Urgency = 1 with get, set

[<AllowNullLiteral>]
type DraftOutput() =
    [<property: Required; StringLength(500, MinimumLength = 10)>]
    member val SuggestedReply = "" with get, set

let runAsync (runtime: IWorkflowRuntime) cancellationToken =
    task {
        let classifier =
            AgentDefinition.create
                "support.classifier"
                "1.0.0"
                "Ticket classifier"
                "Classify the support ticket and assign urgency from 1 to 5."

        let classificationSignature =
            Signature.create<TicketInput, Classification>
                "support.classification"
                "1.0.0"
                "Ticket classification"
                "Return category and urgency as structured output."

        let drafter =
            AgentDefinition.create
                "support.drafter"
                "1.0.0"
                "Response drafter"
                "Draft a concise support response appropriate to the classification."

        let draftSignature =
            Signature.create<DraftInput, DraftOutput>
                "support.workflow-draft"
                "1.0.0"
                "Workflow response draft"
                "Return one suggestedReply as structured output."

        let classificationStep =
            Workflow.agent "classify.ticket" classifier classificationSignature

        let adaptStep =
            Workflow.code "prepare.draft-input" (fun _ (classification: Classification) ->
                Task.FromResult(DraftInput(Category = classification.Category, Urgency = classification.Urgency)))

        let draftStep = Workflow.agent "draft.response" drafter draftSignature

        let definition =
            Workflow.define "support.response-workflow" "1.0.0" classificationStep
            |> Workflow.thenStep adaptStep
            |> Workflow.thenStep draftStep

        let issues = Workflow.validate definition

        if not (Seq.isEmpty issues) then
            for issue in issues do
                eprintfn "Workflow validation failed at %s (%s): %s" issue.NodeId issue.Code issue.Message

            return 1
        else
            printfn "Workflow validated: classify.ticket -> prepare.draft-input -> draft.response"

            let ticket =
                TicketInput(
                    Subject = "Password reset email never arrived",
                    Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
                )

            let! result = Workflow.run runtime definition ticket WorkflowRunOptions.Default cancellationToken

            if result.Result.IsSuccess then
                printfn "Draft: %s" result.Result.Value.SuggestedReply
                return 0
            else
                let failure = result.Result.Failure
                eprintfn "Workflow failed (%O): %s" failure.Code failure.Message
                return 1
    }

[<EntryPoint>]
let main _ =
    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    if String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) then
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    else
        try
            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> IWorkflowRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The workflow timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
