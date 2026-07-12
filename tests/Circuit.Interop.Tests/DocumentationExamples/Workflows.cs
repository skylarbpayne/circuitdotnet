using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class WorkflowExample
{
    public static WorkflowDefinition<int, bool> Build(AgentDefinition agent, AgentSignature<int, bool> signature)
        => WorkflowDefinition<int, bool>
            .Start("approval.workflow", "1.0.0", "classify", agent, signature)
            .RequestApproval(
                "manager.approval",
                approved => new ApprovalPrompt($"Approve {approved}", "A human must approve before completion."))
            .Then(
                "decision",
                (context, response, cancellationToken) => Task.FromResult(response.Approved))
            .Build();
}
