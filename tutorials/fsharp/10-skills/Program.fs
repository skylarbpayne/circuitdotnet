module Circuit.Tutorial.Skills

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

let supportPolicy =
    SkillReference.Create(
        "skill.support-policy",
        "1.0.0",
        "Versioned support response guidance.",
        SkillSource.CreateInline(
            "For password-reset tickets, suggest checking spam and the account address. "
            + "Never ask for a password or claim that an escalation has occurred."
        )
    )

let runAsync (runtime: ICircuitRuntime) cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "Apply the attached support-policy skill when classifying the ticket and drafting the reply."
            |> AgentDefinition.withSkills [ supportPolicy ]

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let ticket =
            TicketInput(
                Subject = "Password reset email never arrived",
                Message = "I requested a reset twice, but no email has arrived."
            )

        let! result = Agent.run runtime agent signature ticket RunOptions.Default cancellationToken

        if result.Result.IsSuccess then
            printfn "Policy skill: %s@%O" supportPolicy.Id.Value supportPolicy.Version
            printfn "Category: %s" result.Result.Value.Category
            printfn "Suggested reply: %s" result.Result.Value.SuggestedReply
            return 0
        else
            let failure = result.Result.Failure
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

            options.SkillResolvers <-
                [| StaticSkillResolver([| ResolvedSkill.Create(supportPolicy) |]) :> ISkillResolver |]

            options.SkillScriptRunner <- ValueNone
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
