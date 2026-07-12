module Circuit.Tutorial.Validation

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
    [<property: Required; StringLength(120, MinimumLength = 3)>]
    member val Subject = "" with get, set

    [<property: Required; StringLength(2000, MinimumLength = 10)>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type TicketOutput() =
    [<property: Required; StringLength(40, MinimumLength = 3)>]
    member val Category = "" with get, set

    [<property: Required; StringLength(500, MinimumLength = 10)>]
    member val SuggestedReply = "" with get, set

let runAsync (runtime: ICircuitRuntime) invalid cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "Read the support ticket. Return a short category and a helpful suggested reply."

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let ticket =
            if invalid then
                TicketInput(Subject = "", Message = "short")
            else
                TicketInput(
                    Subject = "Password reset email never arrived",
                    Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
                )

        let! runResult = Agent.run runtime agent signature ticket RunOptions.Default cancellationToken

        if runResult.Result.IsSuccess then
            let output = runResult.Result.Value
            printfn "Category: %s" output.Category
            printfn "Suggested reply: %s" output.SuggestedReply
            return 0
        else
            let failure = runResult.Result.Failure

            if failure.Code = CircuitFailureCode.Validation then
                eprintfn "Validation rejected the typed contract before the run could continue: %s" failure.Message
            else
                eprintfn "Circuit could not complete the request (%O): %s" failure.Code failure.Message

            return 1
    }

[<EntryPoint>]
let main args =
    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    if String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) then
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    else
        try
            let invalid = args |> Array.contains "--invalid"
            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))

            runAsync runtime invalid timeout.Token
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
