using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Circuit.Core;
using Microsoft.Extensions.AI;

namespace Circuit;

/// <summary>
/// Defines the contract validator.
/// </summary>
public interface IContractValidator<T>
{
    /// <summary>
    /// Validates the supplied value and returns zero or more validation issues.
    /// </summary>
    IReadOnlyList<ValidationIssue> Validate(T value);
}

/// <summary>
/// Defines the tool resolver.
/// </summary>
public interface IToolResolver
{
    /// <summary>
    /// Resolves the tools available for the current run context.
    /// </summary>
    ValueTask<IReadOnlyList<ResolvedTool>> ResolveAsync(ToolResolutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Defines the skill resolver.
/// </summary>
public interface ISkillResolver
{
    /// <summary>
    /// Resolves the skills available for the current run context.
    /// </summary>
    ValueTask<IReadOnlyList<ResolvedSkill>> ResolveAsync(SkillResolutionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Specifies the agent failure code.
/// </summary>
public enum AgentFailureCode
{
    /// <summary>
    /// The input or output contract validation failed.
    /// </summary>
    Validation = (int)CircuitFailureCode.Validation,

    /// <summary>
    /// The selected runtime could not satisfy the requested structured-output behavior.
    /// </summary>
    StructuredOutputUnsupported = (int)CircuitFailureCode.StructuredOutputUnsupported,

    /// <summary>
    /// Circuit could not decode the provider or tool payload into the declared output type.
    /// </summary>
    Decode = (int)CircuitFailureCode.Decode,

    /// <summary>
    /// The provider request failed.
    /// </summary>
    Provider = (int)CircuitFailureCode.Provider,

    /// <summary>
    /// Tool resolution or execution failed.
    /// </summary>
    Tool = (int)CircuitFailureCode.Tool,

    /// <summary>
    /// Approval was required or approval handling failed.
    /// </summary>
    ApprovalRequired = (int)CircuitFailureCode.ApprovalRequired,

    /// <summary>
    /// Skill resolution or script execution failed.
    /// </summary>
    Skill = (int)CircuitFailureCode.Skill,

    /// <summary>
    /// Workflow execution failed.
    /// </summary>
    Workflow = (int)CircuitFailureCode.Workflow,

    /// <summary>
    /// The supplied checkpoint does not match the current workflow definition.
    /// </summary>
    CheckpointMismatch = (int)CircuitFailureCode.CheckpointMismatch,

    /// <summary>
    /// The operation was cancelled.
    /// </summary>
    Cancelled = (int)CircuitFailureCode.Cancelled,
}

/// <summary>
/// Specifies the structured output policy.
/// </summary>
public enum StructuredOutputPolicy
{
    /// <summary>
    /// Require the provider response to satisfy the declared contract without a repair pass.
    /// </summary>
    NativeOnly = (int)Circuit.Core.StructuredOutputPolicy.NativeOnly,

    /// <summary>
    /// Allow one repair pass through the configured secondary structured-output client.
    /// </summary>
    AllowSecondaryModelRepair = (int)Circuit.Core.StructuredOutputPolicy.AllowSecondaryModelRepair,
}

/// <summary>
/// Specifies the sensitive data mode.
/// </summary>
public enum SensitiveDataMode
{
    /// <summary>
    /// Preserve standard diagnostic detail.
    /// </summary>
    Standard = (int)Circuit.Core.SensitiveDataMode.Standard,

    /// <summary>
    /// Prefer sanitized public failures and observer payload handling.
    /// </summary>
    Redact = (int)Circuit.Core.SensitiveDataMode.Redact,
}

/// <summary>
/// Specifies the agent run event kind.
/// </summary>
public enum AgentRunEventKind
{
    /// <summary>
    /// The run started.
    /// </summary>
    RunStarted = (int)RunEventKind.RunStarted,

    /// <summary>
    /// A streamed text delta was produced.
    /// </summary>
    OutputDelta = (int)RunEventKind.OutputDelta,

    /// <summary>
    /// A tool started.
    /// </summary>
    ToolStarted = (int)RunEventKind.ToolStarted,

    /// <summary>
    /// A tool completed.
    /// </summary>
    ToolCompleted = (int)RunEventKind.ToolCompleted,

    /// <summary>
    /// Approval was requested.
    /// </summary>
    ApprovalRequested = (int)RunEventKind.ApprovalRequested,

    /// <summary>
    /// A workflow step started.
    /// </summary>
    StepStarted = (int)RunEventKind.StepStarted,

    /// <summary>
    /// A workflow step completed.
    /// </summary>
    StepCompleted = (int)RunEventKind.StepCompleted,

    /// <summary>
    /// A non-terminal workflow step produced a typed intermediate value.
    /// </summary>
    IntermediateOutput = (int)RunEventKind.IntermediateOutput,

    /// <summary>
    /// The run completed successfully.
    /// </summary>
    RunCompleted = (int)RunEventKind.RunCompleted,

    /// <summary>
    /// The run failed.
    /// </summary>
    RunFailed = (int)RunEventKind.RunFailed,
}

/// <summary>
/// Specifies the tool approval mode.
/// </summary>
public enum ToolApprovalMode
{
    /// <summary>
    /// Never require approval.
    /// </summary>
    Never = (int)ApprovalMode.Never,

    /// <summary>
    /// Always require approval.
    /// </summary>
    Always = (int)ApprovalMode.Always,

    /// <summary>
    /// Require approval when the named runtime policy says so.
    /// </summary>
    ByPolicy = (int)ApprovalMode.ByPolicy,
}

/// <summary>
/// Represents the tool context.
/// </summary>
public sealed class ToolContext
{
    internal ToolContext(Circuit.Core.ToolContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal Circuit.Core.ToolContext Inner { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId => Inner.RunId.Value;

    /// <summary>
    /// Gets the tenant id.
    /// </summary>
    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    /// <summary>
    /// Gets the user id.
    /// </summary>
    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    /// <summary>
    /// Gets the services.
    /// </summary>
    public IServiceProvider Services => Inner.Services;

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public CancellationToken CancellationToken => Inner.CancellationToken;
}

/// <summary>
/// Represents the tool resolution context.
/// </summary>
public sealed class ToolResolutionContext
{
    internal ToolResolutionContext(Circuit.Core.ToolResolutionContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal static ToolResolutionContext FromCore(Circuit.Core.ToolResolutionContext inner) => new(inner);

    internal Circuit.Core.ToolResolutionContext Inner { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId => Inner.RunId.Value;

    /// <summary>
    /// Gets the tenant id.
    /// </summary>
    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    /// <summary>
    /// Gets the user id.
    /// </summary>
    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    /// <summary>
    /// Gets the services.
    /// </summary>
    public IServiceProvider Services => Inner.Services;
}

/// <summary>
/// Represents the skill resolution context.
/// </summary>
public sealed class SkillResolutionContext
{
    internal SkillResolutionContext(Circuit.Core.SkillResolutionContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal static SkillResolutionContext FromCore(Circuit.Core.SkillResolutionContext inner) => new(inner);

    internal Circuit.Core.SkillResolutionContext Inner { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId => Inner.RunId.Value;

    /// <summary>
    /// Gets the tenant id.
    /// </summary>
    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    /// <summary>
    /// Gets the user id.
    /// </summary>
    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    /// <summary>
    /// Gets the services.
    /// </summary>
    public IServiceProvider Services => Inner.Services;
}

/// <summary>
/// Represents the resolved tool.
/// </summary>
public sealed class ResolvedTool
{
    private readonly Circuit.Core.ResolvedTool _inner;

    internal ResolvedTool(Circuit.Core.ResolvedTool inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Id = inner.Name.Value;
        Version = inner.Version.ToString();
        Description = inner.Description;
        ApprovalMode = (ToolApprovalMode)inner.Approval;
        ApprovalPolicy = inner.ApprovalPolicy.IsSome ? inner.ApprovalPolicy.Value : null;
        Tags = new HashSet<string>(inner.Tags, StringComparer.Ordinal);
        InputJsonSchema = inner.InputSchema.ToJsonString();
        OutputJsonSchema = inner.OutputSchema.ToJsonString();
    }

    internal Circuit.Core.ResolvedTool Inner => _inner;

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the approval mode.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; }

    /// <summary>
    /// Gets the approval policy.
    /// </summary>
    public string? ApprovalPolicy { get; }

    /// <summary>
    /// Gets the tags.
    /// </summary>
    public IReadOnlySet<string> Tags { get; }

    /// <summary>
    /// Gets the input json schema.
    /// </summary>
    public string InputJsonSchema { get; }

    /// <summary>
    /// Gets the output json schema.
    /// </summary>
    public string OutputJsonSchema { get; }

    internal static ResolvedTool FromCore(Circuit.Core.ResolvedTool inner) => new(inner);
}

/// <summary>
/// Represents the resolved skill.
/// </summary>
public sealed class ResolvedSkill
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    private readonly Circuit.Core.ResolvedSkill _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolvedSkill"/> class.
    /// </summary>
    public ResolvedSkill(SkillReference reference)
        : this(reference, EmptyProperties)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolvedSkill"/> class.
    /// </summary>
    public ResolvedSkill(SkillReference reference, IReadOnlyDictionary<string, object?> properties)
        : this(CreateCore(reference, properties))
    {
    }

    internal ResolvedSkill(Circuit.Core.ResolvedSkill inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Reference = SkillReference.FromCore(inner.Reference);
        Properties = Copy(inner.Properties);
    }

    internal Circuit.Core.ResolvedSkill Inner => _inner;

    /// <summary>
    /// Gets the reference.
    /// </summary>
    public SkillReference Reference { get; }

    /// <summary>
    /// Gets the properties.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    internal static ResolvedSkill FromCore(Circuit.Core.ResolvedSkill inner) => new(inner);

    private static Circuit.Core.ResolvedSkill CreateCore(
        SkillReference reference,
        IReadOnlyDictionary<string, object?> properties)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(properties);

        return Circuit.Core.ResolvedSkill.Create(
            reference.Inner,
            properties.Select(static pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)));
    }

    private static IReadOnlyDictionary<string, object?> Copy(IReadOnlyDictionary<string, object> source)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            copy[pair.Key] = pair.Value;
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }
}

/// <summary>
/// Represents the validation issue.
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationIssue"/> class.
    /// </summary>
    public ValidationIssue(string path, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Path = path;
        Code = code;
        Message = message;
    }

    internal ValidationIssue(Circuit.Core.ValidationIssue inner)
        : this(inner.Path, inner.Code, inner.Message)
    {
    }

    /// <summary>
    /// Gets the path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }
}

/// <summary>
/// Represents the workflow validation issue.
/// </summary>
public sealed class WorkflowValidationIssue
{
    internal WorkflowValidationIssue(Circuit.Core.WorkflowValidationIssue inner)
    {
        NodeId = inner.NodeId;
        Code = inner.Code;
        Message = inner.Message;
    }

    /// <summary>
    /// Gets the node id.
    /// </summary>
    public string? NodeId { get; }

    /// <summary>
    /// Gets the code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; }
}

/// <summary>
/// Represents the run context.
/// </summary>
public sealed class RunContext
{
    internal RunContext(Circuit.Core.RunContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Agent = AgentDefinition.FromCore(inner.Agent);
        Options = AgentRunOptions.FromCore(inner.Options);
    }

    internal static RunContext FromCore(Circuit.Core.RunContext inner) => new(inner);

    internal Circuit.Core.RunContext Inner { get; }

    /// <summary>
    /// Gets the run id.
    /// </summary>
    public string RunId => Inner.RunId.Value;

    /// <summary>
    /// Gets the agent.
    /// </summary>
    public AgentDefinition Agent { get; }

    /// <summary>
    /// Gets the signature id.
    /// </summary>
    public string SignatureId => Inner.SignatureId.Value;

    /// <summary>
    /// Gets the signature version.
    /// </summary>
    public string SignatureVersion => Inner.SignatureVersion.ToString();

    /// <summary>
    /// Gets the options.
    /// </summary>
    public AgentRunOptions Options { get; }
}

/// <summary>
/// Represents the observed run event.
/// </summary>
public sealed class ObservedRunEvent
{
    internal ObservedRunEvent(
        string runId,
        DateTimeOffset timestamp,
        AgentRunEventKind kind,
        string? operationId,
        string? textDelta,
        AgentFailure? failure,
        ApprovalRequest? approval)
    {
        RunId = runId;
        Timestamp = timestamp;
        Kind = kind;
        OperationId = operationId;
        TextDelta = textDelta;
        Failure = failure;
        Approval = approval;
    }

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
    /// Gets the failure.
    /// </summary>
    public AgentFailure? Failure { get; }

    /// <summary>
    /// Gets the approval.
    /// </summary>
    public ApprovalRequest? Approval { get; }

    internal static ObservedRunEvent FromCore(
        string runId,
        DateTimeOffset timestamp,
        AgentRunEventKind kind,
        string? operationId,
        string? textDelta,
        AgentFailure? failure,
        ApprovalRequest? approval)
        => new(runId, timestamp, kind, operationId, textDelta, failure, approval);
}

/// <summary>
/// Represents the run observation.
/// </summary>
public sealed class RunObservation
{
    internal RunObservation(
        RunContext context,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        bool repaired,
        RunUsage usage,
        CircuitSession? session,
        AgentFailure? failure,
        IReadOnlyDictionary<string, string> diagnosticMetadata)
    {
        Context = context;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Repaired = repaired;
        Usage = usage;
        Session = session;
        Failure = failure;
        DiagnosticMetadata = Copy(diagnosticMetadata);
    }

    /// <summary>
    /// Gets the context.
    /// </summary>
    public RunContext Context { get; }

    /// <summary>
    /// Gets the started at.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Gets the completed at.
    /// </summary>
    public DateTimeOffset CompletedAt { get; }

    /// <summary>
    /// Gets the repaired.
    /// </summary>
    public bool Repaired { get; }

    /// <summary>
    /// Gets the usage.
    /// </summary>
    public RunUsage Usage { get; }

    /// <summary>
    /// Gets the session.
    /// </summary>
    public CircuitSession? Session { get; }

    /// <summary>
    /// Gets the failure.
    /// </summary>
    public AgentFailure? Failure { get; }

    /// <summary>
    /// Gets the diagnostic metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> DiagnosticMetadata { get; }

    internal static RunObservation FromCore(
        RunContext context,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        bool repaired,
        RunUsage usage,
        CircuitSession? session,
        AgentFailure? failure,
        IReadOnlyDictionary<string, string> diagnosticMetadata)
        => new(context, startedAt, completedAt, repaired, usage, session, failure, diagnosticMetadata);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>
/// Specifies the run operation kind.
/// </summary>
public enum RunOperationKind
{
    /// <summary>
    /// The root agent or workflow run.
    /// </summary>
    Run = 0,

    /// <summary>
    /// A tool operation.
    /// </summary>
    Tool = 1,

    /// <summary>
    /// A workflow step operation.
    /// </summary>
    WorkflowStep = 2,

    /// <summary>
    /// An approval operation.
    /// </summary>
    Approval = 3,
}

/// <summary>
/// Represents the run event envelope.
/// </summary>
public sealed class RunEventEnvelope
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDiagnosticMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private RunEventEnvelope(
        string runId,
        DateTimeOffset timestamp,
        AgentRunEventKind kind,
        string definitionId,
        string definitionVersion,
        string agentName,
        string operationId,
        string operationName,
        RunOperationKind operationKind,
        string? requestModel,
        string? textDelta,
        string? prompt,
        string? input,
        string? output,
        string? toolArguments,
        AgentFailure? failure,
        ApprovalRequest? approval,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool repaired,
        RunUsage? usage,
        CircuitSession? session,
        IReadOnlyDictionary<string, string>? diagnosticMetadata)
    {
        RunId = runId;
        Timestamp = timestamp;
        Kind = kind;
        DefinitionId = definitionId;
        DefinitionVersion = definitionVersion;
        AgentName = agentName;
        OperationId = operationId;
        OperationName = operationName;
        OperationKind = operationKind;
        RequestModel = requestModel;
        TextDelta = textDelta;
        Prompt = prompt;
        Input = input;
        Output = output;
        ToolArguments = toolArguments;
        Failure = failure;
        Approval = approval;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Repaired = repaired;
        Usage = usage;
        Session = session;
        DiagnosticMetadata = diagnosticMetadata is null
            ? EmptyDiagnosticMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(diagnosticMetadata, StringComparer.Ordinal));
    }

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
    /// Gets the definition id.
    /// </summary>
    public string DefinitionId { get; }

    /// <summary>
    /// Gets the definition version.
    /// </summary>
    public string DefinitionVersion { get; }

    /// <summary>
    /// Gets the agent name.
    /// </summary>
    public string AgentName { get; }

    /// <summary>
    /// Gets the operation id.
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// Gets the operation name.
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Gets the operation kind.
    /// </summary>
    public RunOperationKind OperationKind { get; }

    /// <summary>
    /// Gets the request model.
    /// </summary>
    public string? RequestModel { get; }

    /// <summary>
    /// Gets the text delta.
    /// </summary>
    public string? TextDelta { get; }

    /// <summary>
    /// Gets the prompt.
    /// </summary>
    public string? Prompt { get; }

    /// <summary>
    /// Gets the input.
    /// </summary>
    public string? Input { get; }

    /// <summary>
    /// Gets the output.
    /// </summary>
    public string? Output { get; }

    /// <summary>
    /// Gets the tool arguments.
    /// </summary>
    public string? ToolArguments { get; }

    /// <summary>
    /// Gets the failure.
    /// </summary>
    public AgentFailure? Failure { get; }

    /// <summary>
    /// Gets the approval.
    /// </summary>
    public ApprovalRequest? Approval { get; }

    /// <summary>
    /// Gets the started at.
    /// </summary>
    public DateTimeOffset? StartedAt { get; }

    /// <summary>
    /// Gets the completed at.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// Gets the repaired.
    /// </summary>
    public bool Repaired { get; }

    /// <summary>
    /// Gets the usage.
    /// </summary>
    public RunUsage? Usage { get; }

    /// <summary>
    /// Gets the session.
    /// </summary>
    public CircuitSession? Session { get; }

    /// <summary>
    /// Gets the diagnostic metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> DiagnosticMetadata { get; }

    internal static RunEventEnvelope Create(
        string runId,
        DateTimeOffset timestamp,
        AgentRunEventKind kind,
        string definitionId,
        string definitionVersion,
        string agentName,
        string operationId,
        string operationName,
        RunOperationKind operationKind,
        string? requestModel,
        string? textDelta,
        string? prompt,
        string? input,
        string? output,
        string? toolArguments,
        AgentFailure? failure,
        ApprovalRequest? approval,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        bool repaired,
        RunUsage? usage,
        CircuitSession? session,
        IReadOnlyDictionary<string, string>? diagnosticMetadata)
        => new(
            runId,
            timestamp,
            kind,
            definitionId,
            definitionVersion,
            agentName,
            operationId,
            operationName,
            operationKind,
            requestModel,
            textDelta,
            prompt,
            input,
            output,
            toolArguments,
            failure,
            approval,
            startedAt,
            completedAt,
            repaired,
            usage,
            session,
            diagnosticMetadata);
}

/// <summary>
/// Defines the run observer.
/// </summary>
public interface IRunObserver
{
    /// <summary>
    /// Observes a public run event.
    /// </summary>
    ValueTask OnEventAsync(RunEventEnvelope @event, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the microsoft agent framework options.
/// </summary>
public sealed class MicrosoftAgentFrameworkOptions
{
    private string? _defaultModelId;
    private JsonSerializerOptions? _jsonSerializerOptions;
    private IChatClient? _secondaryStructuredOutputClient;
    private bool _isFrozen;

    /// <summary>
    /// Gets the default model id.
    /// </summary>
    public string? DefaultModelId
    {
        get => _defaultModelId;
        set
        {
            ThrowIfFrozen();
            _defaultModelId = value;
        }
    }

    /// <summary>
    /// Gets the json serializer options.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions
    {
        get => _jsonSerializerOptions;
        set
        {
            ThrowIfFrozen();
            _jsonSerializerOptions = value;
        }
    }

    /// <summary>
    /// Gets the secondary structured output client.
    /// </summary>
    public IChatClient? SecondaryStructuredOutputClient
    {
        get => _secondaryStructuredOutputClient;
        set
        {
            ThrowIfFrozen();
            _secondaryStructuredOutputClient = value;
        }
    }

    internal MicrosoftAgentFrameworkOptions Snapshot()
    {
        var snapshot = new MicrosoftAgentFrameworkOptions
        {
            _defaultModelId = _defaultModelId,
            _jsonSerializerOptions = _jsonSerializerOptions is null ? null : CreateReadOnlyCopy(_jsonSerializerOptions),
            _secondaryStructuredOutputClient = _secondaryStructuredOutputClient,
            _isFrozen = true,
        };

        return snapshot;
    }

    internal void Freeze()
    {
        if (_isFrozen)
        {
            return;
        }

        if (_jsonSerializerOptions is not null)
        {
            _jsonSerializerOptions = CreateReadOnlyCopy(_jsonSerializerOptions);
        }

        _isFrozen = true;
    }

    private void ThrowIfFrozen()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException("Options are frozen.");
        }
    }

    private static JsonSerializerOptions CreateReadOnlyCopy(JsonSerializerOptions source)
    {
        var copy = new JsonSerializerOptions(source);
        copy.MakeReadOnly();
        return copy;
    }
}

/// <summary>
/// Represents the circuit options.
/// </summary>
public sealed class CircuitOptions
{
    private readonly List<IToolResolver> _toolResolvers = [];
    private readonly List<ISkillResolver> _skillResolvers = [];
    private readonly List<IRunObserver> _runObservers = [];
    private bool _isFrozen;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitOptions"/> class.
    /// </summary>
    public CircuitOptions()
        : this(new MicrosoftAgentFrameworkOptions())
    {
    }

    private CircuitOptions(MicrosoftAgentFrameworkOptions microsoftAgentFramework)
    {
        MicrosoftAgentFramework = microsoftAgentFramework;
    }

    /// <summary>
    /// Gets the microsoft agent framework.
    /// </summary>
    public MicrosoftAgentFrameworkOptions MicrosoftAgentFramework { get; }

    /// <summary>
    /// Gets the configured tool resolvers.
    /// </summary>
    public IReadOnlyList<IToolResolver> ToolResolvers => _toolResolvers.AsReadOnly();

    /// <summary>
    /// Gets the configured skill resolvers.
    /// </summary>
    public IReadOnlyList<ISkillResolver> SkillResolvers => _skillResolvers.AsReadOnly();

    /// <summary>
    /// Gets the configured run observers.
    /// </summary>
    public IReadOnlyList<IRunObserver> RunObservers => _runObservers.AsReadOnly();

    /// <summary>
    /// Adds tool resolver.
    /// </summary>
    public void AddToolResolver(IToolResolver resolver)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(resolver);
        _toolResolvers.Add(resolver);
    }

    /// <summary>
    /// Adds skill resolver.
    /// </summary>
    public void AddSkillResolver(ISkillResolver resolver)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(resolver);
        _skillResolvers.Add(resolver);
    }

    /// <summary>
    /// Adds run observer.
    /// </summary>
    public void AddRunObserver(IRunObserver observer)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(observer);
        _runObservers.Add(observer);
    }

