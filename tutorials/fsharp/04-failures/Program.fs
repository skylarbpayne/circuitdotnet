module Circuit.Tutorial.Failures

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

let printFailure (failure: CircuitFailure) =
    match failure.Code with
    | CircuitFailureCode.Validation -> "The ticket or typed response did not satisfy its validation rules."
    | CircuitFailureCode.StructuredOutputUnsupported ->
        "The selected provider or model cannot guarantee this structured output."
    | CircuitFailureCode.Decode -> "The provider response could not be decoded as the typed output."
    | CircuitFailureCode.Provider -> "The provider could not complete the request."
    | CircuitFailureCode.Tool -> "A tool could not be resolved or completed safely."
    | CircuitFailureCode.ApprovalRequired -> "The run requires an approval response."
    | CircuitFailureCode.Skill -> "A requested skill could not be loaded or applied."
    | CircuitFailureCode.Workflow -> "A workflow operation could not complete."
    | CircuitFailureCode.CheckpointMismatch -> "Saved workflow state does not match the current definition."
    | CircuitFailureCode.Cancelled -> "The request was cancelled before completion."
    | _ -> "The request failed for an unrecognized reason."
    |> eprintfn "%s"

let runAsync (runtime: ICircuitRuntime) mode timeoutToken =
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
            if mode = "validation" then
                TicketInput(Subject = "", Message = "short")
            else
                TicketInput(
                    Subject = "Password reset email never arrived",
                    Message = "I requested a password reset twice, but no email has arrived. What should I try next?"
                )

        use cancelled = new CancellationTokenSource()

        let cancellationToken =
            if mode = "cancel" then
                cancelled.Cancel()
                cancelled.Token
            else
                timeoutToken

        let! runResult = Agent.run runtime agent signature ticket RunOptions.Default cancellationToken

        if runResult.Result.IsSuccess then
            let output = runResult.Result.Value
            printfn "Category: %s" output.Category
            printfn "Suggested reply: %s" output.SuggestedReply
            return 0
        else
            printFailure runResult.Result.Failure
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
            let mode =
                if args |> Array.contains "--validation" then "validation"
                elif args |> Array.contains "--cancel" then "cancel"
                else "live"

            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let runtime = MafRuntime(chatClient, MafRuntimeOptions()) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
            runAsync runtime mode timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
