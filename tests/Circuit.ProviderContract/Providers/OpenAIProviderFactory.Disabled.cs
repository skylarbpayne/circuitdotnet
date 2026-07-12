namespace Circuit.ProviderContract.Providers;

internal sealed class OpenAIProviderFactory : IProviderFactory
{
    public string Name => "openai";

    public ProviderFactoryResult Create()
        => ProviderFactoryResult.Unsupported(
            "OpenAI provider packages are disabled. Build with /p:EnableProviderPackages=true to enable the live contract runner.");
}
