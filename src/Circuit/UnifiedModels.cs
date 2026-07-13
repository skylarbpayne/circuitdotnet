using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Reflection;

namespace Circuit;

/// <summary>Describes whether a Circuit can be checkpointed.</summary>
public enum CircuitCheckpointability
{
    /// <summary>Represents the checkpointable classification.</summary>
    Checkpointable = 0,
    /// <summary>Represents the codec dependent classification.</summary>
    CodecDependent = 1,
    /// <summary>Represents the not checkpointable classification.</summary>
    NotCheckpointable = 2,
}

/// <summary>Classifies an immutable Circuit graph node without exposing executable payloads.</summary>
public enum CircuitNodeKind
{
    /// <summary>An agent provider leaf.</summary>
    Agent = 0,
    /// <summary>A trusted host-code leaf.</summary>
    Code = 1,
    /// <summary>An immutable serialized constant.</summary>
    Value = 2,
    /// <summary>A finite source.</summary>
    Items = 3,
    /// <summary>A non-durable asynchronous source.</summary>
    AsyncSource = 4,
    /// <summary>A durable cursor-aware source.</summary>
    ResumableSource = 5,
    /// <summary>A static pipeline edge.</summary>
    Then = 6,
    /// <summary>A dynamic child factory.</summary>
    Dynamic = 7,
    /// <summary>A failure-capturing node.</summary>
    Attempt = 8,
    /// <summary>A lane recovery node.</summary>
    Recover = 9,
    /// <summary>A static branch selector.</summary>
    Branch = 10,
    /// <summary>A bounded branch merge.</summary>
    Merge = 11,
    /// <summary>A bounded loop.</summary>
    Loop = 12,
    /// <summary>A host approval pause.</summary>
    Approval = 13,
    /// <summary>A lane aggregation node.</summary>
    Aggregate = 14,
    /// <summary>A stable graph naming node.</summary>
    Named = 15,
}

/// <summary>Describes statically inferred output cardinality for one admitted input.</summary>
public enum CircuitCardinality
{
    /// <summary>Exactly one output per admitted input.</summary>
    ExactlyOne = 0,
    /// <summary>Zero or more outputs per admitted input.</summary>
    Many = 1,
}

/// <summary>Provides one immutable, non-executable node view.</summary>
public sealed class CircuitNodeDescriptor
{
    internal CircuitNodeDescriptor(Circuit.Core.CircuitNodeDescriptor inner)
    {
        Path = inner.Path;
        Id = inner.Id;
        Kind = (CircuitNodeKind)inner.Kind;
        Version = inner.Version.IsSome ? inner.Version.Value : null;
        Cardinality = (CircuitCardinality)inner.Cardinality;
        Checkpointability = (CircuitCheckpointability)inner.Checkpointability;
        ConcurrencyLimit = inner.ConcurrencyLimit.IsSome ? inner.ConcurrencyLimit.Value : null;
        IterationLimit = inner.IterationLimit.IsSome ? inner.IterationLimit.Value : null;
        Children = Array.AsReadOnly(inner.Children.ToArray());
    }

    /// <summary>Gets path.</summary>
    public string Path { get; }
    /// <summary>Gets id.</summary>
    public string Id { get; }
    /// <summary>Gets kind.</summary>
    public CircuitNodeKind Kind { get; }
    /// <summary>Gets version.</summary>
    public string? Version { get; }
    /// <summary>Gets cardinality.</summary>
    public CircuitCardinality Cardinality { get; }
    /// <summary>Gets checkpointability.</summary>
    public CircuitCheckpointability Checkpointability { get; }
    /// <summary>Gets concurrencylimit.</summary>
    public int? ConcurrencyLimit { get; }
    /// <summary>Gets iterationlimit.</summary>
    public int? IterationLimit { get; }
    /// <summary>Gets child node paths.</summary>
    public IReadOnlyList<string> Children { get; }
}

