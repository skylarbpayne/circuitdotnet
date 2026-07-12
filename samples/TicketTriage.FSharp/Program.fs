open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core
open Circuit.FSharp
open Circuit.MicrosoftAgentFramework
open Circuit.Testing
open Microsoft.Extensions.AI
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace

let JsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

[<Literal>]
let MaxParallelBranches = 2

[<Literal>]
let ChatClientFactoryAssemblyEnv = "CIRCUIT_SAMPLE_CHAT_CLIENT_FACTORY_ASSEMBLY"

[<Literal>]
let ChatClientFactoryTypeEnv = "CIRCUIT_SAMPLE_CHAT_CLIENT_FACTORY_TYPE"

[<Literal>]
let ModelIdEnv = "CIRCUIT_SAMPLE_MODEL_ID"

[<AllowNullLiteral>]
type TicketInput() =
    member val TicketId = "" with get, set
    member val CustomerId = "" with get, set
    member val Subject = "" with get, set
    member val Body = "" with get, set
    member val RequestedPriority = "" with get, set

[<AllowNullLiteral>]
type TicketClassification() =
    member val Category = "" with get, set
    member val Severity = "" with get, set
    member val RecommendedQueue = "" with get, set
    member val NeedsEscalation = false with get, set
    member val Reason = "" with get, set

[<AllowNullLiteral>]
type CustomerLookupRequest() =
    member val CustomerId = "" with get, set

[<AllowNullLiteral>]
type CustomerRecord() =
    member val CustomerId = "" with get, set
    member val Name = "" with get, set
    member val Plan = "" with get, set
    member val IsVip = false with get, set
    member val OpenIncidentCount = 0 with get, set

[<AllowNullLiteral>]
type EscalationRequest() =
    member val TicketId = "" with get, set
    member val Queue = "" with get, set
    member val Reason = "" with get, set
    member val AnalystSummary = "" with get, set

[<AllowNullLiteral>]
type EscalationReceipt() =
    member val EscalationId = "" with get, set
    member val Queue = "" with get, set
    member val Status = "" with get, set

[<AllowNullLiteral>]
type SupportPolicy() =
    member val Queue = "" with get, set
    member val EscalateSeverities = Array.empty<string> with get, set
    member val VipAlwaysEscalate = false with get, set
    member val Guidance = Array.empty<string> with get, set

type CustomerSummary =
    { Customer: CustomerRecord
      SummaryText: string }

type SampleOptions =
    { Live: bool
      EnableOpenTelemetry: bool
      ShowHelp: bool }

module SampleOptions =
    let parse (args: string array) =
        args
        |> Array.fold
            (fun state arg ->
                match arg with
                | "--live" -> { state with Live = true }
                | "--otel" ->
                    { state with
                        EnableOpenTelemetry = true }
                | "-h"
                | "--help" -> { state with ShowHelp = true }
                | other -> invalidArg "args" $"Unknown argument '{other}'. Use --help for usage.")
            { Live = false
              EnableOpenTelemetry = false
              ShowHelp = false }

    let printHelp () =
        printfn "Usage: dotnet run --project samples/TicketTriage.FSharp [--live] [--otel]"
        printfn "Default mode is offline and uses ScriptedRuntime."
        printfn "Live mode loads a provider-specific IChatClient plugin from the environment."

type CompositeDisposable(disposables: IDisposable list) =
    interface IDisposable with
        member _.Dispose() =
            disposables |> List.iter (fun disposable -> disposable.Dispose())

type SampleRuntime =
    { Runtime: ICircuitRuntime
      Cleanup: IDisposable }

type SampleScenario =
    { Ticket: TicketInput
      Agent: AgentDefinition
      Signature: Signature<TicketInput, TicketClassification>
      CustomerLookupTool: ToolDefinition<CustomerLookupRequest, CustomerRecord>
      EscalationTool: ToolDefinition<EscalationRequest, EscalationReceipt>
      SupportSkill: SkillReference
      SupportPolicyResourcePath: string
      LookupCustomerAsync: CustomerLookupRequest -> CancellationToken -> Task<CustomerRecord>
      EscalateAsync: EscalationRequest -> CancellationToken -> Task<EscalationReceipt> }

