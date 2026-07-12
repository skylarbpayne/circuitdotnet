using System.ComponentModel.DataAnnotations;
using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class ToolsVsSkillsExample
{
    public static AgentDefinition CreateAgent()
    {
        var styleGuide = SkillReference.CreateFile(
            "skill.support-style",
            "1.0.0",
            ["/srv/circuit/skills/support-style"],
            "Company-specific support guidance.");

        return new AgentDefinition(
                "triage.agent",
                "1.0.0",
                "Triage",
                "Use the attached skill for policy and call tools only when fresh data is required.")
            .WithSkills([styleGuide])
            .WithToolTags(["ticket.read", "ticket.write"]);
    }

    public static ToolDefinition<CustomerLookupInput, CustomerLookupOutput> CreateTool()
        => new ToolDefinition<CustomerLookupInput, CustomerLookupOutput>(
                "customer.lookup",
                "1.0.0",
                "Read-only customer lookup.",
                (context, input, cancellationToken) =>
                    Task.FromResult(new CustomerLookupOutput { CustomerId = input.CustomerId, Tier = "enterprise" }))
            .WithApproval(ToolApprovalMode.Never);

    internal sealed class CustomerLookupInput
    {
        [Required]
        public string CustomerId { get; set; } = string.Empty;
    }

    internal sealed class CustomerLookupOutput
    {
        [Required]
        public string CustomerId { get; set; } = string.Empty;

        [Required]
        public string Tier { get; set; } = string.Empty;
    }
}
