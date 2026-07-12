using System.ComponentModel.DataAnnotations;
using Circuit;
using Microsoft.Extensions.AI;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class StructuredOutputExample
{
    public static Task<AgentRunResult<ContactSummary>> RunWithNativeOnlyAsync(
        ICircuitClient client,
        CancellationToken cancellationToken)
    {
        var options = new AgentRunOptions
        {
            StructuredOutputPolicy = StructuredOutputPolicy.NativeOnly,
        };

        return client.RunAsync(
            new AgentDefinition("contacts.agent", "1.0.0", "Contacts", "Return only structured JSON."),
            new AgentSignature<ContactRequest, ContactSummary>("contacts.signature", "1.0.0", "Contact summary", "Return name and status."),
            new ContactRequest { Message = "Summarize Ada Lovelace." },
            options,
            cancellationToken);
    }

    public static ICircuitClient BuildRepairingClient(IChatClient primaryClient, IChatClient repairClient)
        => new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(primaryClient)
            .ConfigureMicrosoftAgentFramework(options => options.SecondaryStructuredOutputClient = repairClient)
            .Build();

    internal sealed class ContactRequest
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class ContactSummary
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string Status { get; set; } = string.Empty;
    }
}