module SampleScenario =
    let private findRepoRoot () =
        let rec loop (current: DirectoryInfo) =
            if isNull current then
                invalidOp "Could not locate the repository root from the sample output directory."

            let slnPath = Path.Combine(current.FullName, "CircuitDotNet.slnx")

            if File.Exists slnPath then
                current.FullName
            else
                loop current.Parent

        loop (DirectoryInfo(AppContext.BaseDirectory))

    let create () =
        let repoRoot = findRepoRoot ()

        let skillRoot =
            Path.Combine(repoRoot, "samples", "TicketTriage.Shared", "SupportPolicySkill")

        let resourcePath = Path.Combine(skillRoot, "references", "escalation-policy.json")

        let supportSkill =
            SkillReference.Create(
                "skill.support-policy",
                "1.0.0",
                "Ticket-triage escalation guidance.",
                SkillSource.CreateFile(skillRoot)
            )

        let lookupCustomerAsync (request: CustomerLookupRequest) (_ct: CancellationToken) =
            Task.FromResult(
                CustomerRecord(
                    CustomerId = request.CustomerId,
                    Name = "Northwind Solar",
                    Plan = "Enterprise",
                    IsVip = true,
                    OpenIncidentCount = 2
                )
            )

        let escalateAsync (request: EscalationRequest) (_ct: CancellationToken) =
            Task.FromResult(EscalationReceipt(EscalationId = "ESC-9001", Queue = request.Queue, Status = "queued"))

        let customerLookupTool =
            ToolDefinition.create<CustomerLookupRequest, CustomerRecord>
                "customer.lookup"
                "1.0.0"
                "Read-only customer lookup."
                (fun _context input -> lookupCustomerAsync input CancellationToken.None)
            |> ToolDefinition.withApproval ApprovalMode.Never

        let escalationTool =
            ToolDefinition.create<EscalationRequest, EscalationReceipt>
                "ticket.escalate"
                "1.0.0"
                "Write an escalation request."
                (fun _context input -> escalateAsync input CancellationToken.None)
            |> ToolDefinition.withApproval ApprovalMode.Always

        let agent =
            AgentDefinition.create
                "ticket-triage.agent"
                "1.0.0"
                "Ticket Triage"
                "Classify the incoming support ticket and return only the structured result."
            |> AgentDefinition.withSkills [ supportSkill ]

        let signature =
            Signature.create<TicketInput, TicketClassification>
                "ticket-triage.signature"
                "1.0.0"
                "Ticket triage"
                "Return category, severity, recommendedQueue, needsEscalation, and a brief reason."

        let ticket =
            TicketInput(
                TicketId = "TICKET-1024",
                CustomerId = "CUST-2048",
                Subject = "Card charged twice after enterprise upgrade",
                Body =
                    "I upgraded the account today, our finance team spotted a duplicate charge, and the rollout is blocked until billing fixes it.",
                RequestedPriority = "high"
            )

        { Ticket = ticket
          Agent = agent
          Signature = signature
          CustomerLookupTool = customerLookupTool
          EscalationTool = escalationTool
          SupportSkill = supportSkill
          SupportPolicyResourcePath = resourcePath
          LookupCustomerAsync = lookupCustomerAsync
          EscalateAsync = escalateAsync }

    let validate (scenario: SampleScenario) =
        if scenario.CustomerLookupTool.Approval <> ApprovalMode.Never then
            invalidOp "The customer lookup tool must be configured with ApprovalMode.Never."

        if scenario.EscalationTool.Approval <> ApprovalMode.Always then
            invalidOp "The escalation tool must be configured with ApprovalMode.Always."

        let skillRoot =
            Path.GetDirectoryName(Path.GetDirectoryName(scenario.SupportPolicyResourcePath))

        if File.Exists(Path.Combine(skillRoot, "run.py")) then
            invalidOp "The support-policy skill must not include a script."

        let resourceFiles =
            Directory.GetFiles(Path.Combine(skillRoot, "references"), "*", SearchOption.AllDirectories)

        if resourceFiles.Length <> 1 then
            invalidOp "The support-policy skill must contain exactly one resource file."

