module Circuit.Tutorial.Telemetry

open System
open System.ComponentModel.DataAnnotations
open System.Threading
open Circuit.Core
open Circuit.FSharp
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI
open OpenAI.Chat
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
                Message = "I requested a password reset twice, but no email has arrived."
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
    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    if String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) then
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    else
        try
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

            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, runtimeOptions) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Could not complete the provider request. Check the configured model and account diagnostics."
            1
