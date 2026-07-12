module Circuit.Tutorial.Sessions

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

let printResult label (result: RunResult<TicketOutput>) =
    if result.Result.IsSuccess then
        let output = result.Result.Value
        printfn "%s category: %s" label output.Category
        printfn "%s reply: %s" label output.SuggestedReply
        true
    else
        let failure = result.Result.Failure
        eprintfn "%s failed (%O): %s" label failure.Code failure.Message
        false

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "Remember the ticket conversation. Return a category and a concise suggested reply."

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let firstTicket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a reset twice. What should I try next?"
            )

        let! first = Agent.run runtime agent signature firstTicket RunOptions.Default cancellationToken

        if not (printResult "First response" first) then
            return 1
        else
            match first.Session with
            | ValueNone ->
                eprintfn "The provider adapter did not return a continuable session."
                return 1
            | ValueSome session ->
                let followUp =
                    TicketInput(
                        Subject = "Follow-up",
                        Message = "I checked spam and the address is correct. What is the next step for that ticket?"
                    )

                let options = RunOptions.Default |> RunOptions.withSession session
                let! second = Agent.run runtime agent signature followUp options cancellationToken
                return if printResult "Follow-up" second then 0 else 1
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
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
