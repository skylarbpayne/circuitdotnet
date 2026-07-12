using System.Globalization;
using System.Reflection;
using Circuit;
using Microsoft.Extensions.AI;

namespace Circuit.ProviderContract.Providers;

internal interface IProviderFactory
{
    string Name { get; }
    ProviderFactoryResult Create();
}

internal sealed class ProviderFactoryResult
{
    private ProviderFactoryResult(ProviderMetadata? metadata, Func<ScenarioHost>? createHost, string? failure)
    {
        Metadata = metadata;
        CreateHost = createHost;
        Failure = failure;
    }

    public ProviderMetadata? Metadata { get; }
    public Func<ScenarioHost>? CreateHost { get; }
    public string? Failure { get; }
    public bool IsSuccess => Metadata is not null && CreateHost is not null;

    public static ProviderFactoryResult Success(ProviderMetadata metadata, Func<ScenarioHost> createHost) => new(metadata, createHost, null);
    public static ProviderFactoryResult Unsupported(string failure) => new(null, null, failure);
}

internal static class ProviderFactoryRegistry
{
    private static readonly IReadOnlyDictionary<string, IProviderFactory> Factories =
        new Dictionary<string, IProviderFactory>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new OpenAIProviderFactory(),
            ["azure-openai"] = new AzureOpenAIProviderFactory(),
        };

    public static IProviderFactory Get(string provider)
        => Factories.TryGetValue(provider, out var factory)
            ? factory
            : throw new InvalidOperationException($"Unsupported provider '{provider}'. Expected one of: {string.Join(", ", Factories.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}.");

    public static IReadOnlyList<string> SupportedProviders => Factories.Keys.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal static class ProviderFactoryHelpers
{
    public static ProviderMetadata CreateMetadata(string provider, string model, decimal inputCostUsdPer1KTokens, decimal outputCostUsdPer1KTokens, params Assembly[] assemblies)
        => new()
        {
            Provider = provider,
            Model = model,
            InputCostUsdPer1KTokens = inputCostUsdPer1KTokens,
            OutputCostUsdPer1KTokens = outputCostUsdPer1KTokens,
            Packages = assemblies
                .Where(static assembly => assembly is not null)
                .Select(static assembly => new ProviderPackageMetadata
                {
                    Name = assembly.GetName().Name ?? assembly.FullName ?? "unknown",
                    Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? assembly.GetName().Version?.ToString()
                        ?? "unknown",
                })
                .OrderBy(static package => package.Name, StringComparer.Ordinal)
                .ToArray(),
        };

    public static ScenarioHost CreateHost(ProviderMetadata metadata, Func<IChatClient> createChatClient, string providerSourceName)
    {
        var recorder = new ProviderCallRecorder(createChatClient());
        var observer = new EnvelopeRunObserver();
        var telemetry = new TelemetryArtifacts(providerSourceName);
        return new ScenarioHost(metadata, recorder, observer, telemetry);
    }

    public static string Required(string name)
        => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? throw new InvalidOperationException($"{name} was not configured.")
            : Environment.GetEnvironmentVariable(name)!;

    public static decimal RequiredDecimal(string name)
        => decimal.TryParse(Required(name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{name} was not a valid decimal.");
}
