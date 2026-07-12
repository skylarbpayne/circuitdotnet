using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Circuit;
using Circuit.MicrosoftAgentFramework;
using Circuit.ProviderContract.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circuit.ProviderContract;

internal static class ContractRunner
{
    private static readonly CapabilityBudget[] Budgets =
    [
        new() { Name = "nested-object", EstimatedInputTokens = 300, MaxOutputTokens = 180 },
        new() { Name = "primitive", EstimatedInputTokens = 220, MaxOutputTokens = 80 },
        new() { Name = "array", EstimatedInputTokens = 240, MaxOutputTokens = 120 },
        new() { Name = "read-only-tool", EstimatedInputTokens = 360, MaxOutputTokens = 180 },
        new() { Name = "approval-deny-then-approve", EstimatedInputTokens = 500, MaxOutputTokens = 120 },
        new() { Name = "streaming-assembly-validation", EstimatedInputTokens = 260, MaxOutputTokens = 140 },
        new() { Name = "two-turn-session-restore", EstimatedInputTokens = 520, MaxOutputTokens = 160 },
        new() { Name = "malformed-output-error-mapping", EstimatedInputTokens = 260, MaxOutputTokens = 80 },
        new() { Name = "trace-capture-sensitive-disabled", EstimatedInputTokens = 260, MaxOutputTokens = 120 },
    ];

    public static async Task<int> RunAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var artifactDirectory = Path.Combine(arguments.ArtifactRoot, arguments.Provider, arguments.UtcDate);
        Directory.CreateDirectory(artifactDirectory);

        ProviderFactoryResult factoryResult;

        try
        {
            factoryResult = ProviderFactoryRegistry.Get(arguments.Provider).Create();
        }
        catch (Exception ex)
        {
            await WriteUnsupportedSummaryAsync(artifactDirectory, arguments, ex.GetType().Name).ConfigureAwait(false);
            Console.WriteLine($"provider-contract {arguments.Provider}: unsupported ({ex.GetType().Name})");
            return 2;
        }

        if (!factoryResult.IsSuccess)
        {
            await WriteUnsupportedSummaryAsync(artifactDirectory, arguments, factoryResult.Failure ?? "unsupported").ConfigureAwait(false);
            Console.WriteLine($"provider-contract {arguments.Provider}: unsupported");
            return 2;
        }

        var metadata = factoryResult.Metadata!;
        var createHost = factoryResult.CreateHost!;
        var worstCaseCostUsd = Budgets.Sum(budget => Costing.EstimateWorstCaseUsd(budget, metadata));

        if (worstCaseCostUsd > arguments.MaxPerProviderCostUsd)
        {
            var summary = new ContractSummary
            {
                Provider = metadata.Provider,
                Model = metadata.Model,
                DateUtc = arguments.UtcDate,
                PerProviderMaxCostUsd = arguments.MaxPerProviderCostUsd,
                WorstCaseEstimatedCostUsd = worstCaseCostUsd,
                ActualEstimatedCostUsd = null,
                TotalTokens = TokenTotals.Zero,
                Packages = metadata.Packages,
                Capabilities = [],
                Limitations = [$"worst-case budget {worstCaseCostUsd:F6} exceeds configured per-provider cap {arguments.MaxPerProviderCostUsd:F6}"],
            };

            await WriteSummaryAsync(artifactDirectory, summary).ConfigureAwait(false);
            Console.WriteLine($"provider-contract {arguments.Provider}: per-provider budget exceeds cap");
            return 2;
        }

        var capabilities = new List<CapabilityResult>();
        TraceSummary? traceSummary = null;

