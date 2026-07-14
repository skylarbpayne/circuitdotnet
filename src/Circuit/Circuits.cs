using Microsoft.FSharp.Core;

namespace Circuit;

/// <summary>An immutable graph-backed Circuit definition.</summary>
public sealed class CircuitDefinition<TInput, TOutput>
{
    internal CircuitDefinition(Circuit.Core.Circuit<TInput, TOutput> inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Graph = new(inner.Graph);
    }

    internal Circuit.Core.Circuit<TInput, TOutput> Inner { get; }
    /// <summary>Gets the id.</summary>
    public string Id => Inner.Id.Value;
    /// <summary>Gets the version.</summary>
    public string Version => Inner.Version.ToString();
    /// <summary>Gets the fingerprint.</summary>
    public string Fingerprint => Inner.Fingerprint;
    /// <summary>Gets the checkpointability.</summary>
    public CircuitCheckpointability Checkpointability => (CircuitCheckpointability)Inner.Checkpointability;
    /// <summary>Gets immutable topology, cardinality, resource, validation, and fingerprint metadata.</summary>
    public CircuitGraphDescriptor Graph { get; }

    /// <summary>Creates a Circuit backed by one typed agent leaf.</summary>
    public static CircuitDefinition<TInput, TOutput> FromAgent(
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(signature);
        return new(Circuit.Core.Circuit.agent<TInput, TOutput>(agent.Inner, signature.Inner));
    }

    /// <summary>Creates a Circuit backed by trusted asynchronous host code.</summary>
    public static CircuitDefinition<TInput, TOutput> FromCode(
        string id,
        string version,
        Func<CircuitContext, TInput, CancellationToken, Task<TOutput>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var coreHandler = Curry<Circuit.Core.CircuitContext, TInput, Task<Circuit.Core.Response<TOutput>>>(async (context, input) =>
            Circuit.Core.ResponseModule.succeed(context, await handler(new CircuitContext(context), input, context.CancellationToken).ConfigureAwait(false)));
        return new(Circuit.Core.Circuit.code<TInput, TOutput>(id, version, coreHandler));
    }

    /// <summary>Creates trusted code that can return either success or a typed failure.</summary>
    public static CircuitDefinition<TInput, TOutput> FromCodeResponse(
        string id,
        string version,
        Func<CircuitContext, TInput, CancellationToken, Task<CircuitResponse<TOutput>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var coreHandler = Curry<Circuit.Core.CircuitContext, TInput, Task<Circuit.Core.Response<TOutput>>>(async (context, input) =>
            (await handler(new CircuitContext(context), input, context.CancellationToken).ConfigureAwait(false)).Inner!);
        return new(Circuit.Core.Circuit.code<TInput, TOutput>(id, version, coreHandler));
    }

    /// <summary>Creates a code node that always produces an expected failure.</summary>
    public static CircuitDefinition<TInput, TOutput> Failure(
        string id,
        string version,
        Func<TInput, CircuitFailure> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var coreHandler = Curry<Circuit.Core.CircuitContext, TInput, Task<Circuit.Core.Response<TOutput>>>((context, input) =>
            Task.FromResult(Circuit.Core.ResponseModule.fail<TOutput>(context, failure(input).Inner)));
        return new(Circuit.Core.Circuit.code<TInput, TOutput>(id, version, coreHandler));
    }

    /// <summary>Creates a constant Circuit whose value is snapshotted by serialization.</summary>
    public static CircuitDefinition<TInput, TOutput> Value(TOutput value)
        => new(Circuit.Core.Circuit.value<TOutput, TInput>(value));

    /// <summary>Creates a finite source with ordinal item keys.</summary>
    public static CircuitDefinition<TInput, TItem> Items<TItem>(
        string id,
        string version,
        Func<TInput, IReadOnlyList<TItem>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(Circuit.Core.Circuit.items<TInput, TItem>(id, version, ToFSharp(items)));
    }