/// <summary>Provides immutable Circuit topology, resource, validation, and fingerprint metadata.</summary>
public sealed class CircuitGraphDescriptor
{
    internal CircuitGraphDescriptor(Circuit.Core.CircuitGraphDescriptor inner)
    {
        Nodes = Array.AsReadOnly(inner.Nodes.Select(node => new CircuitNodeDescriptor(node)).ToArray());
        Cardinality = (CircuitCardinality)inner.Cardinality;
        Checkpointability = (CircuitCheckpointability)inner.Checkpointability;
        Fingerprint = inner.Fingerprint;
        ValidationIssues = Array.AsReadOnly(inner.ValidationIssues.Select(issue => new CircuitValidationIssue(issue)).ToArray());
        IsValid = inner.IsValid;
    }

    /// <summary>Gets nodes in deterministic topology order.</summary>
    public IReadOnlyList<CircuitNodeDescriptor> Nodes { get; }
    /// <summary>Gets cardinality.</summary>
    public CircuitCardinality Cardinality { get; }
    /// <summary>Gets checkpointability.</summary>
    public CircuitCheckpointability Checkpointability { get; }
    /// <summary>Gets fingerprint.</summary>
    public string Fingerprint { get; }
    /// <summary>Gets static validation issues.</summary>
    public IReadOnlyList<CircuitValidationIssue> ValidationIssues { get; }
    /// <summary>Gets whether static graph validation succeeded.</summary>
    public bool IsValid { get; }
}

/// <summary>Classifies a unified Circuit failure.</summary>
public enum CircuitFailureCode
{
    /// <summary>Represents the validation classification.</summary>
    Validation = 0,
    /// <summary>Represents the structured output unsupported classification.</summary>
    StructuredOutputUnsupported = 1,
    /// <summary>Represents the decode classification.</summary>
    Decode = 2,
    /// <summary>Represents the provider classification.</summary>
    Provider = 3,
    /// <summary>Represents the tool classification.</summary>
    Tool = 4,
    /// <summary>Represents the approval required classification.</summary>
    ApprovalRequired = 5,
    /// <summary>Represents the skill classification.</summary>
    Skill = 6,
    /// <summary>Represents the engine classification.</summary>
    Engine = 7,
    /// <summary>Represents the checkpoint mismatch classification.</summary>
    CheckpointMismatch = 8,
    /// <summary>Represents the cancelled classification.</summary>
    Cancelled = 9,
    /// <summary>Represents the not checkpointable classification.</summary>
    NotCheckpointable = 10,
    /// <summary>Represents the cardinality classification.</summary>
    Cardinality = 11,
    /// <summary>Represents the duplicate item key classification.</summary>
    DuplicateItemKey = 12,
    /// <summary>Represents the resource limit classification.</summary>
    ResourceLimit = 13,
    /// <summary>Represents the generated graph integrity classification.</summary>
    GeneratedGraphIntegrity = 14,
    /// <summary>Represents the invalid approval response classification.</summary>
    InvalidApprovalResponse = 15,
}

/// <summary>Represents an expected failure in the unified Circuit protocol.</summary>
public sealed class CircuitFailure
{
    internal CircuitFailure(Circuit.Core.CircuitFailure inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal Circuit.Core.CircuitFailure Inner { get; }
    /// <summary>Gets the code.</summary>
    public CircuitFailureCode Code => (CircuitFailureCode)Inner.Code;
    /// <summary>Gets the message.</summary>
    public string Message => Inner.Message;
    /// <summary>Gets the run id.</summary>
    public string? RunId => Inner.RunId.IsSome ? Inner.RunId.Value.Value : null;
    /// <summary>Gets the operation id.</summary>
    public string? OperationId => Inner.OperationId.IsSome ? Inner.OperationId.Value : null;
    /// <summary>Gets the request id.</summary>
    public string? RequestId => Inner.RequestId.IsSome ? Inner.RequestId.Value : null;
    /// <summary>Gets the exception.</summary>
    public Exception? Exception => Inner.Exception.IsSome ? Inner.Exception.Value : null;

    /// <summary>Creates the create operation.</summary>
    public static CircuitFailure Create(CircuitFailureCode code, string message)
        => new(Circuit.Core.CircuitFailure.Create((Circuit.Core.CircuitFailureCode)code, message));
}

/// <summary>Represents the successful or failed outcome of a Circuit response.</summary>
public sealed class CircuitOutcome<T>
{
    private CircuitOutcome(T? value, CircuitFailure? failure, bool succeeded)
    {
        Value = value;
        Failure = failure;
        IsSuccess = succeeded;
    }

