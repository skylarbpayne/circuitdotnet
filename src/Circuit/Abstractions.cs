#pragma warning disable CS1591

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Circuit.Core;
using Microsoft.Extensions.AI;

namespace Circuit;

public interface IContractValidator<T>
{
    IReadOnlyList<ValidationIssue> Validate(T value);
}

public interface IToolResolver
{
    ValueTask<IReadOnlyList<ResolvedTool>> ResolveAsync(ToolResolutionContext context, CancellationToken cancellationToken);
}

public interface ISkillResolver
{
    ValueTask<IReadOnlyList<ResolvedSkill>> ResolveAsync(SkillResolutionContext context, CancellationToken cancellationToken);
}

public enum AgentFailureCode
{
    Validation = (int)CircuitFailureCode.Validation,
    StructuredOutputUnsupported = (int)CircuitFailureCode.StructuredOutputUnsupported,
    Decode = (int)CircuitFailureCode.Decode,
    Provider = (int)CircuitFailureCode.Provider,
    Tool = (int)CircuitFailureCode.Tool,
    ApprovalRequired = (int)CircuitFailureCode.ApprovalRequired,
    Skill = (int)CircuitFailureCode.Skill,
    Workflow = (int)CircuitFailureCode.Workflow,
    CheckpointMismatch = (int)CircuitFailureCode.CheckpointMismatch,
    Cancelled = (int)CircuitFailureCode.Cancelled,
}

public enum StructuredOutputPolicy
{
    NativeOnly = (int)Circuit.Core.StructuredOutputPolicy.NativeOnly,
    AllowSecondaryModelRepair = (int)Circuit.Core.StructuredOutputPolicy.AllowSecondaryModelRepair,
}

public enum SensitiveDataMode
{
    Standard = (int)Circuit.Core.SensitiveDataMode.Standard,
    Redact = (int)Circuit.Core.SensitiveDataMode.Redact,
}

public enum AgentRunEventKind
{
    RunStarted = (int)RunEventKind.RunStarted,
    OutputDelta = (int)RunEventKind.OutputDelta,
    ToolStarted = (int)RunEventKind.ToolStarted,
    ToolCompleted = (int)RunEventKind.ToolCompleted,
    ApprovalRequested = (int)RunEventKind.ApprovalRequested,
    StepStarted = (int)RunEventKind.StepStarted,
    StepCompleted = (int)RunEventKind.StepCompleted,
    IntermediateOutput = (int)RunEventKind.IntermediateOutput,
    RunCompleted = (int)RunEventKind.RunCompleted,
    RunFailed = (int)RunEventKind.RunFailed,
}

public enum ToolApprovalMode
{
    Never = (int)ApprovalMode.Never,
    Always = (int)ApprovalMode.Always,
    ByPolicy = (int)ApprovalMode.ByPolicy,
}

public sealed class ToolContext
{
    internal ToolContext(Circuit.Core.ToolContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal Circuit.Core.ToolContext Inner { get; }

    public string RunId => Inner.RunId.Value;

    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    public IServiceProvider Services => Inner.Services;

    public CancellationToken CancellationToken => Inner.CancellationToken;
}

public sealed class ToolResolutionContext
{
    internal ToolResolutionContext(Circuit.Core.ToolResolutionContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal static ToolResolutionContext FromCore(Circuit.Core.ToolResolutionContext inner) => new(inner);

    internal Circuit.Core.ToolResolutionContext Inner { get; }

    public string RunId => Inner.RunId.Value;

    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    public IServiceProvider Services => Inner.Services;
}

public sealed class SkillResolutionContext
{
    internal SkillResolutionContext(Circuit.Core.SkillResolutionContext inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal static SkillResolutionContext FromCore(Circuit.Core.SkillResolutionContext inner) => new(inner);

    internal Circuit.Core.SkillResolutionContext Inner { get; }

    public string RunId => Inner.RunId.Value;

    public string? TenantId => Inner.TenantId.IsSome ? Inner.TenantId.Value : null;

    public string? UserId => Inner.UserId.IsSome ? Inner.UserId.Value : null;

    public IServiceProvider Services => Inner.Services;
}

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

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public ToolApprovalMode ApprovalMode { get; }

