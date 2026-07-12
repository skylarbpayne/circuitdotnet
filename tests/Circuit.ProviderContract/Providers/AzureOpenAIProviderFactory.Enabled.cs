using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Circuit.ProviderContract.Providers;

internal sealed class AzureOpenAIProviderFactory : IProviderFactory
{
    public string Name => "azure-openai";

    public ProviderFactoryResult Create()
    {
        try
        {
            var endpoint = ProviderFactoryHelpers.Required("AZURE_OPENAI_ENDPOINT");
            var deployment = ProviderFactoryHelpers.Required("AZURE_OPENAI_DEPLOYMENT");
            var apiKey = ProviderFactoryHelpers.Required("AZURE_OPENAI_API_KEY");
            var model = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL") ?? deployment;
            var inputCost = ProviderFactoryHelpers.RequiredDecimal("AZURE_OPENAI_INPUT_COST_USD_PER_1K");
            var outputCost = ProviderFactoryHelpers.RequiredDecimal("AZURE_OPENAI_OUTPUT_COST_USD_PER_1K");

            var metadata = ProviderFactoryHelpers.CreateMetadata(
                provider: Name,
                model: model,
                inputCostUsdPer1KTokens: inputCost,
                outputCostUsdPer1KTokens: outputCost,
                typeof(AzureOpenAIClient).Assembly,
                typeof(ChatClient).Assembly,
                typeof(OpenAIClientExtensions).Assembly);

            return ProviderFactoryResult.Success(
                metadata,
                () => ProviderFactoryHelpers.CreateHost(
                    metadata,
                    () => new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
                        .GetChatClient(deployment)
                        .AsIChatClient(),
                    providerSourceName: "provider-contract.azure-openai"));
        }
        catch (Exception ex)
        {
            return ProviderFactoryResult.Unsupported(ex.Message);
        }
    }
}
