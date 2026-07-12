using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.FSharp.Core;

namespace Circuit;

/// <summary>
/// Represents the agent failure.
/// </summary>
public sealed class AgentFailure
{
    private AgentFailure(
        AgentFailureCode code,
        string message,
        string? runId,
        string? operationId,
        string? requestId,
        Exception? exception)
    {
        Code = code;
        Message = message;
        RunId = runId;
        OperationId = operationId;
        RequestId = requestId;
        Exception = exception;
    }

    /// <summary>
    /// Gets the code.
    /// </summary>
    public AgentFailureCode Code { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string? RunId { get; }

    /// <summary>
    /// Gets the operation id.
    /// </summary>
    public string? OperationId { get; }

    /// <summary>
    /// Gets the request id.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>
    /// Gets the exception.
    /// </summary>
    public Exception? Exception { get; }

    internal static AgentFailure FromCore(Circuit.Core.CircuitFailure failure)
        => new(
            (AgentFailureCode)failure.Code,
            failure.Message,
            failure.RunId.IsSome ? failure.RunId.Value.Value : null,
            failure.OperationId.IsSome ? failure.OperationId.Value : null,
            failure.RequestId.IsSome ? failure.RequestId.Value : null,
            failure.Exception.IsSome ? failure.Exception.Value : null
        );
}

/// <summary>
/// Represents the operation result.
/// </summary>
public sealed class OperationResult<T>
{
    private OperationResult(T? value, AgentFailure? failure, bool isSuccess)
    {
        if (isSuccess)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (failure is not null)
            {
                throw new ArgumentException("Successful results cannot carry a failure.", nameof(failure));
            }
        }
        else if (failure is null)
        {
            throw new ArgumentNullException(nameof(failure));
        }

        IsSuccess = isSuccess;
        Value = isSuccess ? value : default;
        Failure = isSuccess ? null : failure;
    }

    /// <summary>
    /// Gets the is success.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the failure.
    /// </summary>
    public AgentFailure? Failure { get; }

    /// <summary>
    /// Executes success.
    /// </summary>
    public static OperationResult<T> Success(T value) => new(value, null, true);

    /// <summary>
    /// Executes error.
    /// </summary>
    public static OperationResult<T> Error(AgentFailure failure) => new(default, failure, false);

    internal static OperationResult<T> FromCore(Circuit.Core.CircuitResult<T> result)
        => result.IsSuccess ? Success(result.Value) : Error(AgentFailure.FromCore(result.Failure));
}

/// <summary>
/// Represents the run usage.
/// </summary>
public sealed class RunUsage
{
    private RunUsage(int inputTokens, int outputTokens)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = inputTokens + outputTokens;
    }

    /// <summary>
    /// Gets the input tokens.
    /// </summary>
    public int InputTokens { get; }

    /// <summary>
    /// Gets the output tokens.
    /// </summary>
    public int OutputTokens { get; }

    /// <summary>
    /// Gets the total tokens.
    /// </summary>
    public int TotalTokens { get; }

    internal static RunUsage FromCore(Circuit.Core.RunUsage usage) => new(usage.InputTokens, usage.OutputTokens);
}

/// <summary>
/// Represents the circuit session.
/// </summary>
public sealed class CircuitSession
{
    private CircuitSession(Circuit.Core.CircuitSession inner)
    {
        Inner = inner;
        Id = inner.Id;
        Metadata = Copy(inner.Metadata);
    }

    internal Circuit.Core.CircuitSession Inner { get; }

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal static CircuitSession FromCore(Circuit.Core.CircuitSession session) => new(session);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>
/// Represents the agent run options.
/// </summary>
public sealed class AgentRunOptions
{
    /// <summary>
    /// Gets or sets the session.
    /// </summary>
    public CircuitSession? Session { get; set; }

    /// <summary>
    /// Gets or sets the tenant id.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Gets or sets the tags.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; set; } = EmptyStringDictionary;

    /// <summary>
    /// Gets or sets the structured output policy.
    /// </summary>
    public StructuredOutputPolicy StructuredOutputPolicy { get; set; } = StructuredOutputPolicy.NativeOnly;

