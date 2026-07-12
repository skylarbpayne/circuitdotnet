using System.Runtime.CompilerServices;
using Circuit.Core;
using Xunit;

namespace Circuit.Interop.Tests;

public sealed class InteractiveRuntimeInteropTests
{
    [Fact]
    public async Task third_party_runtime_can_return_public_agent_run_factory_handle()
    {
        var runtime = new ThirdPartyInteractiveRuntime();

        await using var run = await runtime.StartAsync<string, string>(
            null!,
            null!,
            "input",
            RunOptions.Default,
            CancellationToken.None);

        Assert.Equal("0123456789abcdef0123456789abcdef", run.RunId.Value);
        Assert.Same(runtime.Events, run.Events);

        var response = Circuit.Core.ApprovalResponse.Create("approval-1", true);
        await run.RespondAsync(response, CancellationToken.None);

        Assert.Same(response, runtime.Response);
    }

    private sealed class ThirdPartyInteractiveRuntime : IInteractiveCircuitRuntime
    {
        public IAsyncEnumerable<RunEvent<string>> Events { get; } = EmptyEvents();

        public Circuit.Core.ApprovalResponse? Response { get; private set; }

        public Task<AgentRun<TOutput>> StartAsync<TInput, TOutput>(
            Circuit.Core.AgentDefinition agent,
            Signature<TInput, TOutput> signature,
            TInput input,
            RunOptions options,
            CancellationToken cancellationToken)
        {
            Assert.Equal(typeof(string), typeof(TInput));
            Assert.Equal(typeof(string), typeof(TOutput));

            var events = (IAsyncEnumerable<RunEvent<TOutput>>)(object)Events;
            var run = AgentRun<TOutput>.Create(
                RunId.Parse("0123456789abcdef0123456789abcdef"),
                events,
                (response, _) =>
                {
                    Response = response;
                    return ValueTask.CompletedTask;
                },
                () => ValueTask.CompletedTask);

            return Task.FromResult(run);
        }

        private static async IAsyncEnumerable<RunEvent<string>> EmptyEvents(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
