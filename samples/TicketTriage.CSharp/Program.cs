using System.Reflection;
using System.Text.Json;
using Circuit;
using Circuit.MicrosoftAgentFramework;
using Circuit.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

return await TicketTriageSample.RunAsync(args);

internal static class TicketTriageSample
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxParallelBranches = 2;
    private const string ChatClientFactoryAssemblyEnv = "CIRCUIT_SAMPLE_CHAT_CLIENT_FACTORY_ASSEMBLY";
    private const string ChatClientFactoryTypeEnv = "CIRCUIT_SAMPLE_CHAT_CLIENT_FACTORY_TYPE";
    private const string ModelIdEnv = "CIRCUIT_SAMPLE_MODEL_ID";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = SampleOptions.Parse(args);
        if (options.ShowHelp)
        {
            SampleOptions.PrintHelp();
            return 0;
        }

        var scenario = SampleScenario.Create();
        scenario.Validate();

        using var telemetry = options.EnableOpenTelemetry ? CreateTelemetry(options.Live) : null;

        if (options.EnableOpenTelemetry && !options.Live)
        {
            Console.WriteLine("OpenTelemetry console export is enabled, but the offline ScriptedRuntime does not emit observer callbacks.");
        }

        await using var provider = await BuildServiceProviderAsync(options, cancellationToken: CancellationToken.None);
        if (provider is null)
        {
            return 0;
        }

        var client = provider.GetRequiredService<ICircuitClient>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var classification = await ClassifyAsync(client, scenario, options, cts.Token);
            var (customerSummary, policy) = await RunParallelLookupsAsync(scenario, classification, cts.Token);

            Console.WriteLine();
            Console.WriteLine("Customer summary:");
            Console.WriteLine($"- {customerSummary.SummaryText}");
            Console.WriteLine("Policy guidance:");
            foreach (var guidance in policy.Guidance)
            {
                Console.WriteLine($"- {guidance}");
            }

            EscalationReceipt? escalation = null;
            if (ShouldEscalate(classification, customerSummary.Customer, policy))
            {
                Console.WriteLine();
                Console.WriteLine("Approval boundary reached: escalation tool is configured with ApprovalMode.Always.");
                Console.WriteLine("Sample host auto-approves the scripted escalation so the example can run unattended.");

                escalation = await scenario.EscalateAsync(
                    new EscalationRequest
                    {
                        TicketId = scenario.Ticket.TicketId,
                        Queue = policy.Queue,
                        Reason = classification.Reason,
                        AnalystSummary = customerSummary.SummaryText,
                    },
                    cts.Token);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("No escalation needed; the conditional branch was skipped.");
            }

            Console.WriteLine();
            Console.WriteLine("Final triage result:");
            Console.WriteLine($"- Category: {classification.Category}");
            Console.WriteLine($"- Severity: {classification.Severity}");
            Console.WriteLine($"- Recommended queue: {classification.RecommendedQueue}");
            Console.WriteLine($"- Escalated: {escalation is not null}");

            if (escalation is not null)
            {
                Console.WriteLine($"- Escalation id: {escalation.EscalationId}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sample failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<ServiceProvider?> BuildServiceProviderAsync(SampleOptions options, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();

        if (options.Live)
        {
            var chatClient = TryCreateChatClientFromEnvironment();
            if (chatClient is null)
            {
                Console.WriteLine("Live mode skipped: no chat-client plugin or credentials were supplied.");
                Console.WriteLine($"Set {ChatClientFactoryAssemblyEnv} and {ChatClientFactoryTypeEnv} to a provider plugin that exposes public static IChatClient? CreateFromEnvironment().");
                return null;
            }

            services.AddSingleton<IChatClient>(chatClient);
        }
        else
        {
            var runtime = new ScriptedRuntime(
            [
                ScriptedResponses.Stream(
                [
                    "{\"category\":\"billing\",\"severity\":\"high\",",
                    "\"recommendedQueue\":\"billing-escalations\",\"needsEscalation\":true,",
                    "\"reason\":\"Duplicate billing is blocking an enterprise customer rollout.\"}"
                ])
            ]);

            services.AddSingleton<Circuit.Core.ICircuitRuntime>(runtime);
        }

        services.AddCircuit(configure: circuit =>
        {
            var modelId = Environment.GetEnvironmentVariable(ModelIdEnv);
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                circuit.MicrosoftAgentFramework.DefaultModelId = modelId;
            }

            if (options.Live && options.EnableOpenTelemetry)
            {
                circuit.AddRunObserver(new OpenTelemetryRunObserver(options: null));
            }
        });

        await Task.CompletedTask;
        return services.BuildServiceProvider();
    }

    private static IChatClient? TryCreateChatClientFromEnvironment()
    {
        var assemblyPath = Environment.GetEnvironmentVariable(ChatClientFactoryAssemblyEnv);
        var typeName = Environment.GetEnvironmentVariable(ChatClientFactoryTypeEnv);

        if (string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var factoryType = assembly.GetType(typeName, throwOnError: true)!;
        var method = factoryType.GetMethod("CreateFromEnvironment", BindingFlags.Public | BindingFlags.Static);

        if (method is null)
        {
            throw new InvalidOperationException($"{factoryType.FullName} must define public static IChatClient? CreateFromEnvironment().");
        }

        if (!typeof(IChatClient).IsAssignableFrom(method.ReturnType) && method.ReturnType != typeof(IChatClient))
        {
            throw new InvalidOperationException($"{factoryType.FullName}.CreateFromEnvironment() must return {nameof(IChatClient)}.");
        }

        return (IChatClient?)method.Invoke(obj: null, parameters: null);
    }

    private static async Task<TicketClassification> ClassifyAsync(
        ICircuitClient client,
        SampleScenario scenario,
        SampleOptions options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(options.Live ? "Running live structured classification..." : "Running offline structured classification with ScriptedRuntime...");
        Console.WriteLine($"Read tool approval: {scenario.CustomerLookupTool.ApprovalMode}");
        Console.WriteLine($"Write tool approval: {scenario.EscalationTool.ApprovalMode}");
        Console.WriteLine($"Skill: {scenario.SupportSkill.Id}@{scenario.SupportSkill.Version} (resource-only)");

        var events = new List<AgentRunEvent<TicketClassification>>();
        TicketClassification? classification = null;

        var runOptions = new AgentRunOptions
        {
            SensitiveDataMode = SensitiveDataMode.Redact,
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sample"] = "ticket-triage",
                ["mode"] = options.Live ? "live" : "offline",
            }
        };

        await foreach (var @event in client.RunStreamingAsync(
                           scenario.Agent,
                           scenario.Signature,
                           scenario.Ticket,
                           options: runOptions,
                           cancellationToken: cancellationToken))
        {
            events.Add(@event);
            PrintSafeEvent(@event);

            if (@event.Kind == AgentRunEventKind.RunCompleted)
            {
                classification = @event.Value;
            }

            if (@event.Kind == AgentRunEventKind.RunFailed)
            {
                throw new InvalidOperationException(@event.Failure?.Message ?? "The run failed without a public failure message.");
            }
        }

        RunAssertions.AssertMonotonicSequence(events);
        RunAssertions.AssertTerminalEventCount(events, 1);

        return classification ?? throw new InvalidOperationException("The streamed run completed without a structured classification value.");
    }

    private static void PrintSafeEvent(AgentRunEvent<TicketClassification> @event)
    {
        var detail = @event.Kind switch
        {
            AgentRunEventKind.OutputDelta => " (delta omitted)",
            AgentRunEventKind.RunCompleted => " (structured result received)",
            AgentRunEventKind.RunFailed => " (failure message sanitized by Circuit)",
            _ => string.Empty,
        };

        Console.WriteLine($"event[{@event.Sequence}] {@event.Kind}{detail}");
    }

    private static Task<(CustomerSummary Summary, SupportPolicy Policy)> RunParallelLookupsAsync(
        SampleScenario scenario,
        TicketClassification classification,
        CancellationToken cancellationToken)
        => RunBoundedParallelAsync(
            MaxParallelBranches,
            ct => SummarizeCustomerAsync(scenario, classification, ct),
            ct => LoadPolicyAsync(scenario.SupportPolicyResourcePath, ct),
            cancellationToken);

    private static async Task<(TLeft Left, TRight Right)> RunBoundedParallelAsync<TLeft, TRight>(
        int maxConcurrency,
        Func<CancellationToken, Task<TLeft>> left,
        Func<CancellationToken, Task<TRight>> right,
        CancellationToken cancellationToken)
    {
        if (maxConcurrency < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "This sample expects room for two parallel branches.");
        }

        var leftTask = left(cancellationToken);
        var rightTask = right(cancellationToken);
        await Task.WhenAll(leftTask, rightTask);
        return (await leftTask, await rightTask);
    }

    private static async Task<CustomerSummary> SummarizeCustomerAsync(
        SampleScenario scenario,
        TicketClassification classification,
        CancellationToken cancellationToken)
    {
        var customer = await scenario.LookupCustomerAsync(
            new CustomerLookupRequest { CustomerId = scenario.Ticket.CustomerId },
            cancellationToken);

        var summary =
            $"{customer.Name} is on the {customer.Plan} plan, VIP={customer.IsVip.ToString().ToLowerInvariant()}, open incidents={customer.OpenIncidentCount}. " +
            $"Classification suggests {classification.Category}/{classification.Severity}.";

        return new CustomerSummary(customer, summary);
    }

    private static async Task<SupportPolicy> LoadPolicyAsync(string resourcePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(resourcePath);
        var policy = await JsonSerializer.DeserializeAsync<SupportPolicy>(stream, JsonOptions, cancellationToken);
        return policy ?? throw new InvalidOperationException($"Support policy resource at '{resourcePath}' could not be parsed.");
    }

    private static bool ShouldEscalate(
        TicketClassification classification,
        CustomerRecord customer,
        SupportPolicy policy)
    {
        var severityRequiresEscalation = policy.EscalateSeverities.Any(level =>
            string.Equals(level, classification.Severity, StringComparison.OrdinalIgnoreCase));

        return classification.NeedsEscalation && (severityRequiresEscalation || (customer.IsVip && policy.VipAlwaysEscalate));
    }

    private static IDisposable CreateTelemetry(bool liveMode)
    {
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("CircuitDotNet")
            .AddConsoleExporter()
            .Build();

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("CircuitDotNet")
            .AddConsoleExporter()
            .Build();

        Console.WriteLine(liveMode
            ? "OpenTelemetry console export is enabled for Circuit runtime spans and metrics."
            : "OpenTelemetry console export is enabled; live runtime spans appear only when using --live.");

        return new CompositeDisposable(tracerProvider, meterProvider);
    }

    private sealed class CompositeDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed record CustomerSummary(CustomerRecord Customer, string SummaryText);

    private sealed class SampleScenario
    {
        private SampleScenario(
            TicketInput ticket,
            AgentDefinition agent,
            AgentSignature<TicketInput, TicketClassification> signature,
            ToolDefinition<CustomerLookupRequest, CustomerRecord> customerLookupTool,
            ToolDefinition<EscalationRequest, EscalationReceipt> escalationTool,
            SkillReference supportSkill,
            string supportPolicyResourcePath,
            Func<CustomerLookupRequest, CancellationToken, Task<CustomerRecord>> lookupCustomerAsync,
            Func<EscalationRequest, CancellationToken, Task<EscalationReceipt>> escalateAsync)
        {
            Ticket = ticket;
            Agent = agent;
            Signature = signature;
            CustomerLookupTool = customerLookupTool;
            EscalationTool = escalationTool;
            SupportSkill = supportSkill;
            SupportPolicyResourcePath = supportPolicyResourcePath;
            LookupCustomerAsync = lookupCustomerAsync;
            EscalateAsync = escalateAsync;
        }

        public TicketInput Ticket { get; }

        public AgentDefinition Agent { get; }

        public AgentSignature<TicketInput, TicketClassification> Signature { get; }

        public ToolDefinition<CustomerLookupRequest, CustomerRecord> CustomerLookupTool { get; }

        public ToolDefinition<EscalationRequest, EscalationReceipt> EscalationTool { get; }

        public SkillReference SupportSkill { get; }

        public string SupportPolicyResourcePath { get; }

        public Func<CustomerLookupRequest, CancellationToken, Task<CustomerRecord>> LookupCustomerAsync { get; }

        public Func<EscalationRequest, CancellationToken, Task<EscalationReceipt>> EscalateAsync { get; }

        public static SampleScenario Create()
        {
            var repoRoot = FindRepoRoot();
            var skillRoot = Path.Combine(repoRoot, "samples", "TicketTriage.Shared", "SupportPolicySkill");
            var resourcePath = Path.Combine(skillRoot, "references", "escalation-policy.json");

            var supportSkill = SkillReference.CreateFile(
                id: "skill.support-policy",
                version: "1.0.0",
                fileRoots: [skillRoot],
                description: "Ticket-triage escalation guidance.");

            var customerLookup = new Func<CustomerLookupRequest, CancellationToken, Task<CustomerRecord>>(
                (request, _) => Task.FromResult(new CustomerRecord
                {
                    CustomerId = request.CustomerId,
                    Name = "Northwind Solar",
                    Plan = "Enterprise",
                    IsVip = true,
                    OpenIncidentCount = 2,
                }));

            var escalate = new Func<EscalationRequest, CancellationToken, Task<EscalationReceipt>>(
                (request, _) => Task.FromResult(new EscalationReceipt
                {
                    EscalationId = "ESC-9001",
                    Queue = request.Queue,
                    Status = "queued",
                }));

            var customerLookupTool = new ToolDefinition<CustomerLookupRequest, CustomerRecord>(
                    id: "customer.lookup",
                    version: "1.0.0",
                    description: "Read-only customer lookup.",
                    handler: (context, input, cancellationToken) => customerLookup(input, cancellationToken))
                .WithApproval(ToolApprovalMode.Never);

            var escalationTool = new ToolDefinition<EscalationRequest, EscalationReceipt>(
                    id: "ticket.escalate",
                    version: "1.0.0",
                    description: "Write an escalation request.",
                    handler: (context, input, cancellationToken) => escalate(input, cancellationToken))
                .WithApproval(ToolApprovalMode.Always);

            var agent = new AgentDefinition(
                    id: "ticket-triage.agent",
                    version: "1.0.0",
                    name: "Ticket Triage",
                    instructions: "Classify the incoming support ticket and return only the structured result.")
                .WithSkills([supportSkill]);

            var signature = new AgentSignature<TicketInput, TicketClassification>(
                id: "ticket-triage.signature",
                version: "1.0.0",
                description: "Ticket triage",
                instructions: "Return category, severity, recommendedQueue, needsEscalation, and a brief reason.");

            var ticket = new TicketInput
            {
                TicketId = "TICKET-1024",
                CustomerId = "CUST-2048",
                Subject = "Card charged twice after enterprise upgrade",
                Body = "I upgraded the account today, our finance team spotted a duplicate charge, and the rollout is blocked until billing fixes it.",
                RequestedPriority = "high",
            };

            return new SampleScenario(
                ticket,
                agent,
                signature,
                customerLookupTool,
                escalationTool,
                supportSkill,
                resourcePath,
                customerLookup,
                escalate);
        }

        public void Validate()
        {
            if (CustomerLookupTool.ApprovalMode != ToolApprovalMode.Never)
            {
                throw new InvalidOperationException("The customer lookup tool must be configured with ApprovalMode.Never.");
            }

            if (EscalationTool.ApprovalMode != ToolApprovalMode.Always)
            {
                throw new InvalidOperationException("The escalation tool must be configured with ApprovalMode.Always.");
            }

            var skillRoot = Path.GetDirectoryName(Path.GetDirectoryName(SupportPolicyResourcePath)!)!;
            if (File.Exists(Path.Combine(skillRoot, "run.py")))
            {
                throw new InvalidOperationException("The support-policy skill must not include a script.");
            }

            var resourceFiles = Directory.GetFiles(Path.Combine(skillRoot, "references"), "*", SearchOption.AllDirectories);
            if (resourceFiles.Length != 1)
            {
                throw new InvalidOperationException("The support-policy skill must contain exactly one resource file.");
            }
        }
    }

    private sealed class SampleOptions
    {
        public bool Live { get; private init; }

        public bool EnableOpenTelemetry { get; private init; }

        public bool ShowHelp { get; private init; }

        public static SampleOptions Parse(IEnumerable<string> args)
        {
            var options = new SampleOptions();
            var live = false;
            var otel = false;
            var help = false;

            foreach (var arg in args)
            {
                switch (arg)
                {
                    case "--live":
                        live = true;
                        break;
                    case "--otel":
                        otel = true;
                        break;
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{arg}'. Use --help for usage.");
                }
            }

            return new SampleOptions
            {
                Live = live,
                EnableOpenTelemetry = otel,
                ShowHelp = help,
            };
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Usage: dotnet run --project samples/TicketTriage.CSharp [--live] [--otel]");
            Console.WriteLine("Default mode is offline and uses ScriptedRuntime.");
            Console.WriteLine("Live mode loads a provider-specific IChatClient plugin from the environment.");
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CircuitDotNet.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the sample output directory.");
    }

    private sealed class TicketInput
    {
        public string TicketId { get; set; } = string.Empty;

        public string CustomerId { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public string RequestedPriority { get; set; } = string.Empty;
    }

    private sealed class TicketClassification
    {
        public string Category { get; set; } = string.Empty;

        public string Severity { get; set; } = string.Empty;

        public string RecommendedQueue { get; set; } = string.Empty;

        public bool NeedsEscalation { get; set; }

        public string Reason { get; set; } = string.Empty;
    }

    private sealed class CustomerLookupRequest
    {
        public string CustomerId { get; set; } = string.Empty;
    }

    private sealed class CustomerRecord
    {
        public string CustomerId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Plan { get; set; } = string.Empty;

        public bool IsVip { get; set; }

        public int OpenIncidentCount { get; set; }
    }

    private sealed class EscalationRequest
    {
        public string TicketId { get; set; } = string.Empty;

        public string Queue { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;

        public string AnalystSummary { get; set; } = string.Empty;
    }

    private sealed class EscalationReceipt
    {
        public string EscalationId { get; set; } = string.Empty;

        public string Queue { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    private sealed class SupportPolicy
    {
        public string Queue { get; set; } = string.Empty;

        public string[] EscalateSeverities { get; set; } = [];

        public bool VipAlwaysEscalate { get; set; }

        public string[] Guidance { get; set; } = [];
    }
}
