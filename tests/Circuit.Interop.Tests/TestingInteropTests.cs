using Circuit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Circuit.Interop.Tests;

public sealed class TestingInteropTests
{
    private sealed class PingInput
    {
        public string Message { get; set; } = string.Empty;
    }

    private sealed class PongOutput
    {
        public string Message { get; set; } = string.Empty;
    }

    [Fact]
    public async Task scripted_runtime_drives_the_public_csharp_client_and_assertions()
    {
        var runtime = new ScriptedRuntime(
        [
            ScriptedResponses.OutputJson("{\"message\":\"pong\"}"),
            ScriptedResponses.Stream(["{\"message\":\"po", "ng\"}"])
        ]);

        var services = new ServiceCollection();
        services.AddSingleton<Circuit.Core.ICircuitRuntime>(runtime);
        services.AddCircuit(_ => { });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ICircuitClient>();
        var agent = new AgentDefinition("testing.agent", "1.0.0", "Testing Agent", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("testing.signature", "1.0.0", "Ping", "Return pong.");

        var result = await client.RunAsync(agent, signature, new PingInput { Message = "ping" });

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("pong", result.Result.Value!.Message);

        var events = new List<AgentRunEvent<PongOutput>>();
        await foreach (var @event in client.RunStreamingAsync(agent, signature, new PingInput { Message = "stream" }))
        {
            events.Add(@event);
        }

        RunAssertions.AssertMonotonicSequence(events);
        RunAssertions.AssertTerminalEventCount(events, 1);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, runtime.RemainingResponses);
        Assert.Contains(runtime.Calls, call => call.InputJson.Contains("ping", StringComparison.Ordinal));
        Assert.Contains(runtime.Calls, call => call.InputJson.Contains("stream", StringComparison.Ordinal));
    }
}
