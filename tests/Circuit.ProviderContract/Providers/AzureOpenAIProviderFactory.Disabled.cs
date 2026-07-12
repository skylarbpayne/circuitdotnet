namespace Circuit.ProviderContract.Providers;

internal sealed class AzureOpenAIProviderFactory : IProviderFactory
{
    public string Name => "azure-openai";

    public ProviderFactoryResult Create()
        => ProviderFactoryResult.Unsupported(
            "Azure OpenAI provider packages are disabled. Build with /p:EnableProviderPackages=true to enable the live contract runner.");
}
