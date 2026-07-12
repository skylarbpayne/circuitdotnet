using System.ComponentModel.DataAnnotations;
using Circuit;
using Microsoft.Extensions.AI;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class GettingStartedExample
{
    public static async Task<string> RunAsync(IChatClient chatClient, CancellationToken cancellationToken)
    {
        var client = new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(chatClient)
            .Build();

        var agent = new AgentDefinition("support.agent", "1.0.0", "Support", "Return only the structured answer.");
        var signature = new AgentSignature<TicketInput, TicketOutput>(
            "support.signature",
            "1.0.0",
            "Support answer",
            "Return category and summary.");

        var result = await client.RunAsync(agent, signature, new TicketInput { Message = "Reset my password." }, cancellationToken: cancellationToken);
        return result.Result.Value!.Summary;
    }

    internal sealed class TicketInput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class TicketOutput
    {
        [Required]
        public string Category { get; set; } = string.Empty;

        [Required]
        public string Summary { get; set; } = string.Empty;
    }
}
