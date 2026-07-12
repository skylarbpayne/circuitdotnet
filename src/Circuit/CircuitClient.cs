#pragma warning disable CS1591

using System.Reflection;
using System.Text.Json;
using Circuit.Core;
using Microsoft.Extensions.AI;

namespace Circuit;

public interface IAgentClient
{
    Task<AgentRunResult<TOutput>> RunAsync<TInput, TOutput>(
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentRunEvent<TOutput>> RunStreamingAsync<TInput, TOutput>(
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SerializeSessionAsync(
        AgentDefinition agent,
        CircuitSession session,
        CancellationToken cancellationToken = default);

    Task<CircuitSession> DeserializeSessionAsync(
        AgentDefinition agent,
        JsonElement state,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowClient
{
    Task<AgentRunResult<TOutput>> RunWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        TInput input,
        WorkflowRunOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun<TOutput>> StartWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        TInput input,
        WorkflowRunOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowRun<TOutput>> ResumeWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        WorkflowCheckpoint<TOutput> checkpoint,
        CancellationToken cancellationToken = default);
}

public interface ICircuitClient : IAgentClient, IWorkflowClient;

public sealed class CircuitClientBuilder
{
    private readonly List<IToolResolver> _toolResolvers = [];
    private readonly List<ISkillResolver> _skillResolvers = [];
    private readonly List<IRunObserver> _runObservers = [];
    private readonly MicrosoftAgentFrameworkOptions _mafOptions = new();
    private IChatClient? _chatClient;

    public CircuitClientBuilder UseMicrosoftAgentFramework(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        return this;
    }

    public CircuitClientBuilder ConfigureMicrosoftAgentFramework(Action<MicrosoftAgentFrameworkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_mafOptions);
        return this;
    }

    public CircuitClientBuilder AddToolResolver(IToolResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _toolResolvers.Add(resolver);
        return this;
    }

    public CircuitClientBuilder AddSkillResolver(ISkillResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _skillResolvers.Add(resolver);
        return this;
    }

    public CircuitClientBuilder AddRunObserver(IRunObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _runObservers.Add(observer);
        return this;
    }

    public ICircuitClient Build()
    {
        if (_chatClient is null)
        {
            throw new InvalidOperationException("UseMicrosoftAgentFramework must be called before Build().");
        }

        var factoryType = Type.GetType(
            "Circuit.MicrosoftAgentFrameworkRuntimeFactory, Circuit.MicrosoftAgentFramework",
            throwOnError: false);

        if (factoryType is null)
        {
            throw new InvalidOperationException(
                "Circuit.MicrosoftAgentFramework is required to build a client. Add a reference to the CircuitDotNet.MicrosoftAgentFramework package.");
        }

        var method = factoryType.GetMethod(
            "CreateClient",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(IChatClient), typeof(MicrosoftAgentFrameworkOptions), typeof(IReadOnlyList<IToolResolver>), typeof(IReadOnlyList<ISkillResolver>), typeof(IReadOnlyList<IRunObserver>)],
            modifiers: null);

        if (method is null)
        {
            throw new InvalidOperationException("Circuit.MicrosoftAgentFrameworkRuntimeFactory.CreateClient was not found.");
        }

        var client = method.Invoke(
            obj: null,
            parameters:
            [
                _chatClient,
                _mafOptions.Snapshot(),
                _toolResolvers.AsReadOnly(),
                _skillResolvers.AsReadOnly(),
                _runObservers.AsReadOnly(),
            ]);

        return (ICircuitClient?)client
            ?? throw new InvalidOperationException("Circuit.MicrosoftAgentFrameworkRuntimeFactory.CreateClient returned null.");
    }

}

internal static class CircuitClientFactory
{
    internal static ICircuitClient Create(ICircuitRuntime runtime)
        => Create(runtime, runtime as IWorkflowRuntime);

    internal static ICircuitClient Create(ICircuitRuntime runtime, IWorkflowRuntime? workflowRuntime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new CircuitRuntimeBackedClient(runtime, workflowRuntime);
    }
}

internal sealed class CircuitRuntimeBackedClient(ICircuitRuntime runtime, IWorkflowRuntime? workflowRuntime) : ICircuitClient
{
    internal ICircuitRuntime Runtime => runtime;

    private IWorkflowRuntime RequireWorkflowRuntime()
        => workflowRuntime
            ?? throw new InvalidOperationException(
                "The registered Circuit runtime does not support workflows. Register an IWorkflowRuntime to enable workflow operations.");

    public async Task<AgentRunResult<TOutput>> RunAsync<TInput, TOutput>(
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(signature);
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var result = await runtime.RunAsync(agent.Inner, signature.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false);
        return AgentRunResult<TOutput>.FromCore(result);
    }

    public async IAsyncEnumerable<AgentRunEvent<TOutput>> RunStreamingAsync<TInput, TOutput>(
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature,
        TInput input,
        AgentRunOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(signature);
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        await foreach (var item in runtime.RunStreamingAsync(agent.Inner, signature.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false))
        {
            yield return AgentRunEvent<TOutput>.FromCore(item);
        }
    }

    public async Task<JsonElement> SerializeSessionAsync(
        AgentDefinition agent,
        CircuitSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        return await runtime.SerializeSessionAsync(agent.Inner, session.Inner, cancellationToken).AsTask().ConfigureAwait(false);
    }

    public async Task<CircuitSession> DeserializeSessionAsync(
        AgentDefinition agent,
        JsonElement state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var session = await runtime.DeserializeSessionAsync(agent.Inner, state, cancellationToken).AsTask().ConfigureAwait(false);
        return CircuitSession.FromCore(session);
    }

    public async Task<AgentRunResult<TOutput>> RunWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        TInput input,
        WorkflowRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var result = await RequireWorkflowRuntime().RunAsync(workflow.Inner, input, (options ?? new WorkflowRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false);
        return AgentRunResult<TOutput>.FromCore(result);
    }

    public async Task<WorkflowRun<TOutput>> StartWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        TInput input,
        WorkflowRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var run = await RequireWorkflowRuntime().StartAsync(workflow.Inner, input, (options ?? new WorkflowRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false);
        return new WorkflowRun<TOutput>(run);
    }

    public async Task<WorkflowRun<TOutput>> ResumeWorkflowAsync<TInput, TOutput>(
        WorkflowDefinition<TInput, TOutput> workflow,
        WorkflowCheckpoint<TOutput> checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(checkpoint);
        var run = await RequireWorkflowRuntime().ResumeAsync(workflow.Inner, checkpoint.Inner, cancellationToken).ConfigureAwait(false);
        return new WorkflowRun<TOutput>(run);
    }
}
