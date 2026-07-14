module Circuit.Tutorial.Approvals

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
type EscalationInput() =
    [<property: Required; RegularExpression("^[A-Z]{3}-[0-9]{4}$")>]
    member val AccountId = "" with get, set

    [<property: Required; StringLength(120, MinimumLength = 3)>]
    member val Reason = "" with get, set

[<AllowNullLiteral>]
type EscalationOutput() =
    [<property: Required; StringLength(30)>]
    member val Status = "" with get, set

let createEscalationTool () =
    ToolDefinition.create<EscalationInput, EscalationOutput>
        "ticket.escalate"
        "1.0.0"
        "Escalate an account's support ticket to a specialist queue."
        (fun context _input ->
            context.CancellationToken.ThrowIfCancellationRequested()
            Task.FromResult(EscalationOutput(Status = "queued-for-specialist")))
    |> ToolDefinition.withApproval ApprovalMode.Always
    |> fun definition -> ResolvedTool.Create(definition, [ "ticket.escalate" ])

let runAsync (runtime: ICircuitRuntime) approved cancellationToken =
    task {
        let agent =
            AgentDefinition.create
                "support.agent"
                "1.0.0"
                "Support assistant"
                "The customer explicitly requests escalation. Call the escalation tool, then draft a concise reply."
            |> AgentDefinition.withToolTags [ "ticket.escalate" ]

        let signature =
            Signature.create<TicketInput, TicketOutput>
                "support.reply"
                "1.0.0"
                "Support ticket reply"
                "Return category and suggestedReply as structured output."

        let ticket =
            TicketInput(
                Subject = "Escalate account ACM-2048",
                Message =
                    "Repeated password-reset emails have not arrived. Please escalate this account to a specialist."
            )

        let! run = Circuit.start runtime (Circuit.agent agent signature) ticket RunOptions.Default cancellationToken
        let enumerator = run.Events.GetAsyncEnumerator(cancellationToken)
        let mutable terminalCount = 0
        let mutable approvalCount = 0
        let mutable resultCode = 1
        let mutable keepReading = true

        try
            while keepReading do
                let! hasEvent = enumerator.MoveNextAsync().AsTask()

                if not hasEvent then
                    keepReading <- false
                else
                    let event = enumerator.Current

                    match event with
                    | ApprovalRequested request ->
                        approvalCount <- approvalCount + 1
                        printfn "Approval requested for tool '%s' (request %s)." request.ToolName request.RequestId
                        let allowThisRequest = approved && approvalCount = 1

                        let reason =
                            if approvalCount > 1 then
                                "additional escalation requests are not authorized"
                            elif allowThisRequest then
                                "authorized by tutorial operator"
                            else
                                "rejected by tutorial operator"

                        let! accepted =
                            run
                                .RespondAsync(
                                    ApprovalResponse(request.RequestId, allowThisRequest, reason),
                                    cancellationToken
                                )
                                .AsTask()

                        if not accepted.IsSuccess then
                            eprintfn "Approval response was rejected: %s" accepted.Failure.Message
                    | OutputProduced(_, response) when response.IsSuccess ->
                        let output = response.Value
                        printfn "Category: %s" output.Category
                        printfn "Suggested reply: %s" output.SuggestedReply
                        resultCode <- 0
                    | OutputProduced(_, response) ->
                        eprintfn
                            "Circuit could not complete the request (%O): %s"
                            response.Failure.Code
                            response.Failure.Message
                    | RunCompleted response ->
                        terminalCount <- terminalCount + 1

                        match response.Outcome with
                        | Failed failure -> eprintfn "Circuit run failed (%O): %s" failure.Code failure.Message
                        | Succeeded _ -> ()
                    | _ -> ()
        finally
            do enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            do (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

        if approvalCount < 1 || terminalCount <> 1 then
            eprintfn "Expected at least one approval request and exactly one terminal event."
            return 1
        else
            return resultCode
    }

[<EntryPoint>]
let main args =
    let decision =
        match args with
        | [| "--approve" |] -> ValueSome true
        | [| "--reject" |] -> ValueSome false
        | _ -> ValueNone

    let apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let model = Environment.GetEnvironmentVariable("OPENAI_MODEL")

    match decision with
    | ValueNone ->
        eprintfn "Run with exactly one decision: --approve or --reject."
        2
    | ValueSome _ when String.IsNullOrWhiteSpace(apiKey) || String.IsNullOrWhiteSpace(model) ->
        eprintfn "Set OPENAI_API_KEY and OPENAI_MODEL before running this chapter."
        2
    | ValueSome approved ->
        try
            let openAiClient = ChatClient(model, apiKey)
            use chatClient = openAiClient.AsIChatClient()
            let options = MafRuntimeOptions()
            options.ToolResolvers <- [| StaticToolResolver([| createEscalationTool () |]) :> IToolResolver |]
            let runtime = MafRuntime(chatClient, options) :> ICircuitRuntime
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45.0))

            runAsync runtime approved timeout.Token
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | :? OperationCanceledException ->
            eprintfn "The request timed out or was cancelled."
            1
        | _ ->
            eprintfn "Configuration or provider request failed. See your provider and account diagnostics."
            1