    /// <summary>Gets whether the outcome succeeded.</summary>
    public bool IsSuccess { get; }
    /// <summary>Gets the value.</summary>
    public T? Value { get; }
    /// <summary>Gets the failure.</summary>
    public CircuitFailure? Failure { get; }

    internal static CircuitOutcome<T> Success(T value) => new(value, null, true);
    internal static CircuitOutcome<T> Failed(CircuitFailure failure) => new(default, failure, false);
    internal static CircuitOutcome<T> FromCore(Circuit.Core.Response<T> response)
        => response.IsSuccess ? Success(response.Value) : Failed(new CircuitFailure(response.Failure));
}

/// <summary>Carries correlation, usage, session, attempt, and timing metadata for a response.</summary>
public sealed class CircuitResponseMetadata
{
    internal CircuitResponseMetadata(Circuit.Core.ResponseMetadata inner)
    {
        ItemKey = inner.ItemKey.IsSome ? inner.ItemKey.Value.Value : null;
        SourceOrdinal = inner.SourceOrdinal.IsSome ? inner.SourceOrdinal.Value : null;
        SourceOrder = Array.AsReadOnly(inner.SourceOrder.ToArray());
        RunId = inner.RunId.Value;
        NodePath = inner.NodePath;
        Usage = RunUsage.FromCore(inner.Usage);
        Session = inner.Session.IsSome ? CircuitSession.FromCore(inner.Session.Value) : null;
        Attempt = inner.Attempt;
        StartedAt = inner.StartedAt;
        CompletedAt = inner.CompletedAt;
        IdempotencyKey = inner.IdempotencyKey;
    }

    /// <summary>Gets the item key.</summary>
    public string? ItemKey { get; }
    /// <summary>Gets the innermost source ordinal.</summary>
    public long? SourceOrdinal { get; }
    /// <summary>Gets the full hierarchical source order from outermost to innermost source.</summary>
    public IReadOnlyList<long> SourceOrder { get; }
    /// <summary>Gets the run id.</summary>
    public string RunId { get; }
    /// <summary>Gets the node path.</summary>
    public string NodePath { get; }
    /// <summary>Gets the usage.</summary>
    public RunUsage Usage { get; }
    /// <summary>Gets the session.</summary>
    public CircuitSession? Session { get; }
    /// <summary>Gets the attempt.</summary>
    public int Attempt { get; }
    /// <summary>Gets the started at.</summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>Gets the completed at.</summary>
    public DateTimeOffset CompletedAt { get; }
    /// <summary>Gets the idempotency key.</summary>
    public string IdempotencyKey { get; }
}

/// <summary>Represents one completed typed evaluation.</summary>
public sealed class CircuitResponse<T>
{
    internal CircuitResponse(Circuit.Core.Response<T> inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Outcome = CircuitOutcome<T>.FromCore(inner);
        Metadata = new(inner.Metadata);
    }

    internal CircuitResponse(CircuitOutcome<T> outcome, CircuitResponseMetadata metadata)
    {
        Outcome = outcome;
        Metadata = metadata;
    }

    internal Circuit.Core.Response<T>? Inner { get; }
    /// <summary>Gets the outcome.</summary>
    public CircuitOutcome<T> Outcome { get; }
    /// <summary>Gets the metadata.</summary>
    public CircuitResponseMetadata Metadata { get; }
    /// <summary>Gets the is success.</summary>
    public bool IsSuccess => Outcome.IsSuccess;
    /// <summary>Gets the value.</summary>
    public T Value => IsSuccess ? Outcome.Value! : throw new InvalidOperationException("The response does not contain a value.");
    /// <summary>Gets the failure.</summary>
    public CircuitFailure Failure => !IsSuccess ? Outcome.Failure! : throw new InvalidOperationException("The response does not contain a failure.");