    public string? ApprovalPolicy { get; }

    public IReadOnlySet<string> Tags { get; }

    public string InputJsonSchema { get; }

    public string OutputJsonSchema { get; }

    internal static ResolvedTool FromCore(Circuit.Core.ResolvedTool inner) => new(inner);
}

public sealed class ResolvedSkill
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProperties =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    private readonly Circuit.Core.ResolvedSkill _inner;

    public ResolvedSkill(SkillReference reference)
        : this(reference, EmptyProperties)
    {
    }

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

    public SkillReference Reference { get; }

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

public sealed class ValidationIssue
{
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

    public string Path { get; }

    public string Code { get; }

    public string Message { get; }
}

public sealed class WorkflowValidationIssue
{
    internal WorkflowValidationIssue(Circuit.Core.WorkflowValidationIssue inner)
    {
        NodeId = inner.NodeId;
        Code = inner.Code;
        Message = inner.Message;
    }

    public string? NodeId { get; }

    public string Code { get; }

    public string Message { get; }
}

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

    public string RunId => Inner.RunId.Value;

    public AgentDefinition Agent { get; }

    public string SignatureId => Inner.SignatureId.Value;

    public string SignatureVersion => Inner.SignatureVersion.ToString();

    public AgentRunOptions Options { get; }
}

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

    public string RunId { get; }

    public DateTimeOffset Timestamp { get; }

    public AgentRunEventKind Kind { get; }

    public string? OperationId { get; }

    public string? TextDelta { get; }

    public AgentFailure? Failure { get; }

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

    public RunContext Context { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public bool Repaired { get; }

    public RunUsage Usage { get; }

    public CircuitSession? Session { get; }

    public AgentFailure? Failure { get; }

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

public interface IRunObserver
{
    ValueTask OnRunStartedAsync(RunContext context, DateTimeOffset startedAt, CancellationToken cancellationToken);

    ValueTask OnRunEventAsync(ObservedRunEvent @event, CancellationToken cancellationToken);

    ValueTask OnRunCompletedAsync(RunObservation observation, CancellationToken cancellationToken);
}

public sealed class MicrosoftAgentFrameworkOptions
{
    private string? _defaultModelId;
    private JsonSerializerOptions? _jsonSerializerOptions;
    private IChatClient? _secondaryStructuredOutputClient;
    private bool _isFrozen;

    public string? DefaultModelId
    {
        get => _defaultModelId;
        set
        {
            ThrowIfFrozen();
            _defaultModelId = value;
        }
    }

    public JsonSerializerOptions? JsonSerializerOptions
    {
        get => _jsonSerializerOptions;
        set
        {
            ThrowIfFrozen();
            _jsonSerializerOptions = value;
        }
    }

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

public sealed class CircuitOptions
{
    private readonly List<IToolResolver> _toolResolvers = [];
    private readonly List<ISkillResolver> _skillResolvers = [];
    private readonly List<IRunObserver> _runObservers = [];
    private bool _isFrozen;

    public CircuitOptions()
        : this(new MicrosoftAgentFrameworkOptions())
    {
    }

    private CircuitOptions(MicrosoftAgentFrameworkOptions microsoftAgentFramework)
    {
        MicrosoftAgentFramework = microsoftAgentFramework;
    }

    public MicrosoftAgentFrameworkOptions MicrosoftAgentFramework { get; }

    public IReadOnlyList<IToolResolver> ToolResolvers => _toolResolvers.AsReadOnly();

    public IReadOnlyList<ISkillResolver> SkillResolvers => _skillResolvers.AsReadOnly();

    public IReadOnlyList<IRunObserver> RunObservers => _runObservers.AsReadOnly();

    public void AddToolResolver(IToolResolver resolver)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(resolver);
        _toolResolvers.Add(resolver);
    }

    public void AddSkillResolver(ISkillResolver resolver)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(resolver);
        _skillResolvers.Add(resolver);
    }

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
