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
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace

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

        let! runResult = Circuit.run runtime (Circuit.agent agent signature) ticket RunOptions.Default cancellationToken

        if runResult.IsSuccess then
            let output = runResult.Value

            printfn
                "Run succeeded. Category: %s; suggested reply length: %d"
                output.Category
                output.SuggestedReply.Length

            return 0
        else
            let failure = runResult.Failure
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

                use tracerProvider =
                    Sdk.CreateTracerProviderBuilder().AddSource("CircuitDotNet").AddConsoleExporter().Build()

                use meterProvider =
                    Sdk.CreateMeterProviderBuilder().AddMeter("CircuitDotNet").AddConsoleExporter().Build()

                let observerOptions = OpenTelemetryRunObserverOptions()
                observerOptions.CapturePrompt <- false
                observerOptions.CaptureInput <- false
                observerOptions.CaptureOutput <- false
                observerOptions.CaptureToolArguments <- false

                let runtimeOptions = MafRuntimeOptions()

                runtimeOptions.Observers <- [| OpenTelemetryRunObserver(observerOptions) :> Circuit.IRunObserver |]

                let runtime = MafRuntime(chatClient, runtimeOptions) :> ICircuitRuntime
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
