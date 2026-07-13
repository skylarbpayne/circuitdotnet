using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Circuit;
using Circuit.ProviderContract.Providers;

namespace Circuit.ProviderContract;

internal static class ContractRunner
{
    public static async Task<int> RunAsync(CommandLineArguments arguments, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(arguments.ArtifactRoot, arguments.Provider, arguments.UtcDate);
        Directory.CreateDirectory(directory);
        ProviderFactoryResult factory;
        try { factory = ProviderFactoryRegistry.Get(arguments.Provider).Create(); }
        catch (Exception error) { return await WriteUnsupportedAsync(directory, arguments, error.GetType().Name); }
        if (!factory.IsSuccess) return await WriteUnsupportedAsync(directory, arguments, factory.Failure ?? "unsupported");

        var metadata = factory.Metadata!;
        using var host = factory.CreateHost!();
        var client = new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(host.ChatClient)
            .ConfigureMicrosoftAgentFramework(options => options.DefaultModelId = metadata.Model)
            .AddRunObserver(host.Observer)
            .Build();
        var agent = new AgentDefinition("provider.contract.agent", "1.0.0", "Provider contract", "Return the requested JSON.");
        var signature = new AgentSignature<PromptInput, PromptOutput>("provider.contract.signature", "1.0.0", "Contract", "Return message=ready.");
        var circuit = CircuitDefinition<PromptInput, PromptOutput>.FromAgent(agent, signature).Define("provider.contract", "1.0.0");

        CapabilityResult capability;
        try
        {
            var response = await client.RunAsync(circuit, new PromptInput { Prompt = "Return message ready." }, cancellationToken: cancellationToken);
            capability = new CapabilityResult
            {
                Name = "unified-circuit",
                Status = response.IsSuccess && response.Value.Message == "ready" ? CapabilityStatus.Passed : CapabilityStatus.Failed,
                FailureCode = response.IsSuccess ? null : response.Failure.Code.ToString(),
                Tokens = TokenTotals.Zero,
                EstimatedCostUsd = 0,
                RequestIds = [],
                Limitations = [],
            };
        }
        catch (Exception error)
        {
            capability = new CapabilityResult { Name = "unified-circuit", Status = CapabilityStatus.Failed, FailureCode = error.GetType().Name, Tokens = TokenTotals.Zero, EstimatedCostUsd = 0, RequestIds = [], Limitations = [] };
        }

        var summary = new ContractSummary
        {
            Provider = metadata.Provider, Model = metadata.Model, DateUtc = arguments.UtcDate,
            PerProviderMaxCostUsd = arguments.MaxPerProviderCostUsd, WorstCaseEstimatedCostUsd = 0,
            ActualEstimatedCostUsd = 0, TotalTokens = TokenTotals.Zero, Packages = metadata.Packages,
            Capabilities = [capability], Limitations = [],
        };
        await WriteAsync(directory, summary, cancellationToken);
        return capability.Status == CapabilityStatus.Passed ? 0 : 1;
    }

    private static async Task<int> WriteUnsupportedAsync(string directory, CommandLineArguments arguments, string reason)
    {
        var summary = new ContractSummary
        {
            Provider = arguments.Provider, Model = "unavailable", DateUtc = arguments.UtcDate,
            PerProviderMaxCostUsd = arguments.MaxPerProviderCostUsd, WorstCaseEstimatedCostUsd = 0,
            ActualEstimatedCostUsd = null, TotalTokens = TokenTotals.Zero, Packages = [], Capabilities = [], Limitations = [reason],
        };
        await WriteAsync(directory, summary, CancellationToken.None);
        return 2;
    }

    private static async Task WriteAsync(string directory, ContractSummary summary, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(summary, JsonDefaults.SerializerOptions), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), $"# Provider contract — {summary.Provider}\n\nCapabilities: {summary.Capabilities.Count}\n", cancellationToken);
    }

    private sealed class PromptInput { [Required] public string Prompt { get; set; } = string.Empty; }
    private sealed class PromptOutput { [Required] public string Message { get; set; } = string.Empty; }
}
