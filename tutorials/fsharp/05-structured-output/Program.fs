module Circuit.Tutorial.StructuredOutput

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

let runAsync (runtime: ICircuitRuntime) runOptions cancellationToken =
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
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
            )

        let! runResult = Circuit.run runtime (Circuit.agent agent signature) ticket runOptions cancellationToken

        if runResult.IsSuccess then
            let output = runResult.Value
            printfn "Category: %s" output.Category
            printfn "Suggested reply: %s" output.SuggestedReply
            return 0
        else
            let failure = runResult.Failure
            eprintfn "Circuit could not complete the request (%O): %s" failure.Code failure.Message
            return 1
    }

let execute (apiKey: string) (model: string) (repair: bool) =
    let primaryOpenAiClient = ChatClient(model, apiKey)
    use primaryClient = primaryOpenAiClient.AsIChatClient()
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))

    if repair then
        let secondaryOpenAiClient = ChatClient(model, apiKey)
        use secondaryClient = secondaryOpenAiClient.AsIChatClient()
        let runtimeOptions = MafRuntimeOptions()
        runtimeOptions.SecondaryStructuredOutputClient <- ValueSome secondaryClient
        let runtime = MafRuntime(primaryClient, runtimeOptions) :> ICircuitRuntime

        let runOptions =
            RunOptions.Default
            |> RunOptions.withStructuredOutputPolicy StructuredOutputPolicy.AllowSecondaryModelRepair

        printfn "Structured output policy: secondary repair allowed"

        runAsync runtime runOptions timeout.Token
        |> fun work -> work.GetAwaiter().GetResult()
    else
        let runtime = MafRuntime(primaryClient, MafRuntimeOptions()) :> ICircuitRuntime
        printfn "Structured output policy: native only"

        runAsync runtime RunOptions.Default timeout.Token
        |> fun work -> work.GetAwaiter().GetResult()

[<EntryPoint>]
let main args =
    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    if String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) then
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    else
        try
            execute apiKey model (args |> Array.contains "--repair")
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
