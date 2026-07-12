module Circuit.Tutorial.BuildingBlocks

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
type TicketOutput() =
    [<property: Required; StringLength(40)>]
    member val Category = "" with get, set

    [<property: Required; StringLength(500)>]
    member val SuggestedReply = "" with get, set

let runAsync (chatClient: IChatClient) cancellationToken =
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

        let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> ICircuitRuntime

        let ticket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
            )

        printfn "Provider boundary: IChatClient"
        printfn "Agent definition: %s" agent.Id.Value
        printfn "Signature: %s" signature.Id.Value
        printfn "Runtime boundary: ICircuitRuntime"

        let! runResult = Agent.run runtime agent signature ticket RunOptions.Default cancellationToken

        if runResult.Result.IsSuccess then
            let output = runResult.Result.Value
            printfn "Category: %s" output.Category
            printfn "Suggested reply: %s" output.SuggestedReply
            return 0
        else
            let failure = runResult.Result.Failure
            eprintfn "Circuit could not complete the request (%O): %s" failure.Code failure.Message
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
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
            runAsync chatClient timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