module RuntimeFactory =
    let private tryCreateChatClientFromEnvironment () =
        let assemblyPath = Environment.GetEnvironmentVariable(ChatClientFactoryAssemblyEnv)
        let typeName = Environment.GetEnvironmentVariable(ChatClientFactoryTypeEnv)

        if String.IsNullOrWhiteSpace assemblyPath || String.IsNullOrWhiteSpace typeName then
            None
        else
            let assembly = Assembly.LoadFrom(Path.GetFullPath assemblyPath)
            let factoryType = assembly.GetType(typeName, throwOnError = true)

            let method =
                factoryType.GetMethod("CreateFromEnvironment", BindingFlags.Public ||| BindingFlags.Static)

            if isNull method then
                invalidOp $"{factoryType.FullName} must define public static IChatClient? CreateFromEnvironment()."

            if not (typeof<IChatClient>.IsAssignableFrom method.ReturnType) then
                invalidOp $"{factoryType.FullName}.CreateFromEnvironment() must return IChatClient."

            method.Invoke(null, [||]) :?> IChatClient |> Option.ofObj

    let private createTelemetry liveMode =
        let tracerProvider =
            Sdk.CreateTracerProviderBuilder().AddSource("CircuitDotNet").AddConsoleExporter().Build()

        let meterProvider =
            Sdk.CreateMeterProviderBuilder().AddMeter("CircuitDotNet").AddConsoleExporter().Build()

        if liveMode then
            printfn "OpenTelemetry console export is enabled for Circuit runtime spans and metrics."
        else
            printfn "OpenTelemetry console export is enabled; live runtime spans appear only when using --live."

        new CompositeDisposable([ tracerProvider :> IDisposable; meterProvider :> IDisposable ]) :> IDisposable

    let create (options: SampleOptions) =
        let telemetry =
            if options.EnableOpenTelemetry then
                Some(createTelemetry options.Live)
            else
                None

        if options.EnableOpenTelemetry && not options.Live then
            printfn
                "OpenTelemetry console export is enabled, but the offline ScriptedRuntime does not emit observer callbacks."

        if options.Live then
            match tryCreateChatClientFromEnvironment () with
            | None ->
                telemetry |> Option.iter (fun value -> value.Dispose())
                printfn "Live mode skipped: no chat-client plugin or credentials were supplied."

                printfn
                    $"Set {ChatClientFactoryAssemblyEnv} and {ChatClientFactoryTypeEnv} to a provider plugin that exposes public static IChatClient? CreateFromEnvironment()."

                None
            | Some chatClient ->
                let runtimeOptions = MafRuntimeOptions()
                let modelId = Environment.GetEnvironmentVariable(ModelIdEnv)

                if not (String.IsNullOrWhiteSpace modelId) then
                    runtimeOptions.DefaultModelId <- ValueSome modelId

                if options.EnableOpenTelemetry then
                    runtimeOptions.Observers <- [| OpenTelemetryRunObserver() :> Circuit.IRunObserver |]

                let cleanupDisposables = ResizeArray<IDisposable>()
                telemetry |> Option.iter (fun value -> cleanupDisposables.Add value)
                cleanupDisposables.Add(chatClient)

                Some
                    { Runtime = MafRuntime(chatClient, runtimeOptions) :> ICircuitRuntime
                      Cleanup = new CompositeDisposable(cleanupDisposables |> Seq.toList) :> IDisposable }
        else
            let runtime =
                ScriptedRuntime(
                    [| ScriptedResponses.Stream(
                           [| "{\"category\":\"billing\",\"severity\":\"high\","
                              "\"recommendedQueue\":\"billing-escalations\",\"needsEscalation\":true,"
                              "\"reason\":\"Duplicate billing is blocking an enterprise customer rollout.\"}" |]
                       ) |]
                )
                :> ICircuitRuntime

            let cleanupDisposables =
                match telemetry with
                | Some value -> [ value ]
                | None -> []

            Some
                { Runtime = runtime
                  Cleanup = new CompositeDisposable(cleanupDisposables) :> IDisposable }

let printSafeEvent (event: RunEvent<TicketClassification>) =
    let detail =
        match event.Kind with
        | RunEventKind.OutputDelta -> " (delta omitted)"
        | RunEventKind.RunCompleted -> " (structured result received)"
        | RunEventKind.RunFailed -> " (failure message sanitized by Circuit)"
        | _ -> ""

    printfn "event[%d] %O%s" event.Sequence event.Kind detail

let classifyAsync
    (runtime: ICircuitRuntime)
    (scenario: SampleScenario)
    (options: SampleOptions)
    (cancellationToken: CancellationToken)
    =
    task {
        if options.Live then
            printfn "Running live structured classification..."
        else
            printfn "Running offline structured classification with ScriptedRuntime..."

        printfn "Read tool approval: %O" scenario.CustomerLookupTool.Approval
        printfn "Write tool approval: %O" scenario.EscalationTool.Approval
        printfn "Skill: %s@%O (resource-only)" scenario.SupportSkill.Id.Value scenario.SupportSkill.Version

        let events = ResizeArray<RunEvent<TicketClassification>>()

        let stream =
            runtime.RunStreamingAsync(
                scenario.Agent,
                scenario.Signature,
                scenario.Ticket,
                RunOptions.Default,
                cancellationToken
            )

        let enumerator = stream.GetAsyncEnumerator(cancellationToken)
        let mutable classification = None

        try
            let mutable keepGoing = true

            while keepGoing do
                let! moved = enumerator.MoveNextAsync().AsTask()

                if moved then
                    let event = enumerator.Current
                    events.Add event
                    printSafeEvent event

                    match event.Kind with
                    | RunEventKind.RunCompleted -> classification <- Some event.Value.Value
                    | RunEventKind.RunFailed ->
                        let failure = event.Failure.Value
                        raise (InvalidOperationException failure.Message)
                    | _ -> ()
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        RunAssertions.AssertMonotonicSequence(events)
        RunAssertions.AssertTerminalEventCount(events, 1)

        let value =
            match classification with
            | Some value -> value
            | None ->
                raise (
                    InvalidOperationException "The streamed run completed without a structured classification value."
                )

        return value
    }

