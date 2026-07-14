using Circuit;

namespace DocumentationExamples;

internal static class Sessions
{
    internal static CircuitDefinition<string, string> Create()
    {
        var agent = new AgentDefinition("docs-agent", "1.0.0", "Docs", "Return output.");
        var signature = new AgentSignature<string, string>("docs-signature", "1.0.0", "Docs", "Return output.");
        return CircuitDefinition<string, string>.FromAgent(agent, signature).Define("docs-sessions", "1.0.0");
    }
}
