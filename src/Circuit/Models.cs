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
    /// Gets whether the operation succeeded.
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

    /// <summary>Gets or sets ambient services.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Gets the max concurrency value.</summary>
    public int MaxConcurrency { get; set; } = 8;
    /// <summary>Gets the event buffer capacity value.</summary>
    public int EventBufferCapacity { get; set; } = 128;
    /// <summary>Gets the max dynamic depth value.</summary>
    public int MaxDynamicDepth { get; set; } = 16;
    /// <summary>Gets the max dynamic nodes value.</summary>
    public int MaxDynamicNodes { get; set; } = 1024;
    /// <summary>Gets the max approval rounds value.</summary>
    public int MaxApprovalRounds { get; set; } = 16;
    /// <summary>Gets the maximum number of items returned by one resumable-source page.</summary>
    public int MaxSourcePageSize { get; set; } = 256;
    /// <summary>Gets the maximum number of resumable-source pages read across one checkpoint lineage.</summary>
    public int MaxSourcePages { get; set; } = 1024;
    /// <summary>Gets the maximum serialized checkpoint size in bytes.</summary>
    public int MaxCheckpointBytes { get; set; } = 4 * 1024 * 1024;
    /// <summary>Gets the disposal drain timeout value.</summary>
    public TimeSpan DisposalDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

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
                Services ?? EmptyServiceProvider.Instance)
            .WithMaxConcurrency(MaxConcurrency)
            .WithEventBufferCapacity(EventBufferCapacity)
            .WithLimits(MaxDynamicDepth, MaxDynamicNodes, MaxApprovalRounds, MaxSourcePageSize, MaxSourcePages)
            .WithMaxCheckpointBytes(MaxCheckpointBytes)
            .WithDisposalDrainTimeout(DisposalDrainTimeout);
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
            MaxConcurrency = options.MaxConcurrency,
            EventBufferCapacity = options.EventBufferCapacity,
            MaxDynamicDepth = options.MaxDynamicDepth,
            MaxDynamicNodes = options.MaxDynamicNodes,
            MaxApprovalRounds = options.MaxApprovalRounds,
            MaxSourcePageSize = options.MaxSourcePageSize,
            MaxSourcePages = options.MaxSourcePages,
            MaxCheckpointBytes = options.MaxCheckpointBytes,
            DisposalDrainTimeout = options.DisposalDrainTimeout,
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
        /// <summary>Gets the instance value.</summary>
        public static EmptyServiceProvider Instance { get; } = new();

        /// <summary>
        /// Executes get service.
        /// </summary>
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Supplies process-local dependencies when resuming a serialized checkpoint.</summary>
public sealed class ResumeOptions
{
    /// <summary>Gets or sets the service provider rebound in the receiving process.</summary>
    public IServiceProvider? Services { get; set; }

    internal Circuit.Core.ResumeOptions ToCore()
        => new(Services ?? EmptyResumeServiceProvider.Instance);

    private sealed class EmptyResumeServiceProvider : IServiceProvider
    {
        internal static EmptyResumeServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// Represents the approval request.
/// </summary>
public sealed class ApprovalRequest
{
    private ApprovalRequest(string requestId, string toolName, string? argumentsJson, ApprovalPrompt? prompt)
    {
        RequestId = requestId;
        ToolName = toolName;
        ArgumentsJson = argumentsJson;
        Prompt = prompt;
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

    /// <summary>Gets the complete graph approval prompt when this request came from an approval node.</summary>
    public ApprovalPrompt? Prompt { get; }

    internal static ApprovalRequest FromCore(Circuit.Core.ApprovalRequest request)
        => new(
            request.RequestId,
            request.ToolName,
            request.ArgumentsJson.IsSome ? request.ArgumentsJson.Value : null,
            request.Prompt.IsSome ? ApprovalPrompt.FromCore(request.Prompt.Value) : null);
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
