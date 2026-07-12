module Circuit.Tutorial.SwitchProviders

open System
open System.ComponentModel.DataAnnotations
open System.Threading
open Azure
open Azure.AI.OpenAI
open Circuit.Core
open Circuit.FSharp
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI

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

type Provider =
    | OpenAI
    | AzureOpenAI

let parseProvider value =
    match value with
    | "openai" -> Ok OpenAI
    | "azure-openai" -> Ok AzureOpenAI
    | _ -> Error "Set CIRCUIT_PROVIDER to either openai or azure-openai."

let requiredEnvironment names =
    let values = names |> Array.map Environment.GetEnvironmentVariable

    if values |> Array.exists String.IsNullOrWhiteSpace then
        let missingConfiguration = String.Join(", ", names)
        Error $"Set these variables for the selected provider: {missingConfiguration}."
    else
        Ok values

let createChatClient provider =
    match provider with
    | OpenAI ->
        match requiredEnvironment [| "OPENAI_API_KEY"; "OPENAI_MODEL" |] with
        | Error message -> Error message
        | Ok values ->
            let apiKey, model = values[0], values[1]
            OpenAI.Chat.ChatClient(model, apiKey).AsIChatClient() |> Ok
    | AzureOpenAI ->
        match requiredEnvironment [| "AZURE_OPENAI_ENDPOINT"; "AZURE_OPENAI_DEPLOYMENT"; "AZURE_OPENAI_API_KEY" |] with
        | Error message -> Error message
        | Ok values ->
            let endpoint, deployment, apiKey = values[0], values[1], values[2]

            AzureOpenAIClient(Uri(endpoint), AzureKeyCredential(apiKey)).GetChatClient(deployment).AsIChatClient()
            |> Ok

let runAsync (runtime: ICircuitRuntime) cancellationToken =
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
    match Environment.GetEnvironmentVariable("CIRCUIT_PROVIDER") |> parseProvider with
    | Error message ->
        eprintfn "%s" message
        2
    | Ok provider ->
        try
            match createChatClient provider with
            | Error message ->
                eprintfn "%s" message
                2
            | Ok chatClient ->
                use chatClient = chatClient
                let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> ICircuitRuntime
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
                runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn
                "Could not complete the provider request. Check the selected provider configuration and account diagnostics."

            1
