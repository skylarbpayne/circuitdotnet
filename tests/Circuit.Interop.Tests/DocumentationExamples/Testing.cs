using System.ComponentModel.DataAnnotations;
using Circuit;
using Circuit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class TestingExample
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var runtime = new ScriptedRuntime([ScriptedResponses.OutputJson("{\"message\":\"pong\"}")]);
        var services = new ServiceCollection();
        services.AddSingleton<Circuit.Core.ICircuitRuntime>(runtime);
        services.AddCircuit(_ => { });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ICircuitClient>();
        var agent = new AgentDefinition("testing.agent", "1.0.0", "Testing", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("testing.signature", "1.0.0", "Ping", "Return pong.");

        var result = await client.RunAsync(agent, signature, new PingInput { Message = "ping" }, cancellationToken: cancellationToken);
        var events = new List<AgentRunEvent<PongOutput>>();

        await foreach (var @event in client.RunStreamingAsync(agent, signature, new PingInput { Message = "ping" }, cancellationToken: cancellationToken))
        {
            events.Add(@event);
        }

        RunAssertions.AssertMonotonicSequence(events);
        RunAssertions.AssertTerminalEventCount(events, 1);
        _ = result.Result.Value!.Message;
    }

    internal sealed class PingInput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    internal sealed class PongOutput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }
}