    /// <summary>
    /// Gets or sets the sensitive data mode.
    /// </summary>
    public SensitiveDataMode SensitiveDataMode { get; set; } = SensitiveDataMode.Standard;

    /// <summary>
    /// Gets or sets the services.
    /// </summary>
    public IServiceProvider? Services { get; set; }

    internal Circuit.Core.RunOptions ToCore()
    {
        var tags = Tags ?? EmptyStringDictionary;

        return new Circuit.Core.RunOptions(
            Session is null ? FSharpValueOption<Circuit.Core.CircuitSession>.None : FSharpValueOption<Circuit.Core.CircuitSession>.Some(Session.Inner),
            string.IsNullOrWhiteSpace(TenantId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(TenantId),
            string.IsNullOrWhiteSpace(UserId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(UserId),
            ValidateAndCopyTags(tags),
            (Circuit.Core.StructuredOutputPolicy)StructuredOutputPolicy,
            (Circuit.Core.SensitiveDataMode)SensitiveDataMode,
            Services ?? EmptyServiceProvider.Instance);
    }

    internal static AgentRunOptions FromCore(Circuit.Core.RunOptions options)
        => new()
        {
            Session = options.Session.IsSome ? CircuitSession.FromCore(options.Session.Value) : null,
            TenantId = options.TenantId.IsSome ? options.TenantId.Value : null,
            UserId = options.UserId.IsSome ? options.UserId.Value : null,
            Tags = Copy(options.Tags),
            StructuredOutputPolicy = (StructuredOutputPolicy)options.StructuredOutputPolicy,
            SensitiveDataMode = (SensitiveDataMode)options.SensitiveDataMode,
            Services = options.Services,
        };

    private static readonly IReadOnlyDictionary<string, string> EmptyStringDictionary =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, string> ValidateAndCopyTags(IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var entries = source.ToArray();

        if (entries.Length > 32)
        {
            throw new ArgumentException("No more than 32 tags are allowed.", "tags");
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("Tag keys cannot be blank.", "tags");
            }

            if (entry.Key.Length > 64)
            {
                throw new ArgumentException("Tag keys must be 64 characters or fewer.", "tags");
            }

            if (entry.Key.StartsWith("circuit.", StringComparison.Ordinal))
            {
                throw new ArgumentException("Tag keys beginning with 'circuit.' are reserved.", "tags");
            }

            if (entry.Value is null)
            {
                throw new ArgumentNullException("tags");
            }

            if (entry.Value.Length > 256)
            {
                throw new ArgumentException("Tag values must be 256 characters or fewer.", "tags");
            }

            if (!copy.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException("Duplicate tag keys are not allowed.", "tags");
            }
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        /// <summary>
        /// Executes get service.
        /// </summary>
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// Represents the workflow run options.
/// </summary>
public sealed class WorkflowRunOptions
{
    /// <summary>
    /// Gets or sets the session id.
    /// </summary>
    public string? SessionId { get; set; }

    internal Circuit.Core.WorkflowRunOptions ToCore()
        => new(string.IsNullOrWhiteSpace(SessionId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(SessionId));

    internal static WorkflowRunOptions FromCore(Circuit.Core.WorkflowRunOptions options)
        => new() { SessionId = options.SessionId.IsSome ? options.SessionId.Value : null };
}

/// <summary>
/// Represents the approval request.
/// </summary>
public sealed class ApprovalRequest
{
    private ApprovalRequest(string requestId, string toolName, string? argumentsJson)
    {
        RequestId = requestId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
    }

    /// <summary>
    /// Gets the request id.
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string ToolName { get; }

    /// <summary>
    /// Gets the arguments json.
    /// </summary>
    public string? ArgumentsJson { get; }

    internal static ApprovalRequest FromCore(Circuit.Core.ApprovalRequest request)
        => new(request.RequestId, request.ToolName, request.ArgumentsJson.IsSome ? request.ArgumentsJson.Value : null);
}

/// <summary>
/// Represents the approval prompt.
/// </summary>
public sealed class ApprovalPrompt
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalPrompt"/> class.
    /// </summary>
    public ApprovalPrompt(string title, string message)
        : this(title, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalPrompt"/> class.
    /// </summary>
    public ApprovalPrompt(string title, string message, IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        Title = title;
        Message = message;
        Metadata = metadata is null
            ? EmptyStringDictionary
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal Circuit.Core.ApprovalPrompt ToCore()
        => new(Title, Message, Metadata.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

    internal static ApprovalPrompt FromCore(Circuit.Core.ApprovalPrompt prompt)
        => new(prompt.Title, prompt.Message, Copy(prompt.Metadata));

    private static readonly IReadOnlyDictionary<string, string> EmptyStringDictionary =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>
/// Represents the approval response.
/// </summary>
public sealed class ApprovalResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalResponse"/> class.
    /// </summary>
    public ApprovalResponse(string requestId, bool approved)
        : this(requestId, approved, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalResponse"/> class.
    /// </summary>
    public ApprovalResponse(string requestId, bool approved, string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (note is not null && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("note cannot be blank when provided.", nameof(note));
        }

        RequestId = requestId;
        Approved = approved;
        Note = note;
    }

    /// <summary>
    /// Gets the request id.
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// Gets the approved.
    /// </summary>
    public bool Approved { get; }

    /// <summary>
    /// Gets the note.
    /// </summary>
    public string? Note { get; }

    internal Circuit.Core.ApprovalResponse ToCore() => new(RequestId, Approved, Note);

    internal static ApprovalResponse FromCore(Circuit.Core.ApprovalResponse response)
        => new(response.RequestId, response.Approved, response.Note);
}

/// <summary>
/// Represents the agent run event.
/// </summary>
public sealed class AgentRunEvent<T>
{
    private AgentRunEvent(
        long sequence,
        string runId,
        DateTimeOffset timestamp,
        AgentRunEventKind kind,
        string? operationId,
        string? textDelta,
        T? value,
        AgentFailure? failure,
        ApprovalRequest? approval)
    {
        Sequence = sequence;
        RunId = runId;
        Timestamp = timestamp;
        Kind = kind;
        OperationId = operationId;
        TextDelta = textDelta;
        Value = value;
        Failure = failure;
        Approval = approval;
    }

    /// <summary>
    /// Gets the sequence.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Gets the timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Gets the kind.
    /// </summary>
    public AgentRunEventKind Kind { get; }

    /// <summary>
    /// Gets the operation id.
    /// </summary>
    public string? OperationId { get; }

    /// <summary>
    /// Gets the text delta.
    /// </summary>
    public string? TextDelta { get; }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the failure.
    /// </summary>
    public AgentFailure? Failure { get; }

    /// <summary>
    /// Gets the approval.
    /// </summary>
    public ApprovalRequest? Approval { get; }

    internal static AgentRunEvent<T> FromCore(Circuit.Core.RunEvent<T> @event)
        => new(
            @event.Sequence,
            @event.RunId.Value,
            @event.Timestamp,
            (AgentRunEventKind)@event.Kind,
            @event.OperationId.IsSome ? @event.OperationId.Value : null,
            @event.TextDelta.IsSome ? @event.TextDelta.Value : null,
            @event.Value.IsSome ? @event.Value.Value : default,
            @event.Failure.IsSome ? AgentFailure.FromCore(@event.Failure.Value) : null,
            @event.Approval.IsSome ? ApprovalRequest.FromCore(@event.Approval.Value) : null
        );
}

/// <summary>
/// Represents the agent run result.
/// </summary>
public sealed class AgentRunResult<T>
{
    private AgentRunResult(
        string runId,
        OperationResult<T> result,
        RunUsage usage,
        CircuitSession? session,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        RunId = runId;
        Result = result;
        Usage = usage;
        Session = session;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Gets the result.
    /// </summary>
    public OperationResult<T> Result { get; }

    /// <summary>
    /// Gets the usage.
    /// </summary>
    public RunUsage Usage { get; }

    /// <summary>
    /// Gets the session.
    /// </summary>
    public CircuitSession? Session { get; }

    /// <summary>
    /// Gets the started at.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Gets the completed at.
    /// </summary>
    public DateTimeOffset CompletedAt { get; }

    internal static AgentRunResult<T> FromCore(Circuit.Core.RunResult<T> result)
        => new(
            result.RunId.Value,
            OperationResult<T>.FromCore(result.Result),
            RunUsage.FromCore(result.Usage),
            result.Session.IsSome ? CircuitSession.FromCore(result.Session.Value) : null,
            result.StartedAt,
            result.CompletedAt
        );
}

/// <summary>
/// Represents the workflow checkpoint.
/// </summary>
public sealed class WorkflowCheckpoint<T>
{
    private WorkflowCheckpoint(Circuit.Core.WorkflowCheckpoint<T> inner)
    {
        Inner = inner;
        DefinitionId = inner.DefinitionId.Value;
        DefinitionVersion = inner.DefinitionVersion.ToString();
        CreatedAt = inner.CreatedAt;
    }

    internal Circuit.Core.WorkflowCheckpoint<T> Inner { get; }

    /// <summary>
    /// Gets the definition id.
    /// </summary>
    public string DefinitionId { get; }

    /// <summary>
    /// Gets the definition version.
    /// </summary>
    public string DefinitionVersion { get; }

    /// <summary>
    /// Gets the checkpoint creation time in UTC.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Serializes the checkpoint to an opaque JSON envelope.
    /// </summary>
    public JsonElement Serialize() => Inner.Serialize();

    internal static WorkflowCheckpoint<T> FromCore(Circuit.Core.WorkflowCheckpoint<T> checkpoint) => new(checkpoint);

    /// <summary>
    /// Restores a checkpoint from a serialized JSON envelope.
    /// </summary>
    /// <param name="state">The checkpoint envelope returned by <see cref="Serialize"/>.</param>
    /// <returns>The restored checkpoint.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not a valid checkpoint envelope.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> uses an unsupported checkpoint format version.</exception>
    public static WorkflowCheckpoint<T> Deserialize(JsonElement state)
        => new(Circuit.Core.WorkflowCheckpoint<T>.Deserialize(state));
}

/// <summary>
/// Represents the workflow context.
/// </summary>
public sealed class WorkflowContext
{
    internal WorkflowContext(Circuit.Core.WorkflowContext inner)
    {
        Inner = inner;
    }

    internal Circuit.Core.WorkflowContext Inner { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId => Inner.RunId.Value;

    /// <summary>
    /// Gets the definition id.
    /// </summary>
    public string DefinitionId => Inner.DefinitionId.Value;

    /// <summary>
    /// Gets the definition version.
    /// </summary>
    public string DefinitionVersion => Inner.DefinitionVersion.ToString();

    /// <summary>
    /// Gets the step id.
    /// </summary>
    public string StepId => Inner.StepId;

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken => Inner.CancellationToken;
}

/// <summary>
/// Represents the workflow run.
/// </summary>
public sealed class WorkflowRun<T> : IAsyncDisposable
{
    private readonly Circuit.Core.WorkflowRun<T> _inner;

    internal WorkflowRun(Circuit.Core.WorkflowRun<T> inner)
    {
        _inner = inner;
        RunId = inner.RunId.Value;
        Events = Stream(inner.Events);
    }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Gets the events.
    /// </summary>
    public IAsyncEnumerable<AgentRunEvent<T>> Events { get; }

    /// <summary>
    /// Executes respond async.
    /// </summary>
    public ValueTask RespondAsync(ApprovalResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return _inner.RespondAsync(response.ToCore(), cancellationToken);
    }

    /// <summary>
    /// Creates checkpoint async.
    /// </summary>
    public async Task<WorkflowCheckpoint<T>> CreateCheckpointAsync(CancellationToken cancellationToken = default)
        => WorkflowCheckpoint<T>.FromCore(await _inner.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Executes dispose async.
    /// </summary>
    public ValueTask DisposeAsync() => ((IAsyncDisposable)_inner).DisposeAsync();

    private static async IAsyncEnumerable<AgentRunEvent<T>> Stream(
        IAsyncEnumerable<Circuit.Core.RunEvent<T>> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return AgentRunEvent<T>.FromCore(item);
        }
    }
}
