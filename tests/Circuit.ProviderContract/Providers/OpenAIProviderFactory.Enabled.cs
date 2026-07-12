using Circuit;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Circuit.ProviderContract.Providers;

internal sealed class OpenAIProviderFactory : IProviderFactory
{
    public string Name => "openai";

    public ProviderFactoryResult Create()
    {
        try
        {
            var model = ProviderFactoryHelpers.Required("OPENAI_MODEL");
            var apiKey = ProviderFactoryHelpers.Required("OPENAI_API_KEY");
            var inputCost = ProviderFactoryHelpers.RequiredDecimal("OPENAI_INPUT_COST_USD_PER_1K");
            var outputCost = ProviderFactoryHelpers.RequiredDecimal("OPENAI_OUTPUT_COST_USD_PER_1K");

            var metadata = ProviderFactoryHelpers.CreateMetadata(
                provider: Name,
                model: model,
                inputCostUsdPer1KTokens: inputCost,
                outputCostUsdPer1KTokens: outputCost,
                typeof(ChatClient).Assembly,
                typeof(OpenAIClientExtensions).Assembly);

            return ProviderFactoryResult.Success(
                metadata,
                () => ProviderFactoryHelpers.CreateHost(
                    metadata,
                    () => new ChatClient(model: model, apiKey: apiKey).AsIChatClient(),
                    providerSourceName: "provider-contract.openai"));
        }
        catch (Exception ex)
        {
            return ProviderFactoryResult.Unsupported(ex.Message);
        }
    }
}