    /// <summary>Creates a finite source with caller-supplied stable item keys.</summary>
    public static CircuitDefinition<TInput, TItem> KeyedItems<TItem>(
        string id,
        string version,
        Func<TItem, string> key,
        Func<TInput, IReadOnlyList<TItem>> items)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(items);
        return new(Circuit.Core.Circuit.keyedItems<TItem, TInput>(id, version, ToFSharp(key), ToFSharp(items)));
    }

    /// <summary>Creates a durable cursor-aware source.</summary>
    public static CircuitDefinition<TInput, TItem> Source<TItem>(
        string id,
        string version,
        IResumableCircuitSource<TInput, TItem> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(Circuit.Core.Circuit.source<TInput, TItem>(id, version, new CoreResumableSource<TInput, TItem>(source)));
    }

    /// <summary>Creates a non-checkpointable asynchronous source.</summary>
    public static CircuitDefinition<TInput, TItem> AsyncItems<TItem>(
        string id,
        string version,
        Func<TInput, IAsyncEnumerable<TItem>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(Circuit.Core.Circuit.asyncSource<TInput, TItem>(id, version, ToFSharp(items)));
    }

    /// <summary>Creates a Circuit that pauses for a host approval response.</summary>
    public static CircuitDefinition<TInput, ApprovalResponse> Approval(
        string id,
        string version,
        Func<TInput, ApprovalPrompt> prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var approval = Circuit.Core.Circuit.approval<TInput>(
            id,
            version,
            ToFSharp<TInput, Circuit.Core.ApprovalPrompt>(input => prompt(input).ToCore()));
        var map = Circuit.Core.Circuit.code<Circuit.Core.ApprovalResponse, ApprovalResponse>(
            $"{id}-response",
            version,
            Curry<Circuit.Core.CircuitContext, Circuit.Core.ApprovalResponse, Task<Circuit.Core.Response<ApprovalResponse>>>((context, response) =>
                Task.FromResult(Circuit.Core.ResponseModule.succeed(context, ApprovalResponse.FromCore(response)))));
        return new(Circuit.Core.Circuit.thenStep(map, approval));
    }

    /// <summary>Creates a Circuit that selects one named branch per input.</summary>
    public static CircuitDefinition<TInput, TOutput> Branch(
        string id,
        string version,
        Func<TInput, string> route,
        IReadOnlyDictionary<string, CircuitDefinition<TInput, TOutput>> branches,
        CircuitDefinition<TInput, TOutput>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(branches);
        var coreBranches = branches.ToDictionary(pair => pair.Key, pair => pair.Value.Inner);
        var coreFallback = fallback is null
            ? FSharpValueOption<Circuit.Core.Circuit<TInput, TOutput>>.None
            : FSharpValueOption<Circuit.Core.Circuit<TInput, TOutput>>.Some(fallback.Inner);
        return new(Circuit.Core.Circuit.branch(id, version, ToFSharp(route), coreBranches, coreFallback));
    }

    /// <summary>Creates a bounded merge of independent Circuit branches.</summary>
    public static CircuitDefinition<TInput, TOutput> Merge(
        string id,
        string version,
        int maxConcurrency,
        IReadOnlyList<CircuitDefinition<TInput, TOutput>> branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        return new(Circuit.Core.Circuit.merge<TInput, TOutput>(id, version, maxConcurrency, branches.Select(branch => branch.Inner).ToArray()));
    }

    /// <summary>Creates a bounded loop over one value type.</summary>
    public static CircuitDefinition<TValue, TValue> Loop<TValue>(
        string id,
        string version,
        int maxIterations,
        Func<TValue, bool> whileTrue,
        CircuitDefinition<TValue, TValue> body)
    {
        ArgumentNullException.ThrowIfNull(whileTrue);
        ArgumentNullException.ThrowIfNull(body);
        return new(Circuit.Core.Circuit.loop<TValue>(id, version, maxIterations, ToFSharp(whileTrue), body.Inner));
    }

    /// <summary>Assigns the root definition identity and semantic version.</summary>
    public CircuitDefinition<TInput, TOutput> Define(string id, string version)
        => new(Circuit.Core.Circuit.define<TInput, TOutput>(id, version, Inner));

    /// <summary>Adds a stable name segment to this Circuit graph.</summary>
    public CircuitDefinition<TInput, TOutput> Named(string id)
        => new(Circuit.Core.Circuit.named(id, Inner));

    /// <summary>Routes every successful lane response into the next Circuit.</summary>
    public CircuitDefinition<TInput, TNext> Then<TNext>(CircuitDefinition<TOutput, TNext> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new(Circuit.Core.Circuit.thenStep(next.Inner, Inner));
    }

    /// <summary>Materializes and evaluates a validated dynamic child for each successful lane.</summary>
    public CircuitDefinition<TInput, TNext> ThenDynamic<TNext>(
        string id,
        string version,
        Func<TOutput, string> key,
        int maxConcurrency,
        Func<TOutput, CircuitDefinition<TOutput, TNext>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        return new(Circuit.Core.Circuit.thenDynamic(
            id,
            version,
            ToFSharp(key),
            maxConcurrency,
            ToFSharp<TOutput, Circuit.Core.Circuit<TOutput, TNext>>(value => factory(value).Inner),
            Inner));
    }

    /// <summary>Turns either lane outcome into a successful facade response value.</summary>
    public CircuitDefinition<TInput, CircuitResponse<TOutput>> Attempt()
    {
        var attempted = Circuit.Core.Circuit.attempt(Inner);
        var map = Circuit.Core.Circuit.code<Circuit.Core.Response<TOutput>, CircuitResponse<TOutput>>(
            "map-attempt-response",
            "1.0.0",
            Curry<Circuit.Core.CircuitContext, Circuit.Core.Response<TOutput>, Task<Circuit.Core.Response<CircuitResponse<TOutput>>>>((context, response) =>
                Task.FromResult(Circuit.Core.ResponseModule.succeed(context, new CircuitResponse<TOutput>(response)))));
        return new(Circuit.Core.Circuit.thenStep(map, attempted));
    }

    /// <summary>Recovers failed lane responses with trusted host code.</summary>
    public CircuitDefinition<TInput, TOutput> Recover(string id, string version, Func<CircuitFailure, TOutput> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new(Circuit.Core.Circuit.recover(id, version, ToFSharp<Circuit.Core.CircuitFailure, TOutput>(failure => handler(new CircuitFailure(failure))), Inner));
    }

    /// <summary>Aggregates all lane responses into one newly created output value.</summary>
    public CircuitDefinition<TInput, TAggregate> Aggregate<TAggregate>(
        string id,
        string version,
        Func<CircuitContext, IReadOnlyList<CircuitResponse<TOutput>>, CancellationToken, Task<TAggregate>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var coreHandler = Curry3<Circuit.Core.CircuitContext, IReadOnlyList<Circuit.Core.Response<TOutput>>, CancellationToken, Task<Circuit.Core.Response<TAggregate>>>(async (context, responses, cancellationToken) =>
            Circuit.Core.ResponseModule.succeed(context, await handler(new CircuitContext(context), responses.Select(response => new CircuitResponse<TOutput>(response)).ToArray(), cancellationToken).ConfigureAwait(false)));
        return new(Circuit.Core.Circuit.aggregate<TOutput, TAggregate, TInput>(id, version, coreHandler, Inner));
    }

    /// <summary>Validates the validate operation.</summary>
    public IReadOnlyList<CircuitValidationIssue> Validate()
        => Circuit.Core.Circuit.validate(Inner)
            .Select(issue => new CircuitValidationIssue(issue.NodeId, issue.Code, issue.Message))
            .ToArray();

    private static FSharpFunc<TArg, TResult> ToFSharp<TArg, TResult>(Func<TArg, TResult> handler)
        => FSharpFunc<TArg, TResult>.FromConverter(new Converter<TArg, TResult>(handler));

    private static FSharpFunc<T1, FSharpFunc<T2, TResult>> Curry<T1, T2, TResult>(Func<T1, T2, TResult> handler)
        => FSharpFunc<T1, FSharpFunc<T2, TResult>>.FromConverter(
            new Converter<T1, FSharpFunc<T2, TResult>>(first => ToFSharp<T2, TResult>(second => handler(first, second))));

    private static FSharpFunc<T1, FSharpFunc<T2, FSharpFunc<T3, TResult>>> Curry3<T1, T2, T3, TResult>(Func<T1, T2, T3, TResult> handler)
        => FSharpFunc<T1, FSharpFunc<T2, FSharpFunc<T3, TResult>>>.FromConverter(
            new Converter<T1, FSharpFunc<T2, FSharpFunc<T3, TResult>>>(first =>
                FSharpFunc<T2, FSharpFunc<T3, TResult>>.FromConverter(
                    new Converter<T2, FSharpFunc<T3, TResult>>(second => ToFSharp<T3, TResult>(third => handler(first, second, third))))));
}

/// <summary>Describes one immutable Circuit definition validation issue.</summary>
public sealed class CircuitValidationIssue
{
    internal CircuitValidationIssue(Circuit.Core.CircuitValidationIssue inner)
        : this(inner.NodeId, inner.Code, inner.Message)
    {
    }

    /// <summary>Initializes a validation issue.</summary>
    public CircuitValidationIssue(string nodeId, string code, string message)
    {
        NodeId = nodeId;
        Code = code;
        Message = message;
    }

    /// <summary>Gets the node id.</summary>
    public string NodeId { get; }
    /// <summary>Gets the code.</summary>
    public string Code { get; }
    /// <summary>Gets the message.</summary>
    public string Message { get; }
}
