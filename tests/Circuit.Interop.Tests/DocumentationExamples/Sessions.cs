using System.Text.Json;
using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class SessionsExample
{
    public static async Task<CircuitSession> RoundTripAsync(
        IAgentClient client,
        AgentDefinition agent,
        CircuitSession session,
        CancellationToken cancellationToken)
    {
        JsonElement state = await client.SerializeSessionAsync(agent, session, cancellationToken);
        return await client.DeserializeSessionAsync(agent, state, cancellationToken);
    }
}
