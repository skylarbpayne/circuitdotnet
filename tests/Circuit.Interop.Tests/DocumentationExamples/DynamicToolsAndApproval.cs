using System.ComponentModel.DataAnnotations;
using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class DynamicToolsAndApprovalExample
{
    public static CircuitClientBuilder Configure(CircuitClientBuilder builder)
        => builder.AddToolResolver(new TenantToolResolver());

    private sealed class TenantToolResolver : IToolResolver
    {
        public ValueTask<IReadOnlyList<ResolvedTool>> ResolveAsync(
            ToolResolutionContext context,
            CancellationToken cancellationToken)
        {
            var tools = new List<ResolvedTool>();

            tools.Add(new ToolDefinition<SearchInput, SearchOutput>(
                    "kb.search",
                    "1.0.0",
                    "Read-only knowledge-base search.",
                    (toolContext, input, ct) => Task.FromResult(new SearchOutput { Result = $"tenant={context.TenantId}: {input.Query}" }))
                .WithApproval(ToolApprovalMode.Never)
                .ToResolvedTool(["kb.read"]));

            if (string.Equals(context.TenantId, "enterprise", StringComparison.Ordinal))
            {
                tools.Add(new ToolDefinition<WriteInput, WriteOutput>(
                        "ticket.escalate",
                        "1.0.0",
                        "Escalate a ticket.",
                        (toolContext, input, ct) => Task.FromResult(new WriteOutput { TicketId = input.TicketId, Status = "queued" }))
                    .WithApproval(ToolApprovalMode.Always)
                    .ToResolvedTool(["ticket.write"]));
            }

            return ValueTask.FromResult<IReadOnlyList<ResolvedTool>>(tools);
        }
    }

    internal sealed class SearchInput
    {
        [Required]
        public string Query { get; set; } = string.Empty;
    }

    internal sealed class SearchOutput
    {
        [Required]
        public string Result { get; set; } = string.Empty;
    }

    internal sealed class WriteInput
    {
        [Required]
        public string TicketId { get; set; } = string.Empty;
    }

    internal sealed class WriteOutput
    {
        [Required]
        public string TicketId { get; set; } = string.Empty;

        [Required]
        public string Status { get; set; } = string.Empty;
    }
}
