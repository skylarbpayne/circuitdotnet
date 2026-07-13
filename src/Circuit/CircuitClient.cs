using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Circuit;

/// <summary>Runs every Circuit shape through one unified runtime.</summary>
public interface ICircuitClient
{
    /// <summary>Runs a Circuit that must produce exactly one output.</summary>
    Task<CircuitResponse<TOutput>> RunAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Collects outputs in completion order.</summary>
    Task<CircuitResponse<IReadOnlyList<CircuitResponse<TOutput>>>> CollectAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Collects outputs resequenced by source ordinal.</summary>
    Task<CircuitResponse<IReadOnlyList<CircuitResponse<TOutput>>>> CollectSourceOrderAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Streams completed root responses.</summary>
    IAsyncEnumerable<CircuitResponse<TOutput>> StreamAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starts the full event and approval protocol.</summary>
    Task<CircuitRun<TOutput>> StartAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        TInput input,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes an exact Circuit checkpoint.</summary>
    Task<CircuitRun<TOutput>> ResumeAsync<TInput, TOutput>(
        CircuitDefinition<TInput, TOutput> circuit,
        CircuitCheckpoint<TOutput> checkpoint,
        ResumeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Serializes adapter-owned session state.</summary>
    Task<JsonElement> SerializeSessionAsync(AgentDefinition agent, CircuitSession session, CancellationToken cancellationToken = default);
    /// <summary>Deserializes adapter-owned session state.</summary>
    Task<CircuitSession> DeserializeSessionAsync(AgentDefinition agent, JsonElement state, CancellationToken cancellationToken = default);
}

/// <summary>Builds a unified Circuit client.</summary>
public sealed class CircuitClientBuilder
{
    private readonly List<IToolResolver> _toolResolvers = [];
    private readonly List<ISkillResolver> _skillResolvers = [];
    private readonly List<IRunObserver> _runObservers = [];
    private readonly MicrosoftAgentFrameworkOptions _mafOptions = new();
    private IChatClient? _chatClient;
    private object? _runtime;

    /// <summary>Uses a Core-compatible runtime supplied by an adapter or Circuit.Testing.</summary>
    /// <remarks>The object must implement the runtime contract shipped by CircuitDotNet.Core.</remarks>
    public CircuitClientBuilder UseRuntime(object runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime is not Circuit.Core.ICircuitRuntime)
        {
            throw new ArgumentException("The supplied object does not implement the unified Circuit runtime contract.", nameof(runtime));
        }

        _runtime = runtime;
        return this;
    }

    /// <summary>Configures the client to use the Microsoft Agent Framework adapter.</summary>
    public CircuitClientBuilder UseMicrosoftAgentFramework(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        return this;
    }

    /// <summary>Configures Microsoft Agent Framework adapter options.</summary>
    public CircuitClientBuilder ConfigureMicrosoftAgentFramework(Action<MicrosoftAgentFrameworkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_mafOptions);
        return this;
    }

    /// <summary>Adds a tool resolver to the adapter pipeline.</summary>
    public CircuitClientBuilder AddToolResolver(IToolResolver resolver) { ArgumentNullException.ThrowIfNull(resolver); _toolResolvers.Add(resolver); return this; }
    /// <summary>Adds a skill resolver to the adapter pipeline.</summary>
    public CircuitClientBuilder AddSkillResolver(ISkillResolver resolver) { ArgumentNullException.ThrowIfNull(resolver); _skillResolvers.Add(resolver); return this; }
    /// <summary>Adds an observer for provider leaf activity.</summary>
    public CircuitClientBuilder AddRunObserver(IRunObserver observer) { ArgumentNullException.ThrowIfNull(observer); _runObservers.Add(observer); return this; }

    /// <summary>Builds an immutable Circuit client from the configured runtime.</summary>
    public ICircuitClient Build()
    {
        if (_runtime is Circuit.Core.ICircuitRuntime runtime) return CircuitClientFactory.Create(runtime);
        if (_chatClient is null) throw new InvalidOperationException("UseMicrosoftAgentFramework must be called before Build().");
        var factoryType = Type.GetType("Circuit.MicrosoftAgentFrameworkRuntimeFactory, Circuit.MicrosoftAgentFramework", false)
            ?? throw new InvalidOperationException("CircuitDotNet.MicrosoftAgentFramework is required to build a client.");
        var method = factoryType.GetMethod("CreateClient", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("MicrosoftAgentFrameworkRuntimeFactory.CreateClient was not found.");
        return (ICircuitClient)(method.Invoke(null, [_chatClient, _mafOptions.Snapshot(), _toolResolvers.AsReadOnly(), _skillResolvers.AsReadOnly(), _runObservers.AsReadOnly()])
            ?? throw new InvalidOperationException("CreateClient returned null."));
    }
}

