#pragma warning disable CS1591

using System.Collections.ObjectModel;
using System.Text.Json;
using Circuit.Core;
using Microsoft.FSharp.Core;

namespace Circuit;

public sealed class SkillReference
{
    private SkillReference(Circuit.Core.SkillReference inner)
    {
        Inner = inner;
        Id = inner.Id.Value;
        Version = inner.Version.ToString();
        Description = inner.Description;
        Metadata = Copy(inner.Metadata);
    }

    public SkillReference(string id, string version)
        : this(id, version, string.Empty)
    {
    }

    public SkillReference(string id, string version, string description)
        : this(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateCustom(), []))
    {
    }

    internal Circuit.Core.SkillReference Inner { get; }

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static SkillReference CreateFile(string id, string version, IEnumerable<string> fileRoots, string description = "")
    {
        ArgumentNullException.ThrowIfNull(fileRoots);
        return new SkillReference(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateFile(fileRoots), []));
    }

    public static SkillReference CreateInline(string id, string version, string instructions, string description = "")
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return new SkillReference(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateInline(instructions), []));
    }

    internal static SkillReference FromCore(Circuit.Core.SkillReference reference) => new(reference);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

public sealed class AgentDefinition
{
    private readonly Circuit.Core.AgentDefinition _inner;

    public AgentDefinition(string id, string version, string name, string instructions)
        : this(Circuit.Core.AgentDefinition.Create(id, version, name, instructions, FSharpValueOption<string>.None, [], [], []))
    {
    }

    private AgentDefinition(Circuit.Core.AgentDefinition inner)
    {
        _inner = inner;
        Id = inner.Id.Value;
        Version = inner.Version.ToString();
        Name = inner.Name;
        Instructions = inner.Instructions;
        ModelHint = inner.ModelHint.IsSome ? inner.ModelHint.Value : null;
        ToolTags = Array.AsReadOnly(inner.ToolTags.OrderBy(static tag => tag, StringComparer.Ordinal).ToArray());
        Skills = Array.AsReadOnly(inner.Skills.Select(SkillReference.FromCore).ToArray());
        Metadata = Copy(inner.Metadata);
    }

    internal Circuit.Core.AgentDefinition Inner => _inner;

    public string Id { get; }

    public string Version { get; }

    public string Name { get; }

    public string Instructions { get; }

    public string? ModelHint { get; }

    public IReadOnlyList<string> ToolTags { get; }

    public IReadOnlyList<SkillReference> Skills { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public AgentDefinition WithModelHint(string modelHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelHint);
        return Recreate(FSharpValueOption<string>.Some(modelHint), ToolTags, Skills);
    }

    public AgentDefinition WithToolTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return Recreate(ModelHint is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ModelHint), tags, Skills);
    }

    public AgentDefinition WithSkills(IEnumerable<SkillReference> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return Recreate(ModelHint is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ModelHint), ToolTags, skills);
    }

    internal static AgentDefinition FromCore(Circuit.Core.AgentDefinition definition) => new(definition);

    private AgentDefinition Recreate(FSharpValueOption<string> modelHint, IEnumerable<string> tags, IEnumerable<SkillReference> skills)
        => new(
            Circuit.Core.AgentDefinition.Create(
                Id,
                Version,
                Name,
                Instructions,
                modelHint,
                tags,
                skills.Select(static skill => skill.Inner),
                Metadata.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))));

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

public sealed class AgentSignature<TInput, TOutput>
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();
    private readonly Circuit.Core.Signature<TInput, TOutput> _inner;

    public AgentSignature(string id, string version, string description, string instructions)
        : this(Circuit.Core.Signature<TInput, TOutput>.Create(id, version, description, instructions, DefaultJsonOptions, [], []))
    {
    }

    private AgentSignature(Circuit.Core.Signature<TInput, TOutput> inner)
    {
        _inner = inner;
        Id = inner.Id.Value;
        Version = inner.Version.ToString();
        Description = inner.Description;
        Instructions = inner.Instructions;
        InputJsonSchema = inner.Input.Schema.ToJsonString();
        OutputJsonSchema = inner.Output.Schema.ToJsonString();
    }

    internal Circuit.Core.Signature<TInput, TOutput> Inner => _inner;

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public string Instructions { get; }

    public string InputJsonSchema { get; }

    public string OutputJsonSchema { get; }

    public AgentSignature<TInput, TOutput> AddInputValidator(IContractValidator<TInput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new AgentSignature<TInput, TOutput>(
            Circuit.Core.Signature<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                Instructions,
                _inner.JsonSerializerOptions,
                _inner.Input.Validators.Append(CoreAdapters.ToCore(validator)),
                _inner.Output.Validators));
    }

    public AgentSignature<TInput, TOutput> AddOutputValidator(IContractValidator<TOutput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new AgentSignature<TInput, TOutput>(
            Circuit.Core.Signature<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                Instructions,
                _inner.JsonSerializerOptions,
                _inner.Input.Validators,
                _inner.Output.Validators.Append(CoreAdapters.ToCore(validator))));
    }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = Circuit.Core.CircuitJson.createOptions();
        options.MakeReadOnly();
        return options;
    }
}