    /// <summary>Creates the succeed operation.</summary>
    public static CircuitResponse<T> Succeed(CircuitContext context, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(Circuit.Core.ResponseModule.succeed(context.Inner, value));
    }

    /// <summary>Creates the fail operation.</summary>
    public static CircuitResponse<T> Fail(CircuitContext context, CircuitFailure failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(failure);
        return new(Circuit.Core.ResponseModule.fail<T>(context.Inner, failure.Inner));
    }
}

/// <summary>Provides stable node and item correlation to trusted host code.</summary>
public sealed class CircuitContext
{
    internal CircuitContext(Circuit.Core.CircuitContext inner) => Inner = inner;
    internal Circuit.Core.CircuitContext Inner { get; }
    /// <summary>Gets the run id.</summary>
    public string RunId => Inner.RunId.Value;
    /// <summary>Gets the node path.</summary>
    public string NodePath => Inner.NodePath;
    /// <summary>Gets the item key.</summary>
    public string? ItemKey => Inner.ItemKey.IsSome ? Inner.ItemKey.Value.Value : null;
    /// <summary>Gets the idempotency key.</summary>
    public string IdempotencyKey => Inner.IdempotencyKey;
    /// <summary>Gets the services.</summary>
    public IServiceProvider Services => Inner.Options.Services;
    /// <summary>Gets the cancellation token.</summary>
    public CancellationToken CancellationToken => Inner.CancellationToken;
}

/// <summary>Represents a page from a durable source.</summary>
public sealed class CircuitSourcePage<T>
{
    /// <summary>Initializes a new circuit source page instance.</summary>
    public CircuitSourcePage(IReadOnlyList<T> items, string? continuationToken, bool completed)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        ContinuationToken = continuationToken;
        Completed = completed;
    }

    /// <summary>Gets the items.</summary>
    public IReadOnlyList<T> Items { get; }
    /// <summary>Gets the continuation token.</summary>
    public string? ContinuationToken { get; }
    /// <summary>Gets the completed.</summary>
    public bool Completed { get; }
}

/// <summary>Produces cursor-aware durable source pages.</summary>
public interface IResumableCircuitSource<TInput, TItem>
{
    /// <summary>Reads the next durable source page.</summary>
    ValueTask<CircuitSourcePage<TItem>> ReadAsync(TInput input, string? continuationToken, CancellationToken cancellationToken);
}

internal sealed class CoreResumableSource<TInput, TItem>(IResumableCircuitSource<TInput, TItem> source)
    : Circuit.Core.IResumableCircuitSource<TInput, TItem>
{
    /// <summary>Reads the read async operation.</summary>
    public async ValueTask<Circuit.Core.CircuitSourcePage<TItem>> ReadAsync(
        TInput input,
        FSharpValueOption<string> continuationToken,
        CancellationToken cancellationToken)
    {
        var page = await source.ReadAsync(input, continuationToken.IsSome ? continuationToken.Value : null, cancellationToken).ConfigureAwait(false);
        return new(page.Items, page.ContinuationToken is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(page.ContinuationToken), page.Completed);
    }
}

/// <summary>Describes immutable run identity and definition correlation.</summary>
public sealed class CircuitRunInfo
{
    internal CircuitRunInfo(Circuit.Core.RunInfo inner)
    {
        RunId = inner.RunId.Value;
        LineageId = inner.LineageId;
        DefinitionId = inner.DefinitionId.Value;
        DefinitionVersion = inner.DefinitionVersion.ToString();
        Fingerprint = inner.Fingerprint;
        StartedAt = inner.StartedAt;
    }