internal static class CircuitClientFactory
{
    internal static ICircuitClient Create(Circuit.Core.ICircuitRuntime runtime) => new CircuitRuntimeBackedClient(runtime);
}

internal sealed class CircuitRuntimeBackedClient(Circuit.Core.ICircuitRuntime runtime) : ICircuitClient
{
    internal Circuit.Core.ICircuitRuntime Runtime => runtime;

    /// <summary>Runs a Circuit and projects its single response.</summary>
    public async Task<CircuitResponse<TOutput>> RunAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, TInput input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => new(await Circuit.Core.Circuit.run(runtime, circuit.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false));

    /// <summary>Collects all Circuit lane responses in completion order.</summary>
    public async Task<CircuitResponse<IReadOnlyList<CircuitResponse<TOutput>>>> CollectAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, TInput input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => MapCollection(await Circuit.Core.Circuit.collect(runtime, circuit.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false));

    /// <summary>Collects all Circuit lane responses in source order.</summary>
    public async Task<CircuitResponse<IReadOnlyList<CircuitResponse<TOutput>>>> CollectSourceOrderAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, TInput input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => MapCollection(await Circuit.Core.Circuit.collectSourceOrder(runtime, circuit.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false));

    /// <summary>Streams completed Circuit lane responses.</summary>
    public IAsyncEnumerable<CircuitResponse<TOutput>> StreamAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, TInput input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => ConvertStream(Circuit.Core.Circuit.stream(runtime, circuit.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken), cancellationToken);

    /// <summary>Starts an interactive Circuit run.</summary>
    public async Task<CircuitRun<TOutput>> StartAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, TInput input, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => new(await runtime.StartAsync(circuit.Inner, input, (options ?? new AgentRunOptions()).ToCore(), cancellationToken).ConfigureAwait(false));

    /// <summary>Resumes an interactive Circuit run from a checkpoint.</summary>
    public async Task<CircuitRun<TOutput>> ResumeAsync<TInput, TOutput>(CircuitDefinition<TInput, TOutput> circuit, CircuitCheckpoint<TOutput> checkpoint, ResumeOptions? options = null, CancellationToken cancellationToken = default)
        => new(await runtime.ResumeAsync(circuit.Inner, checkpoint.Inner, (options ?? new ResumeOptions()).ToCore(), cancellationToken).ConfigureAwait(false));

    /// <summary>Serializes adapter-owned provider session state.</summary>
    public async Task<JsonElement> SerializeSessionAsync(AgentDefinition agent, CircuitSession session, CancellationToken cancellationToken = default)
        => await runtime.SerializeSessionAsync(agent.Inner, session.Inner, cancellationToken).AsTask().ConfigureAwait(false);

    /// <summary>Deserializes adapter-owned provider session state.</summary>
    public async Task<CircuitSession> DeserializeSessionAsync(AgentDefinition agent, JsonElement state, CancellationToken cancellationToken = default)
        => CircuitSession.FromCore(await runtime.DeserializeSessionAsync(agent.Inner, state, cancellationToken).AsTask().ConfigureAwait(false));

    private static CircuitResponse<IReadOnlyList<CircuitResponse<T>>> MapCollection<T>(Circuit.Core.Response<IReadOnlyList<Circuit.Core.Response<T>>> result)
    {
        var metadata = new CircuitResponseMetadata(result.Metadata);
        return result.IsSuccess
            ? new(CircuitOutcome<IReadOnlyList<CircuitResponse<T>>>.Success(result.Value.Select(item => new CircuitResponse<T>(item)).ToArray()), metadata)
            : new(CircuitOutcome<IReadOnlyList<CircuitResponse<T>>>.Failed(new CircuitFailure(result.Failure)), metadata);
    }

    private static async IAsyncEnumerable<CircuitResponse<T>> ConvertStream<T>(
        IAsyncEnumerable<Circuit.Core.Response<T>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new CircuitResponse<T>(item);
        }
    }
}