let runBoundedParallelAsync left right cancellationToken =
    task {
        if MaxParallelBranches < 2 then
            invalidOp "This sample expects room for two parallel branches."

        let leftTask = left cancellationToken
        let rightTask = right cancellationToken
        do! Task.WhenAll([| leftTask :> Task; rightTask :> Task |])
        let! leftValue = leftTask
        let! rightValue = rightTask
        return leftValue, rightValue
    }

let summarizeCustomerAsync
    (scenario: SampleScenario)
    (classification: TicketClassification)
    (cancellationToken: CancellationToken)
    =
    task {
        let request = CustomerLookupRequest(CustomerId = scenario.Ticket.CustomerId)
        let! customer = scenario.LookupCustomerAsync request cancellationToken

        let summary =
            $"{customer.Name} is on the {customer.Plan} plan, VIP={customer.IsVip.ToString().ToLowerInvariant()}, open incidents={customer.OpenIncidentCount}. Classification suggests {classification.Category}/{classification.Severity}."

        return
            { Customer = customer
              SummaryText = summary }
    }

let loadPolicyAsync resourcePath cancellationToken =
    task {
        use stream = File.OpenRead resourcePath
        let! policy = JsonSerializer.DeserializeAsync<SupportPolicy>(stream, JsonOptions, cancellationToken)

        let value =
            match policy with
            | null ->
                raise (InvalidOperationException $"Support policy resource at '{resourcePath}' could not be parsed.")
            | value -> value

        return value
    }

let shouldEscalate (classification: TicketClassification) (customer: CustomerRecord) (policy: SupportPolicy) =
    let severityRequiresEscalation =
        policy.EscalateSeverities
        |> Array.exists (fun level -> String.Equals(level, classification.Severity, StringComparison.OrdinalIgnoreCase))

    classification.NeedsEscalation
    && (severityRequiresEscalation || (customer.IsVip && policy.VipAlwaysEscalate))

[<EntryPoint>]
let main argv =
    let options = SampleOptions.parse argv

    if options.ShowHelp then
        SampleOptions.printHelp ()
        0
    else
        let scenario = SampleScenario.create ()
        SampleScenario.validate scenario

        match RuntimeFactory.create options with
        | None -> 0
        | Some runtime ->
            use cleanup = runtime.Cleanup
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)

            try
                let classification =
                    classifyAsync runtime.Runtime scenario options cts.Token
                    |> fun task -> task.GetAwaiter().GetResult()

                let customerSummary, policy =
                    runBoundedParallelAsync
                        (summarizeCustomerAsync scenario classification)
                        (loadPolicyAsync scenario.SupportPolicyResourcePath)
                        cts.Token
                    |> fun task -> task.GetAwaiter().GetResult()

                printfn ""
                printfn "Customer summary:"
                printfn "- %s" customerSummary.SummaryText
                printfn "Policy guidance:"
                policy.Guidance |> Array.iter (fun item -> printfn "- %s" item)

                let escalation =
                    if shouldEscalate classification customerSummary.Customer policy then
                        printfn ""
                        printfn "Approval boundary reached: escalation tool is configured with ApprovalMode.Always."
                        printfn "Sample host auto-approves the scripted escalation so the example can run unattended."

                        scenario.EscalateAsync
                            (EscalationRequest(
                                TicketId = scenario.Ticket.TicketId,
                                Queue = policy.Queue,
                                Reason = classification.Reason,
                                AnalystSummary = customerSummary.SummaryText
                            ))
                            cts.Token
                        |> fun task -> Some(task.GetAwaiter().GetResult())
                    else
                        printfn ""
                        printfn "No escalation needed; the conditional branch was skipped."
                        None

                printfn ""
                printfn "Final triage result:"
                printfn "- Category: %s" classification.Category
                printfn "- Severity: %s" classification.Severity
                printfn "- Recommended queue: %s" classification.RecommendedQueue
                printfn "- Escalated: %b" escalation.IsSome

                escalation
                |> Option.iter (fun value -> printfn "- Escalation id: %s" value.EscalationId)

                0
            with ex ->
                eprintfn "Sample failed: %s" ex.Message
                1
