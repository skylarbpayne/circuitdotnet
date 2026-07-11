#pragma warning disable CS1591

using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.FSharp.Core;

namespace Circuit;

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

    public AgentFailureCode Code { get; }

    public string Message { get; }

    public string? RunId { get; }

    public string? OperationId { get; }

    public string? RequestId { get; }

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

    public bool IsSuccess { get; }

    public T? Value { get; }

    public AgentFailure? Failure { get; }

    public static OperationResult<T> Success(T value) => new(value, null, true);

    public static OperationResult<T> Error(AgentFailure failure) => new(default, failure, false);

    internal static OperationResult<T> FromCore(Circuit.Core.CircuitResult<T> result)
        => result.IsSuccess ? Success(result.Value) : Error(AgentFailure.FromCore(result.Failure));
}

public sealed class RunUsage
{
    private RunUsage(int inputTokens, int outputTokens)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = inputTokens + outputTokens;
    }

    public int InputTokens { get; }

    public int OutputTokens { get; }

    public int TotalTokens { get; }

    internal static RunUsage FromCore(Circuit.Core.RunUsage usage) => new(usage.InputTokens, usage.OutputTokens);
}

public sealed class CircuitSession
{
    private CircuitSession(Circuit.Core.CircuitSession inner)
    {
        Inner = inner;
        Id = inner.Id;
        Metadata = Copy(inner.Metadata);
    }

    internal Circuit.Core.CircuitSession Inner { get; }

    public string Id { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal static CircuitSession FromCore(Circuit.Core.CircuitSession session) => new(session);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

public sealed class AgentRunOptions
{
    public CircuitSession? Session { get; set; }

    public string? TenantId { get; set; }

    public string? UserId { get; set; }

    public IReadOnlyDictionary<string, string> Tags { get; set; } = EmptyStringDictionary;

    public StructuredOutputPolicy StructuredOutputPolicy { get; set; } = StructuredOutputPolicy.NativeOnly;

    public SensitiveDataMode SensitiveDataMode { get; set; } = SensitiveDataMode.Standard;

    public IServiceProvider? Services { get; set; }

    internal Circuit.Core.RunOptions ToCore()
    {
        var tags = Tags ?? EmptyStringDictionary;

        return new Circuit.Core.RunOptions(
            Session is null ? FSharpValueOption<Circuit.Core.CircuitSession>.None : FSharpValueOption<Circuit.Core.CircuitSession>.Some(Session.Inner),
            string.IsNullOrWhiteSpace(TenantId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(TenantId),
            string.IsNullOrWhiteSpace(UserId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(UserId),
            Copy(tags),
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

public sealed class WorkflowRunOptions
{
    public string? SessionId { get; set; }

    internal Circuit.Core.WorkflowRunOptions ToCore()
        => new(string.IsNullOrWhiteSpace(SessionId) ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(SessionId));

    internal static WorkflowRunOptions FromCore(Circuit.Core.WorkflowRunOptions options)
        => new() { SessionId = options.SessionId.IsSome ? options.SessionId.Value : null };
}

public sealed class ApprovalRequest
{
    private ApprovalRequest(string requestId, string toolName, string? argumentsJson)
    {
        RequestId = requestId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
    }

    public string RequestId { get; }

    public string ToolName { get; }

    public string? ArgumentsJson { get; }

    internal static ApprovalRequest FromCore(Circuit.Core.ApprovalRequest request)
        => new(request.RequestId, request.ToolName, request.ArgumentsJson.IsSome ? request.ArgumentsJson.Value : null);
}

public sealed class ApprovalPrompt
{
    public ApprovalPrompt(string title, string message)
        : this(title, message, null)
    {
    }

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

    public string Title { get; }

    public string Message { get; }

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

public sealed class ApprovalResponse
{
    public ApprovalResponse(string requestId, bool approved)
        : this(requestId, approved, null)
    {
    }

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

    public string RequestId { get; }

    public bool Approved { get; }

    public string? Note { get; }

    internal Circuit.Core.ApprovalResponse ToCore() => new(RequestId, Approved, Note);

    internal static ApprovalResponse FromCore(Circuit.Core.ApprovalResponse response)
        => new(response.RequestId, response.Approved, response.Note);
}

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

    public long Sequence { get; }

    public string RunId { get; }

    public DateTimeOffset Timestamp { get; }

    public AgentRunEventKind Kind { get; }

    public string? OperationId { get; }

    public string? TextDelta { get; }

    public T? Value { get; }

    public AgentFailure? Failure { get; }

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

    public string RunId { get; }

    public OperationResult<T> Result { get; }

    public RunUsage Usage { get; }

    public CircuitSession? Session { get; }

    public DateTimeOffset StartedAt { get; }

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

    public string DefinitionId { get; }

    public string DefinitionVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public JsonElement Serialize() => Inner.Serialize();

    internal static WorkflowCheckpoint<T> FromCore(Circuit.Core.WorkflowCheckpoint<T> checkpoint) => new(checkpoint);

    internal static WorkflowCheckpoint<T> Deserialize(JsonElement state)
        => new(Circuit.Core.WorkflowCheckpoint<T>.Deserialize(state));
}

public sealed class WorkflowContext
{
    internal WorkflowContext(Circuit.Core.WorkflowContext inner)
    {
        Inner = inner;
    }

    internal Circuit.Core.WorkflowContext Inner { get; }

    public string RunId => Inner.RunId.Value;

    public string DefinitionId => Inner.DefinitionId.Value;

    public string DefinitionVersion => Inner.DefinitionVersion.ToString();

    public string StepId => Inner.StepId;

    public CancellationToken CancellationToken => Inner.CancellationToken;
}

public sealed class WorkflowRun<T> : IAsyncDisposable
{
    private readonly Circuit.Core.WorkflowRun<T> _inner;

    internal WorkflowRun(Circuit.Core.WorkflowRun<T> inner)
    {
        _inner = inner;
        RunId = inner.RunId.Value;
        Events = Stream(inner.Events);
    }

    public string RunId { get; }

    public IAsyncEnumerable<AgentRunEvent<T>> Events { get; }

    public ValueTask RespondAsync(ApprovalResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return _inner.RespondAsync(response.ToCore(), cancellationToken);
    }

    public async Task<WorkflowCheckpoint<T>> CreateCheckpointAsync(CancellationToken cancellationToken = default)
        => WorkflowCheckpoint<T>.FromCore(await _inner.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false));

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
