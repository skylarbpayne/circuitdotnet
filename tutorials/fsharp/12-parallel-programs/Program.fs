module Circuit.Tutorial.ParallelPrograms

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
type Analysis() =
    [<property: Required; StringLength(20)>]
    member val Perspective = "" with get, set

    [<property: Required; StringLength(300)>]
    member val Finding = "" with get, set

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let ticket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
            )

        let signature =
            Signature.create<TicketInput, Analysis>
                "support.analysis"
                "1.0.0"
                "Ticket analysis"
                "Return the requested perspective name and one concise finding."

        let makeAgent id name instructions =
            AgentDefinition.create id "1.0.0" name instructions

        let programs =
            [ Circuit.call
                  (makeAgent
                      "support.sentiment"
                      "Sentiment analyst"
                      "Analyze customer sentiment. Set perspective to Sentiment.")
                  signature
                  ticket
              Circuit.call
                  (makeAgent "support.risk" "Risk analyst" "Analyze account and security risk. Set perspective to Risk.")
                  signature
                  ticket
              Circuit.call
                  (makeAgent
                      "support.routing"
                      "Routing analyst"
                      "Recommend the responsible support queue. Set perspective to Routing.")
                  signature
                  ticket ]

        let! result =
            Circuit.``parallel`` 2 programs
            |> Circuit.run runtime RunOptions.Default cancellationToken

        if result.Result.IsSuccess then
            printfn "Analyses (declaration order):"

            result.Result.Value
            |> List.iteri (fun index analysis -> printfn "%d. %s: %s" (index + 1) analysis.Perspective analysis.Finding)

            return 0
        else
            let failure = result.Result.Failure
            eprintfn "Parallel program failed (%O): %s" failure.Code failure.Message
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
            eprintfn "The analyses timed out or were cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
