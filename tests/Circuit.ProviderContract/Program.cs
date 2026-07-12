using Circuit.ProviderContract;
using Circuit.ProviderContract.Providers;

var arguments = CommandLineArguments.Parse(args);
var result = await ContractRunner.RunAsync(arguments, CancellationToken.None);
return result;

internal sealed class CommandLineArguments
{
    public required string Provider { get; init; }
    public required string ArtifactRoot { get; init; }
    public required string UtcDate { get; init; }
    public required decimal MaxCostUsd { get; init; }

    public static CommandLineArguments Parse(string[] args)
    {
        string? provider = null;
        string artifactRoot = Path.Combine(Environment.CurrentDirectory, "artifacts", "provider-contract");
        string utcDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        decimal? maxCostUsd = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--provider":
                    provider = RequireValue(args, ++index, "--provider");
                    break;
                case "--artifact-root":
                    artifactRoot = RequireValue(args, ++index, "--artifact-root");
                    break;
                case "--utc-date":
                    utcDate = RequireValue(args, ++index, "--utc-date");
                    break;
                case "--max-cost-usd":
                    maxCostUsd = decimal.Parse(RequireValue(args, ++index, "--max-cost-usd"), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument '{args[index]}'. Use --help for usage.");
            }
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException("--provider is required.");
        }

        if (!maxCostUsd.HasValue || maxCostUsd.Value <= 0)
        {
            throw new InvalidOperationException("--max-cost-usd must be greater than zero.");
        }

        return new CommandLineArguments
        {
            Provider = provider,
            ArtifactRoot = Path.GetFullPath(artifactRoot),
            UtcDate = utcDate,
            MaxCostUsd = maxCostUsd.Value,
        };
    }

    private static string RequireValue(string[] args, int index, string option)
        => index >= args.Length ? throw new InvalidOperationException($"{option} requires a value.") : args[index];

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --project tests/Circuit.ProviderContract/Circuit.ProviderContract.csproj -- --provider <name> --max-cost-usd <amount> [--artifact-root <path>] [--utc-date <yyyy-MM-dd>]");
        Console.WriteLine($"Supported providers: {string.Join(", ", ProviderFactoryRegistry.SupportedProviders)}");
    }
}