    internal CircuitOptions Snapshot()
    {
        var snapshot = new CircuitOptions(MicrosoftAgentFramework.Snapshot());
        snapshot._toolResolvers.AddRange(_toolResolvers);
        snapshot._skillResolvers.AddRange(_skillResolvers);
        snapshot._runObservers.AddRange(_runObservers);
        snapshot._isFrozen = true;
        return snapshot;
    }

    private void ThrowIfFrozen()
    {
        if (_isFrozen)
        {
            throw new InvalidOperationException("Options are frozen.");
        }
    }
}

internal static class CoreAdapters
{
    internal static Circuit.Core.IContractValidator<T> ToCore<T>(IContractValidator<T> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new ContractValidatorAdapter<T>(validator);
    }

    internal static Circuit.Core.IToolResolver ToCore(IToolResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new ToolResolverAdapter(resolver);
    }

    internal static Circuit.Core.ISkillResolver ToCore(ISkillResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new SkillResolverAdapter(resolver);
    }

    private sealed class ContractValidatorAdapter<T>(IContractValidator<T> inner) : Circuit.Core.IContractValidator<T>
    {
        /// <summary>
        /// Validates validate.
        /// </summary>
        public IReadOnlyList<Circuit.Core.ValidationIssue> Validate(T value)
        {
            var issues = inner.Validate(value);
            if (issues is null)
            {
                return null!;
            }

            var converted = new Circuit.Core.ValidationIssue[issues.Count];
            for (var index = 0; index < issues.Count; index++)
            {
                var issue = issues[index];
                converted[index] = new Circuit.Core.ValidationIssue(issue.Path, issue.Code, issue.Message);
            }

            return converted;
        }
    }