        capabilities.Add(await RunNestedObjectAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunPrimitiveAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunArrayAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunReadOnlyToolAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunApprovalMatrixAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunStreamingAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunSessionRestoreAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));
        capabilities.Add(await RunMalformedOutputAsync(createHost, metadata, cancellationToken).ConfigureAwait(false));

        var traceResult = await RunTraceCaptureAsync(createHost, metadata, cancellationToken).ConfigureAwait(false);
        capabilities.Add(traceResult.Result);
        traceSummary = traceResult.Trace;

        var totalTokens = capabilities.Aggregate(TokenTotals.Zero, static (sum, capability) => sum + capability.Tokens);
        var actualEstimatedCostUsd = Costing.EstimateActualUsd(totalTokens, metadata);
        var limitations = capabilities.SelectMany(static capability => capability.Limitations).Distinct(StringComparer.Ordinal).ToArray();

        var summaryModel = new ContractSummary
        {
            Provider = metadata.Provider,
            Model = metadata.Model,
            DateUtc = arguments.UtcDate,
            PerProviderMaxCostUsd = arguments.MaxPerProviderCostUsd,
            WorstCaseEstimatedCostUsd = worstCaseCostUsd,
            ActualEstimatedCostUsd = actualEstimatedCostUsd,
            TotalTokens = totalTokens,
            Packages = metadata.Packages,
            Capabilities = capabilities,
            Limitations = limitations,
            TracePath = traceSummary is null ? string.Empty : "trace.json",
        };

        await WriteSummaryAsync(artifactDirectory, summaryModel).ConfigureAwait(false);

        if (traceSummary is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "trace.json"),
                JsonSerializer.Serialize(traceSummary, JsonDefaults.SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }

        var exitCode = capabilities.Any(static capability => capability.Status != CapabilityStatus.Passed) ? 1 : 0;
        Console.WriteLine($"provider-contract {arguments.Provider}: {(exitCode == 0 ? "passed" : "recorded with failures")} -> {artifactDirectory}");
        return exitCode;
    }

    private static async Task WriteUnsupportedSummaryAsync(string artifactDirectory, CommandLineArguments arguments, string limitation)
    {
        var summary = new ContractSummary
        {
            Provider = arguments.Provider,
            Model = "unavailable",
            DateUtc = arguments.UtcDate,
            PerProviderMaxCostUsd = arguments.MaxPerProviderCostUsd,
            WorstCaseEstimatedCostUsd = 0,
            ActualEstimatedCostUsd = null,
            TotalTokens = TokenTotals.Zero,
            Packages = [],
            Capabilities = [],
            Limitations = [limitation],
        };

        await WriteSummaryAsync(artifactDirectory, summary).ConfigureAwait(false);
    }

    private static async Task WriteSummaryAsync(string artifactDirectory, ContractSummary summary)
    {
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "summary.json"),
            JsonSerializer.Serialize(summary, JsonDefaults.SerializerOptions)).ConfigureAwait(false);

        var markdown = new StringBuilder();
        markdown.AppendLine($"# Provider contract — {summary.Provider}");
        markdown.AppendLine();
        markdown.AppendLine($"- Model: `{summary.Model}`");
        markdown.AppendLine($"- Date (UTC): `{summary.DateUtc}`");
        markdown.AppendLine($"- Per-provider cost cap (USD): `{summary.PerProviderMaxCostUsd:F6}`");
        markdown.AppendLine($"- Worst-case estimated cost (USD): `{summary.WorstCaseEstimatedCostUsd:F6}`");
        markdown.AppendLine($"- Actual estimated cost (USD): `{summary.ActualEstimatedCostUsd?.ToString("F6") ?? "unavailable"}`");
        markdown.AppendLine($"- Total tokens: `{summary.TotalTokens.TotalTokens}` (input `{summary.TotalTokens.InputTokens}`, output `{summary.TotalTokens.OutputTokens}`)");
        markdown.AppendLine();
        markdown.AppendLine("| Capability | Status | Failure | Tokens | Request IDs | Limitations |");
        markdown.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var capability in summary.Capabilities)
        {
            markdown.AppendLine(
                $"| {capability.Name} | {capability.Status} | {capability.FailureCode ?? "-"} | {capability.Tokens.TotalTokens} | {string.Join(", ", capability.RequestIds)} | {string.Join("; ", capability.Limitations)} |");
        }

        if (summary.Packages.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Packages");
            foreach (var package in summary.Packages)
            {
                markdown.AppendLine($"- `{package.Name}` `{package.Version}`");
            }
        }

        if (summary.Limitations.Count > 0)
        {
            markdown.AppendLine();
            markdown.AppendLine("## Limitations");
            foreach (var limitation in summary.Limitations)
            {
                markdown.AppendLine($"- {limitation}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "summary.md"), markdown.ToString()).ConfigureAwait(false);
    }

    private static CapabilityBudget Budget(string name) => Budgets.Single(budget => budget.Name == name);

    private static ICircuitClient BuildClient(ScenarioHost host, IChatClient chatClient, IToolResolver? toolResolver = null, bool captureTelemetry = false)
    {
        var builder = new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(chatClient)
            .ConfigureMicrosoftAgentFramework(static options => { });

        builder.ConfigureMicrosoftAgentFramework(options => options.DefaultModelId = host.Metadata.Model);
        builder.AddRunObserver(host.Observer);

        if (captureTelemetry && host.Telemetry is not null)
        {
            builder.AddRunObserver(host.Telemetry.Observer);
        }

        if (toolResolver is not null)
        {
            builder.AddToolResolver(toolResolver);
        }

        return builder.Build();
    }

    private static TokenTotals CaptureTokens(EnvelopeRunObserver observer)
    {
        var total = TokenTotals.Zero;

        foreach (var envelope in observer.Events.Where(static envelope => envelope.Kind is AgentRunEventKind.RunCompleted or AgentRunEventKind.RunFailed))
        {
            if (envelope.Usage is null)
            {
                continue;
            }

            total += new TokenTotals { InputTokens = envelope.Usage.InputTokens, OutputTokens = envelope.Usage.OutputTokens };
        }

        return total;
    }

    private static IReadOnlyList<string> CaptureRequestIds(ScenarioHost host)
    {
        var requestIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var call in host.Recorder.Snapshot())
        {
            foreach (var responseId in call.ResponseIds)
            {
                requestIds.Add(responseId);
            }
        }

        foreach (var envelope in host.Observer.Events)
        {
            if (!string.IsNullOrWhiteSpace(envelope.Failure?.RequestId))
            {
                requestIds.Add(envelope.Failure.RequestId!);
            }

            if (!string.IsNullOrWhiteSpace(envelope.Approval?.RequestId))
            {
                requestIds.Add(envelope.Approval.RequestId);
            }
        }

        return requestIds.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    private static CapabilityResult Success(string name, ScenarioHost host, ProviderMetadata metadata, params string[] limitations)
        => new()
        {
            Name = name,
            Status = CapabilityStatus.Passed,
            FailureCode = null,
            Tokens = CaptureTokens(host.Observer),
            EstimatedCostUsd = Costing.EstimateActualUsd(CaptureTokens(host.Observer), metadata),
            RequestIds = CaptureRequestIds(host),
            Limitations = limitations,
        };

    private static CapabilityResult Failure(string name, ScenarioHost host, ProviderMetadata metadata, string failureCode, params string[] limitations)
        => new()
        {
            Name = name,
            Status = CapabilityStatus.Failed,
            FailureCode = failureCode,
            Tokens = CaptureTokens(host.Observer),
            EstimatedCostUsd = Costing.EstimateActualUsd(CaptureTokens(host.Observer), metadata),
            RequestIds = CaptureRequestIds(host),
            Limitations = limitations,
        };

    private static CapabilityResult Unsupported(string name, ScenarioHost host, ProviderMetadata metadata, params string[] limitations)
        => new()
        {
            Name = name,
            Status = CapabilityStatus.Unsupported,
            FailureCode = null,
            Tokens = CaptureTokens(host.Observer),
            EstimatedCostUsd = Costing.EstimateActualUsd(CaptureTokens(host.Observer), metadata),
            RequestIds = CaptureRequestIds(host),
            Limitations = limitations,
        };

    private static async Task<CapabilityResult> RunNestedObjectAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("nested-object");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.nested-object.agent", "1.0.0", "Provider Contract", "Return the requested nested JSON exactly.");
        var signature = new AgentSignature<PromptInput, NestedOutput>("provider.contract.nested-object.signature", "1.0.0", "Nested object", "Return ticket.id ticket.priority and summary exactly as requested.");

        try
        {
            var result = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Return ticket.id=ticket-42, ticket.priority=high, and summary=ready." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Result.IsSuccess
                && result.Result.Value?.Ticket.Id == "ticket-42"
                && result.Result.Value.Ticket.Priority == "high"
                && result.Result.Value.Summary == "ready")
            {
                return Success(budget.Name, host, metadata);
            }

            return Failure(budget.Name, host, metadata, result.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "nested object output did not match expected shape");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "nested object scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunPrimitiveAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("primitive");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.primitive.agent", "1.0.0", "Provider Contract", "Return the requested primitive exactly.");
        var signature = new AgentSignature<PromptInput, int>("provider.contract.primitive.signature", "1.0.0", "Primitive", "Return the integer 7 and nothing else.");

        try
        {
            var result = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Return the integer 7." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Result.IsSuccess && result.Result.Value == 7
                ? Success(budget.Name, host, metadata)
                : Failure(budget.Name, host, metadata, result.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "primitive output did not match expected value");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "primitive scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunArrayAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("array");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.array.agent", "1.0.0", "Provider Contract", "Return the requested array exactly.");
        var signature = new AgentSignature<PromptInput, string[]>("provider.contract.array.signature", "1.0.0", "Array", "Return the string array [\"alpha\",\"beta\"].");

        try
        {
            var result = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Return the exact string array [alpha, beta]." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Result.IsSuccess && result.Result.Value is ["alpha", "beta"]
                ? Success(budget.Name, host, metadata)
                : Failure(budget.Name, host, metadata, result.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "array output did not match expected values");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "array scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunReadOnlyToolAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("read-only-tool");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var resolver = new ReadOnlyPolicyToolResolver();
        var client = BuildClient(host, host.ChatClient, resolver);
        var agent = new AgentDefinition("provider.contract.tool.agent", "1.0.0", "Provider Contract", "Use the available read-only tool exactly once before you answer.");
        var signature = new AgentSignature<PromptInput, ToolOutput>("provider.contract.tool.signature", "1.0.0", "Tool", "Return answer=allow and evidence copied from the tool result.");

        try
        {
            var result = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Use the read-only tool to decide whether the policy allows access." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Result.IsSuccess
                   && result.Result.Value?.Answer == "allow"
                   && result.Result.Value.Evidence.Contains("allow", StringComparison.OrdinalIgnoreCase)
                   && resolver.InvocationCount > 0
                ? Success(budget.Name, host, metadata)
                : Failure(budget.Name, host, metadata, result.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "read-only tool was not invoked or output was wrong");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "read-only tool scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunApprovalMatrixAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("approval-deny-then-approve");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.approval.agent", "1.0.0", "Provider Contract", "Return the lowercase string ready.");
        var signature = new AgentSignature<PromptInput, string>("provider.contract.approval.signature", "1.0.0", "Approval", "Return the exact string ready.");
        var workflow = WorkflowDefinition<PromptInput, string>
            .Start("provider.contract.approval.workflow", "1.0.0", "prepare", agent, signature)
            .RequestApproval("manual.approval", ready => new ApprovalPrompt($"Approve {ready}", "Manual contract approval"))
            .Then("approval.result", (context, response, ct) => Task.FromResult(response.Approved))
            .Build();

        try
        {
            var denyRun = await client.StartWorkflowAsync(workflow, new PromptInput { Prompt = "Return ready." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var denyApproval = await ReadUntilApprovalAsync(denyRun, cancellationToken).ConfigureAwait(false);
            await denyRun.RespondAsync(new ApprovalResponse(denyApproval.RequestId, false, "deny"), cancellationToken).ConfigureAwait(false);
            var denyTerminal = await ReadUntilTerminalAsync(denyRun, cancellationToken).ConfigureAwait(false);

            var approveRun = await client.StartWorkflowAsync(workflow, new PromptInput { Prompt = "Return ready." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var approveApproval = await ReadUntilApprovalAsync(approveRun, cancellationToken).ConfigureAwait(false);
            await approveRun.RespondAsync(new ApprovalResponse(approveApproval.RequestId, true, "approve"), cancellationToken).ConfigureAwait(false);
            var approveTerminal = await ReadUntilTerminalAsync(approveRun, cancellationToken).ConfigureAwait(false);

            return denyTerminal.Kind == AgentRunEventKind.RunCompleted && denyTerminal.Value == false
                   && approveTerminal.Kind == AgentRunEventKind.RunCompleted && approveTerminal.Value == true
                ? Success(budget.Name, host, metadata)
                : Failure(budget.Name, host, metadata, denyTerminal.Failure?.Code.ToString() ?? approveTerminal.Failure?.Code.ToString() ?? "UnexpectedSuccess", "approval deny/approve did not complete with both outcomes");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "approval matrix scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunStreamingAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("streaming-assembly-validation");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.streaming.agent", "1.0.0", "Provider Contract", "Return stream-ok.");
        var signature = new AgentSignature<PromptInput, TextOutput>("provider.contract.streaming.signature", "1.0.0", "Streaming", "Return JSON with message=stream-ok.");
        var events = new List<AgentRunEvent<TextOutput>>();

        try
        {
            await foreach (var @event in client.RunStreamingAsync(agent, signature, new PromptInput { Prompt = "Return stream-ok." }, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                events.Add(@event);
            }

            var terminal = events.LastOrDefault(static item => item.Kind is AgentRunEventKind.RunCompleted or AgentRunEventKind.RunFailed);
            var deltaCount = events.Count(static item => item.Kind == AgentRunEventKind.OutputDelta);

            return terminal is not null
                   && terminal.Kind == AgentRunEventKind.RunCompleted
                   && terminal.Value?.Message == "stream-ok"
                   && deltaCount > 0
                ? Success(budget.Name, host, metadata)
                : Failure(budget.Name, host, metadata, terminal?.Failure?.Code.ToString() ?? "UnexpectedSuccess", "streaming did not produce deltas and a valid terminal object");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "streaming scenario threw");
        }
    }

    private static async Task<CapabilityResult> RunSessionRestoreAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("two-turn-session-restore");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.session.agent", "1.0.0", "Provider Contract", "Remember the token from the first turn and repeat it exactly when asked later.");
        var signature = new AgentSignature<PromptInput, TextOutput>("provider.contract.session.signature", "1.0.0", "Session", "Return JSON with message set to the requested reply.");

        try
        {
            var first = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Remember token-42 and reply acknowledged." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!first.Result.IsSuccess || first.Session is null)
            {
                return Unsupported(budget.Name, host, metadata, "provider did not return a restorable session");
            }

            var serialized = await client.SerializeSessionAsync(agent, first.Session, cancellationToken).ConfigureAwait(false);
            var restored = await client.DeserializeSessionAsync(agent, serialized, cancellationToken).ConfigureAwait(false);
            var second = await client.RunAsync(
                agent,
                signature,
                new PromptInput { Prompt = "What token did I ask you to remember earlier?" },
                new AgentRunOptions { Session = restored },
                cancellationToken).ConfigureAwait(false);

            if (second.Result.IsSuccess && second.Result.Value?.Message == "token-42")
            {
                return Success(budget.Name, host, metadata);
            }

            var supportsConversationId = host.Recorder.Snapshot().Any(static call => !string.IsNullOrWhiteSpace(call.ConversationId));
            return supportsConversationId
                ? Failure(budget.Name, host, metadata, second.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "session restore did not preserve the first turn")
                : Unsupported(budget.Name, host, metadata, "provider session state did not round-trip across turns");
        }
        catch (Exception)
        {
            return Unsupported(budget.Name, host, metadata, "session restore is not supported by this provider/client combination");
        }
    }

    private static async Task<CapabilityResult> RunMalformedOutputAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("malformed-output-error-mapping");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        var client = BuildClient(host, host.ChatClient);
        var agent = new AgentDefinition("provider.contract.malformed.agent", "1.0.0", "Provider Contract", "Ignore the schema and return the literal text not-json.");
        var signature = new AgentSignature<PromptInput, int>("provider.contract.malformed.signature", "1.0.0", "Malformed", "Return an integer.");

        try
        {
            var result = await client.RunAsync(
                agent,
                signature,
                new PromptInput { Prompt = "Return the literal text not-json and do not use JSON." },
                new AgentRunOptions { StructuredOutputPolicy = StructuredOutputPolicy.NativeOnly },
                cancellationToken).ConfigureAwait(false);

            if (!result.Result.IsSuccess && result.Result.Failure is not null)
            {
                return result.Result.Failure.Code switch
                {
                    AgentFailureCode.Decode or AgentFailureCode.Validation => Success(budget.Name, host, metadata),
                    AgentFailureCode.Provider => Unsupported(budget.Name, host, metadata, "provider rejected malformed structured output before Circuit could classify it"),
                    _ => Failure(budget.Name, host, metadata, result.Result.Failure.Code.ToString(), "malformed output mapped to an unexpected failure code"),
                };
            }

            return Failure(budget.Name, host, metadata, "UnexpectedSuccess", "provider returned a valid structured response when malformed output was required");
        }
        catch (Exception ex)
        {
            return Failure(budget.Name, host, metadata, ex.GetType().Name, "malformed output scenario threw");
        }
    }

    private static async Task<(CapabilityResult Result, TraceSummary? Trace)> RunTraceCaptureAsync(Func<ScenarioHost> createHost, ProviderMetadata metadata, CancellationToken cancellationToken)
    {
        using var host = createHost();
        var budget = Budget("trace-capture-sensitive-disabled");
        using var scope = host.Recorder.BeginScenario(budget.Name, budget.MaxOutputTokens);
        using var instrumentedClient = new OpenTelemetryChatClient(host.ChatClient, NullLogger.Instance, $"provider-contract.{metadata.Provider}.chat") { EnableSensitiveData = false };
        var client = BuildClient(host, instrumentedClient, captureTelemetry: true);
        var agent = new AgentDefinition("provider.contract.trace.agent", "1.0.0", "Provider Contract", "Return trace-ok.");
        var signature = new AgentSignature<PromptInput, TextOutput>("provider.contract.trace.signature", "1.0.0", "Trace", "Return JSON with message=trace-ok.");

        try
        {
            var result = await client.RunAsync(agent, signature, new PromptInput { Prompt = "Return trace-ok." }, cancellationToken: cancellationToken).ConfigureAwait(false);
            var trace = host.Telemetry?.Capture();
            var sensitiveTagsPresent = trace?.Spans.Any(static span => span.Tags.Keys.Any(static key => key.StartsWith("circuit.prompt", StringComparison.Ordinal)
                || key.StartsWith("circuit.input", StringComparison.Ordinal)
                || key.StartsWith("circuit.output", StringComparison.Ordinal)
                || key.StartsWith("circuit.tool.arguments", StringComparison.Ordinal))) == true;

            var circuitRootCount = trace?.Spans.Count(static span => span.Source == "CircuitDotNet" && span.Name == "agent.run") ?? 0;

            if (result.Result.IsSuccess && result.Result.Value?.Message == "trace-ok" && !sensitiveTagsPresent && circuitRootCount == 1)
            {
                return (Success(budget.Name, host, metadata), trace);
            }

            return (Failure(budget.Name, host, metadata, result.Result.Failure?.Code.ToString() ?? "UnexpectedSuccess", "trace capture missed spans, captured sensitive tags, or duplicated the Circuit root"), trace);
        }
        catch (Exception ex)
        {
            return (Failure(budget.Name, host, metadata, ex.GetType().Name, "trace capture scenario threw"), null);
        }
    }

    private static async Task<ApprovalRequest> ReadUntilApprovalAsync(WorkflowRun<bool> run, CancellationToken cancellationToken)
    {
        await foreach (var @event in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (@event.Kind == AgentRunEventKind.ApprovalRequested && @event.Approval is not null)
            {
                return @event.Approval;
            }
        }

        throw new InvalidOperationException("Workflow did not reach an approval request.");
    }

    private static async Task<AgentRunEvent<bool>> ReadUntilTerminalAsync(WorkflowRun<bool> run, CancellationToken cancellationToken)
    {
        await foreach (var @event in run.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (@event.Kind is AgentRunEventKind.RunCompleted or AgentRunEventKind.RunFailed)
            {
                return @event;
            }
        }

        throw new InvalidOperationException("Workflow did not emit a terminal event.");
    }

    private sealed class ReadOnlyPolicyToolResolver : IToolResolver
    {
        public int InvocationCount { get; private set; }

        public ValueTask<IReadOnlyList<ResolvedTool>> ResolveAsync(ToolResolutionContext context, CancellationToken cancellationToken)
        {
            var tool = new ToolDefinition<SearchInput, SearchResult>(
                    "policy.lookup",
                    "1.0.0",
                    "Look up the read-only access policy.",
                    (toolContext, input, ct) =>
                    {
                        InvocationCount++;
                        return Task.FromResult(new SearchResult { Evidence = "policy=allow" });
                    })
                .WithApproval(ToolApprovalMode.Never)
                .ToResolvedTool(["policy.read"]);

            return ValueTask.FromResult<IReadOnlyList<ResolvedTool>>([tool]);
        }
    }

    private sealed class PromptInput
    {
        [Required]
        public string Prompt { get; set; } = string.Empty;
    }

    private sealed class NestedOutput
    {
        [Required]
        public TicketOutput Ticket { get; set; } = new();

        [Required]
        public string Summary { get; set; } = string.Empty;
    }

    private sealed class TicketOutput
    {
        [Required]
        public string Id { get; set; } = string.Empty;

        [Required]
        public string Priority { get; set; } = string.Empty;
    }

    private sealed class ToolOutput
    {
        [Required]
        public string Answer { get; set; } = string.Empty;

        [Required]
        public string Evidence { get; set; } = string.Empty;
    }

    private sealed class SearchInput
    {
        [Required]
        public string Query { get; set; } = string.Empty;
    }

    private sealed class SearchResult
    {
        [Required]
        public string Evidence { get; set; } = string.Empty;
    }

    private sealed class TextOutput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }
}