    /// <summary>Gets the run id.</summary>
    public string RunId { get; }
    /// <summary>Gets the lineage id.</summary>
    public string LineageId { get; }
    /// <summary>Gets the definition id.</summary>
    public string DefinitionId { get; }
    /// <summary>Gets the definition version.</summary>
    public string DefinitionVersion { get; }
    /// <summary>Gets the fingerprint.</summary>
    public string Fingerprint { get; }
    /// <summary>Gets the started at.</summary>
    public DateTimeOffset StartedAt { get; }
}

/// <summary>Describes one graph node evaluation without exposing its typed payload.</summary>
public sealed class CircuitNodeInfo
{
    internal CircuitNodeInfo(Circuit.Core.NodeInfo inner)
    {
        NodeId = inner.NodeId;
        NodePath = inner.NodePath;
        ItemKey = inner.ItemKey.IsSome ? inner.ItemKey.Value.Value : null;
        Attempt = inner.Attempt;
        Timestamp = inner.Timestamp;
    }

    /// <summary>Gets the node id.</summary>
    public string NodeId { get; }
    /// <summary>Gets the node path.</summary>
    public string NodePath { get; }
    /// <summary>Gets the item key.</summary>
    public string? ItemKey { get; }
    /// <summary>Gets the attempt.</summary>
    public int Attempt { get; }
    /// <summary>Gets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>Carries an observational provider output delta.</summary>
public sealed class CircuitOutputDelta
{
    internal CircuitOutputDelta(Circuit.Core.CircuitOutputDelta inner)
    {
        NodePath = inner.NodePath;
        ItemKey = inner.ItemKey.IsSome ? inner.ItemKey.Value.Value : null;
        Text = inner.Text;
        Timestamp = inner.Timestamp;
    }

    /// <summary>Gets the node path.</summary>
    public string NodePath { get; }
    /// <summary>Gets the item key.</summary>
    public string? ItemKey { get; }
    /// <summary>Gets the text.</summary>
    public string Text { get; }
    /// <summary>Gets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>Describes a completed node response without exposing its typed value.</summary>
public sealed class CircuitUntypedResponse
{
    internal CircuitUntypedResponse(Circuit.Core.UntypedResponse inner)
    {
        IsSuccess = inner.IsSuccess;
        Failure = inner.Failure.IsSome ? new CircuitFailure(inner.Failure.Value) : null;
        Metadata = new CircuitResponseMetadata(inner.Metadata);
    }

    /// <summary>Gets whether the node succeeded.</summary>
    public bool IsSuccess { get; }
    /// <summary>Gets the failure.</summary>
    public CircuitFailure? Failure { get; }
    /// <summary>Gets complete response correlation and timing metadata.</summary>
    public CircuitResponseMetadata Metadata { get; }
}

/// <summary>Identifies one unified Circuit event kind.</summary>
public enum CircuitEventKind
{
    /// <summary>Represents the run started classification.</summary>
    RunStarted,
    /// <summary>Represents the node started classification.</summary>
    NodeStarted,
    /// <summary>Represents the output delta classification.</summary>
    OutputDelta,
    /// <summary>Represents the output produced classification.</summary>
    OutputProduced,
    /// <summary>Represents the approval requested classification.</summary>
    ApprovalRequested,
    /// <summary>Represents the node completed classification.</summary>
    NodeCompleted,
    /// <summary>Represents the run completed classification.</summary>
    RunCompleted,
}

/// <summary>Represents one event from a unified Circuit run.</summary>
public sealed class CircuitEvent<T>
{
    private CircuitEvent(CircuitEventKind kind) => Kind = kind;