    private sealed class ToolResolverAdapter(IToolResolver inner) : Circuit.Core.IToolResolver
    {
        /// <summary>
        /// Executes resolve async.
        /// </summary>
        public async ValueTask<IReadOnlyList<Circuit.Core.ResolvedTool>> ResolveAsync(
            Circuit.Core.ToolResolutionContext context,
            CancellationToken cancellationToken)
        {
            var tools = await inner.ResolveAsync(ToolResolutionContext.FromCore(context), cancellationToken).ConfigureAwait(false);
            if (tools is null)
            {
                return null!;
            }

            var converted = new Circuit.Core.ResolvedTool[tools.Count];
            for (var index = 0; index < tools.Count; index++)
            {
                converted[index] = tools[index] is null ? null! : tools[index].Inner;
            }

            return converted;
        }
    }

    private sealed class SkillResolverAdapter(ISkillResolver inner) : Circuit.Core.ISkillResolver
    {
        /// <summary>
        /// Executes resolve async.
        /// </summary>
        public async ValueTask<IReadOnlyList<Circuit.Core.ResolvedSkill>> ResolveAsync(
            Circuit.Core.SkillResolutionContext context,
            CancellationToken cancellationToken)
        {
            var skills = await inner.ResolveAsync(SkillResolutionContext.FromCore(context), cancellationToken).ConfigureAwait(false);
            if (skills is null)
            {
                return null!;
            }

            var converted = new Circuit.Core.ResolvedSkill[skills.Count];
            for (var index = 0; index < skills.Count; index++)
            {
                converted[index] = skills[index] is null ? null! : skills[index].Inner;
            }

            return converted;
        }
    }
}
