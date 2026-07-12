using Circuit;
using Circuit.MicrosoftAgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class ObservabilityExample
{
    public static IServiceCollection AddObservedCircuit(IServiceCollection services, IChatClient chatClient)
    {
        services.AddSingleton(chatClient);
        services.AddCircuit(options =>
        {
            options.AddRunObserver(
                new OpenTelemetryRunObserver(new OpenTelemetryRunObserverOptions
                {
                    CaptureOutput = true,
                    Redactor = text => text.Replace("secret", "[redacted]", StringComparison.OrdinalIgnoreCase),
                }));
        });

        return services;
    }
}