    /// <summary>Gets the kind.</summary>
    public CircuitEventKind Kind { get; }
    /// <summary>Gets complete run data for <see cref="CircuitEventKind.RunStarted"/>.</summary>
    public CircuitRunInfo? Run { get; private init; }
    /// <summary>Gets complete node data for node-started and node-completed events.</summary>
    public CircuitNodeInfo? Node { get; private init; }
    /// <summary>Gets complete delta data for <see cref="CircuitEventKind.OutputDelta"/>.</summary>
    public CircuitOutputDelta? Delta { get; private init; }
    /// <summary>Gets the item key.</summary>
    public string? ItemKey { get; private init; }
    /// <summary>Gets the output.</summary>
    public CircuitResponse<T>? Output { get; private init; }
    /// <summary>Gets the approval.</summary>
    public ApprovalRequest? Approval { get; private init; }
    /// <summary>Gets the node response.</summary>
    public CircuitUntypedResponse? NodeResponse { get; private init; }
    /// <summary>Gets the terminal.</summary>
    public CircuitResponse<RunSummary>? Terminal { get; private init; }

    /// <summary>Gets the owning run identifier for every event kind.</summary>
    public string RunId { get; private init; } = string.Empty;
    /// <summary>Gets the node path.</summary>
    public string? NodePath => Node?.NodePath ?? Delta?.NodePath;
    /// <summary>Gets the text delta.</summary>
    public string? TextDelta => Delta?.Text;

    internal static CircuitEvent<T> FromCore(Circuit.Core.CircuitEvent<T> value, string runId)
    {
        var (caseInfo, fields) = FSharpValue.GetUnionFields(value, value.GetType(), FSharpOption<System.Reflection.BindingFlags>.None);
        return caseInfo.Name switch
        {
            "RunStarted" => new(CircuitEventKind.RunStarted) { RunId = runId, Run = new((Circuit.Core.RunInfo)fields[0]) },
            "NodeStarted" => new(CircuitEventKind.NodeStarted) { RunId = runId, Node = new((Circuit.Core.NodeInfo)fields[0]) },
            "OutputDelta" => new(CircuitEventKind.OutputDelta) { RunId = runId, Delta = new((Circuit.Core.CircuitOutputDelta)fields[0]) },
            "OutputProduced" => new(CircuitEventKind.OutputProduced)
            {
                RunId = runId,
                ItemKey = ((FSharpValueOption<Circuit.Core.ItemKey>)fields[0]).IsSome
                    ? ((FSharpValueOption<Circuit.Core.ItemKey>)fields[0]).Value.Value
                    : null,
                Output = new((Circuit.Core.Response<T>)fields[1]),
            },
            "ApprovalRequested" => new(CircuitEventKind.ApprovalRequested)
            {
                RunId = runId,
                Approval = ApprovalRequest.FromCore((Circuit.Core.ApprovalRequest)fields[0]),
            },
            "NodeCompleted" => new(CircuitEventKind.NodeCompleted)
            {
                RunId = runId,
                Node = new((Circuit.Core.NodeInfo)fields[0]),
                NodeResponse = new((Circuit.Core.UntypedResponse)fields[1]),
            },
            "RunCompleted" => new(CircuitEventKind.RunCompleted)
            {
                RunId = runId,
                Terminal = new CircuitResponse<Circuit.Core.RunSummary>((Circuit.Core.Response<Circuit.Core.RunSummary>)fields[0]).MapSummary(),
            },
            _ => throw new InvalidOperationException($"Unknown Circuit event case '{caseInfo.Name}'."),
        };
    }

}

/// <summary>Summarizes one completed Circuit run.</summary>
public sealed class RunSummary
{
    internal RunSummary(Circuit.Core.RunSummary value)
    {
        OutputCount = value.OutputCount;
        SucceededCount = value.SucceededCount;
        FailedCount = value.FailedCount;
        Usage = RunUsage.FromCore(value.Usage);
        StartedAt = value.StartedAt;
        CompletedAt = value.CompletedAt;
    }

