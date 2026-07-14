module Circuit.Tutorial.Streaming

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

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "Classify the ticket and draft a short, helpful reply."

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let ticket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a reset twice. What should I try next?"
            )

        let definition = Circuit.agent agent signature
        let! run = Circuit.start runtime definition ticket RunOptions.Default cancellationToken
        let enumerator = run.Events.GetAsyncEnumerator(cancellationToken)
        let mutable terminalCount = 0
        let mutable deltaCharacters = 0
        let mutable completedOutput: Response<TicketOutput> voption = ValueNone
        let mutable failure: CircuitFailure voption = ValueNone

        try
            let mutable keepReading = true

            while keepReading do
                let! hasEvent = enumerator.MoveNextAsync().AsTask()

                if not hasEvent then
                    keepReading <- false
                else
                    match enumerator.Current with
                    | OutputDelta delta ->
                        deltaCharacters <- deltaCharacters + delta.Text.Length
                        printf "."
                    | OutputProduced(_, response) -> completedOutput <- ValueSome response
                    | RunCompleted response ->
                        terminalCount <- terminalCount + 1

                        match response.Outcome with
                        | Failed problem -> failure <- ValueSome problem
                        | Succeeded _ -> ()

                        keepReading <- false
                    | _ -> ()
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

        printfn "\nReceived %d provider-variable delta characters." deltaCharacters

        if terminalCount <> 1 then
            eprintfn "The stream ended without exactly one terminal event."
            return 1
        else
            match completedOutput, failure with
            | ValueSome response, _ when response.IsSuccess ->
                printfn "Category: %s" response.Value.Category
                printfn "Suggested reply: %s" response.Value.SuggestedReply
                return 0
            | ValueSome response, _ ->
                eprintfn
                    "Circuit could not complete the request (%O): %s"
                    response.Failure.Code
                    response.Failure.Message

                return 1
            | _, ValueSome problem ->
                eprintfn "Circuit could not complete the request (%O): %s" problem.Code problem.Message
                return 1
            | _ ->
                eprintfn "The terminal event did not contain a result."
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
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
