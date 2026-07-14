module Circuit.Tutorial.Tools

open System
open System.ComponentModel.DataAnnotations
open System.Threading
open System.Threading.Tasks
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

[<AllowNullLiteral>]
type AccountLookupInput() =
    [<property: Required; RegularExpression("^[A-Z]{3}-[0-9]{4}$")>]
    member val AccountId = "" with get, set

[<AllowNullLiteral>]
type AccountLookupOutput() =
    [<property: Required; StringLength(20)>]
    member val Plan = "" with get, set

    [<property: Required; StringLength(20)>]
    member val Status = "" with get, set

let createLookupTool () =
    ToolDefinition.create<AccountLookupInput, AccountLookupOutput>
        "account.lookup"
        "1.0.0"
        "Look up the current plan and status for an account ID."
        (fun context input ->
            context.CancellationToken.ThrowIfCancellationRequested()

            Task.FromResult(
                AccountLookupOutput(
                    Plan =
                        (if input.AccountId = "ACM-2048" then
                             "Business"
                         else
                             "Standard"),
                    Status = "Active"
                )
            ))
    |> ToolDefinition.withApproval ApprovalMode.Never
    |> fun definition -> ResolvedTool.Create(definition, [ "account.read" ])

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "Use the account lookup tool for current account facts, then classify the ticket and draft a reply."
            |> AgentDefinition.withToolTags [ "account.read" ]

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let ticket =
            TicketInput(
                Subject = "Feature missing for ACM-2048",
                Message =
                    "Please check my current plan and explain whether my active account should have priority support."
            )

        let! result = Circuit.run runtime (Circuit.agent agent signature) ticket RunOptions.Default cancellationToken

        if result.IsSuccess then
            printfn "Category: %s" result.Value.Category
            printfn "Suggested reply: %s" result.Value.SuggestedReply
            return 0
        else
            let failure = result.Failure
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
            let options = MafRuntimeOptions()
            options.ToolResolvers <- [| StaticToolResolver([| createLookupTool () |]) :> IToolResolver |]
            let runtime = MafRuntime(chatClient, options) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
            runAsync runtime timeout.Token |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