    /// <summary>Gets the output count.</summary>
    public int OutputCount { get; }
    /// <summary>Gets the succeeded count.</summary>
    public int SucceededCount { get; }
    /// <summary>Gets the failed count.</summary>
    public int FailedCount { get; }
    /// <summary>Gets the usage.</summary>
    public RunUsage Usage { get; }
    /// <summary>Gets the started at.</summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>Gets the completed at.</summary>
    public DateTimeOffset CompletedAt { get; }
}

internal static class UnifiedResponseMapping
{
    internal static CircuitResponse<RunSummary> MapSummary(this CircuitResponse<Circuit.Core.RunSummary> source)
        => source.IsSuccess
            ? new(CircuitOutcome<RunSummary>.Success(new RunSummary(source.Value)), source.Metadata)
            : new(CircuitOutcome<RunSummary>.Failed(source.Failure), source.Metadata);
}

/// <summary>Wraps an opaque exact-definition checkpoint.</summary>
public sealed class CircuitCheckpoint<T>
{
    internal CircuitCheckpoint(Circuit.Core.CircuitCheckpoint<T> inner) => Inner = inner;
    internal Circuit.Core.CircuitCheckpoint<T> Inner { get; }
    /// <summary>Gets the definition id.</summary>
    public string DefinitionId => Inner.DefinitionId.Value;
    /// <summary>Gets the definition version.</summary>
    public string DefinitionVersion => Inner.DefinitionVersion.ToString();
    /// <summary>Gets the fingerprint.</summary>
    public string Fingerprint => Inner.Fingerprint;
    /// <summary>Gets the lineage id.</summary>
    public string LineageId => Inner.LineageId;
    /// <summary>Gets the created at.</summary>
    public DateTimeOffset CreatedAt => Inner.CreatedAt;
    /// <summary>Serializes the serialize operation.</summary>
    public JsonElement Serialize() => Inner.Serialize();
    /// <summary>Deserializes the deserialize operation.</summary>
    public static CircuitCheckpoint<T> Deserialize(JsonElement state) => new(Circuit.Core.CircuitCheckpoint<T>.Deserialize(state));
}

/// <summary>Owns a live unified Circuit event stream and approval/checkpoint protocol.</summary>
public sealed class CircuitRun<T> : IAsyncDisposable
{
    internal CircuitRun(Circuit.Core.CircuitRun<T> inner) => Inner = inner;
    internal Circuit.Core.CircuitRun<T> Inner { get; }
    /// <summary>Gets the run id.</summary>
    public string RunId => Inner.RunId.Value;
    /// <summary>Gets the events.</summary>
    public IAsyncEnumerable<CircuitEvent<T>> Events => Convert(Inner.Events, RunId);

    /// <summary>Responds to the respond async operation.</summary>
    public async Task<CircuitResponse<bool>> RespondAsync(ApprovalResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new CircuitResponse<Microsoft.FSharp.Core.Unit>(
            await Inner.RespondAsync(response.ToCore(), cancellationToken).AsTask().ConfigureAwait(false));
        return result.IsSuccess
            ? new(CircuitOutcome<bool>.Success(true), result.Metadata)
            : new(CircuitOutcome<bool>.Failed(result.Failure), result.Metadata);
    }

    /// <summary>Creates the create checkpoint async operation.</summary>
    public async Task<CircuitResponse<CircuitCheckpoint<T>>> CreateCheckpointAsync(CancellationToken cancellationToken = default)
    {
        var result = new CircuitResponse<Circuit.Core.CircuitCheckpoint<T>>(
            await Inner.CreateCheckpointAsync(cancellationToken).AsTask().ConfigureAwait(false));
        return result.IsSuccess
            ? new(CircuitOutcome<CircuitCheckpoint<T>>.Success(new CircuitCheckpoint<T>(result.Value)), result.Metadata)
            : new(CircuitOutcome<CircuitCheckpoint<T>>.Failed(result.Failure), result.Metadata);
    }

    /// <summary>Disposes the dispose async operation.</summary>
    public ValueTask DisposeAsync() => ((IAsyncDisposable)Inner).DisposeAsync();

    private static async IAsyncEnumerable<CircuitEvent<T>> Convert(
        IAsyncEnumerable<Circuit.Core.CircuitEvent<T>> source,
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return CircuitEvent<T>.FromCore(item, runId);
        }
    }
}
