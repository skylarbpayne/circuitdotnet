module Circuit.Tutorial.CircuitPrograms

open System
open System.ComponentModel.DataAnnotations
open System.Threading
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
type Classification() =
    [<property: Required; StringLength(40)>]
    member val Category = "" with get, set

    [<property: Range(1, 5)>]
    member val Urgency = 1 with get, set

[<AllowNullLiteral>]
type DraftInput() =
    [<property: Required; StringLength(120)>]
    member val Subject = "" with get, set

    [<property: Required; StringLength(2000)>]
    member val Message = "" with get, set

    [<property: Required; StringLength(40)>]
    member val Category = "" with get, set

    [<property: Range(1, 5)>]
    member val Urgency = 1 with get, set

[<AllowNullLiteral>]
type DraftOutput() =
    [<property: Required; StringLength(500)>]
    member val SuggestedReply = "" with get, set

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let classifier =
            AgentDefinition.create
                "support.classifier"
                "1.0.0"
                "Ticket classifier"
                "Classify the support ticket and assign urgency from 1 (low) to 5 (critical)."

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
                "Draft a concise, safe support response using the supplied classification."

        let draftSignature =
            Signature.create<DraftInput, DraftOutput>
                "support.draft"
                "1.0.0"
                "Support response draft"
                "Return one suggestedReply as structured output."

        let ticket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
            )

        let program =
            circuit {
                let! classification = Circuit.call classifier classificationSignature ticket

                let draftInput =
                    DraftInput(
                        Subject = ticket.Subject,
                        Message = ticket.Message,
                        Category = classification.Category,
                        Urgency = classification.Urgency
                    )

                let! draft = Circuit.call drafter draftSignature draftInput
                return classification, draft
            }

        let! result = Circuit.run runtime RunOptions.Default cancellationToken program

        if result.Result.IsSuccess then
            let classification, draft = result.Result.Value
            printfn "Run: %O" result.RunId
            printfn "Category: %s (urgency %d)" classification.Category classification.Urgency
            printfn "Draft: %s" draft.SuggestedReply
            return 0
        else
            let failure = result.Result.Failure
            eprintfn "Circuit program failed (%O): %s" failure.Code failure.Message
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
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The program timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
