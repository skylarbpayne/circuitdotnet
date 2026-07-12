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

        let stream =
            runtime.RunStreamingAsync(agent, signature, ticket, RunOptions.Default, cancellationToken)

        let enumerator = stream.GetAsyncEnumerator(cancellationToken)
        let mutable lastSequence = -1L
        let mutable terminalCount = 0
        let mutable deltaCharacters = 0
        let mutable completedOutput = ValueNone
        let mutable failure = ValueNone

        try
            let mutable keepReading = true

            while keepReading do
                let! hasEvent = enumerator.MoveNextAsync().AsTask()

                if not hasEvent then
                    keepReading <- false
                else
                    let event = enumerator.Current

                    if event.Sequence <= lastSequence then
                        invalidOp "The runtime emitted a non-monotonic event sequence."

                    lastSequence <- event.Sequence

                    match event.Kind with
                    | RunEventKind.OutputDelta ->
                        match event.TextDelta with
                        | ValueSome delta ->
                            // Deltas can contain customer data, so report only progress here.
                            deltaCharacters <- deltaCharacters + delta.Length
                            printf "."
                        | ValueNone -> ()
                    | RunEventKind.RunCompleted ->
                        terminalCount <- terminalCount + 1
                        completedOutput <- event.Value
                    | RunEventKind.RunFailed ->
                        terminalCount <- terminalCount + 1
                        failure <- event.Failure
                    | _ -> ()
        finally
            do enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        printfn "\nReceived %d provider-variable delta characters." deltaCharacters

        if terminalCount <> 1 then
            eprintfn "The stream ended without exactly one terminal event."
            return 1
        else
            match completedOutput, failure with
            | ValueSome output, _ ->
                printfn "Category: %s" output.Category
                printfn "Suggested reply: %s" output.SuggestedReply
                return 0
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