public sealed class ToolDefinition<TInput, TOutput>
{
    private readonly Circuit.Core.ToolDefinition<TInput, TOutput> _inner;

    public ToolDefinition(
        string id,
        string version,
        string description,
        Func<ToolContext, TInput, CancellationToken, Task<TOutput>> handler)
        : this(CreateCore(id, version, description, handler))
    {
    }

    private ToolDefinition(Circuit.Core.ToolDefinition<TInput, TOutput> inner)
    {
        _inner = inner;
        Id = inner.Name.Value;
        Version = inner.Version.ToString();
        Description = inner.Description;
        InputJsonSchema = inner.Input.Schema.ToJsonString();
        OutputJsonSchema = inner.Output.Schema.ToJsonString();
        ApprovalMode = (ToolApprovalMode)inner.Approval;
        ApprovalPolicy = inner.ApprovalPolicy.IsSome ? inner.ApprovalPolicy.Value : null;
    }

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public string InputJsonSchema { get; }

    public string OutputJsonSchema { get; }

    public ToolApprovalMode ApprovalMode { get; }

    public string? ApprovalPolicy { get; }

    public ToolDefinition<TInput, TOutput> AddInputValidator(IContractValidator<TInput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new ToolDefinition<TInput, TOutput>(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                Circuit.Core.Contract<TInput>.Create(CreateDefaultJsonOptions(), _inner.Input.Validators.Append(CoreAdapters.ToCore(validator))),
                _inner.Output,
                (Circuit.Core.ApprovalMode)ApprovalMode,
                ApprovalPolicy is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ApprovalPolicy),
                CreateHandler(_inner)));
    }

    public ToolDefinition<TInput, TOutput> AddOutputValidator(IContractValidator<TOutput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new ToolDefinition<TInput, TOutput>(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                _inner.Input,
                Circuit.Core.Contract<TOutput>.Create(CreateDefaultJsonOptions(), _inner.Output.Validators.Append(CoreAdapters.ToCore(validator))),
                (Circuit.Core.ApprovalMode)ApprovalMode,
                ApprovalPolicy is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ApprovalPolicy),
                CreateHandler(_inner)));
    }

    public ToolDefinition<TInput, TOutput> WithApproval(ToolApprovalMode approvalMode)
        => new(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                _inner.Input,
                _inner.Output,
                (Circuit.Core.ApprovalMode)approvalMode,
                approvalMode == ToolApprovalMode.ByPolicy && ApprovalPolicy is not null
                    ? FSharpValueOption<string>.Some(ApprovalPolicy)
                    : FSharpValueOption<string>.None,
                CreateHandler(_inner)));

    public ToolDefinition<TInput, TOutput> WithApprovalPolicy(string approvalPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalPolicy);
        return new(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                _inner.Input,
                _inner.Output,
                Circuit.Core.ApprovalMode.ByPolicy,
                FSharpValueOption<string>.Some(approvalPolicy),
                CreateHandler(_inner)));
    }

    public ResolvedTool ToResolvedTool(IEnumerable<string>? tags = null)
        => ResolvedTool.FromCore(Circuit.Core.ResolvedTool.Create(_inner, tags ?? []));

    internal Circuit.Core.ToolDefinition<TInput, TOutput> Inner => _inner;

    private static Circuit.Core.ToolDefinition<TInput, TOutput> CreateCore(
        string id,
        string version,
        string description,
        Func<ToolContext, TInput, CancellationToken, Task<TOutput>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var options = Circuit.Core.CircuitJson.createOptions();
        options.MakeReadOnly();

        return Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
            id,
            version,
            description,
            Circuit.Core.Contract<TInput>.Create(options, []),
            Circuit.Core.Contract<TOutput>.Create(options, []),
            Circuit.Core.ApprovalMode.Always,
            FSharpValueOption<string>.None,
            new Func<Circuit.Core.ToolContext, TInput, Task<TOutput>>((context, input) => handler(new ToolContext(context), input, context.CancellationToken)));
    }

    private static Func<Circuit.Core.ToolContext, TInput, Task<TOutput>> CreateHandler(Circuit.Core.ToolDefinition<TInput, TOutput> definition)
        => (context, input) => definition.InvokeAsync(context, input);

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = Circuit.Core.CircuitJson.createOptions();
        options.MakeReadOnly();
        return options;
    }
}
