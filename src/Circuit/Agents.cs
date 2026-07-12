using System.Collections.ObjectModel;
using System.Text.Json;
using Circuit.Core;
using Microsoft.FSharp.Core;

namespace Circuit;

/// <summary>
/// Represents the skill reference.
/// </summary>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillReference"/> class.
    /// </summary>
    public SkillReference(string id, string version)
        : this(id, version, string.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillReference"/> class.
    /// </summary>
    public SkillReference(string id, string version, string description)
        : this(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateCustom(), []))
    {
    }

    internal Circuit.Core.SkillReference Inner { get; }

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
    /// Gets the metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Creates file.
    /// </summary>
    public static SkillReference CreateFile(string id, string version, IEnumerable<string> fileRoots, string description = "")
    {
        ArgumentNullException.ThrowIfNull(fileRoots);
        return new SkillReference(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateFile(fileRoots), []));
    }

    /// <summary>
    /// Creates inline.
    /// </summary>
    public static SkillReference CreateInline(string id, string version, string instructions, string description = "")
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return new SkillReference(Circuit.Core.SkillReference.Create(id, version, description, Circuit.Core.SkillSource.CreateInline(instructions), []));
    }

    internal static SkillReference FromCore(Circuit.Core.SkillReference reference) => new(reference);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.Ordinal));
}

/// <summary>
/// Represents the agent definition.
/// </summary>
public sealed class AgentDefinition
{
    private readonly Circuit.Core.AgentDefinition _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentDefinition"/> class.
    /// </summary>
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

    /// <summary>
    /// Gets the id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the instructions.
    /// </summary>
    public string Instructions { get; }

    /// <summary>
    /// Gets the model hint.
    /// </summary>
    public string? ModelHint { get; }

    /// <summary>
    /// Gets the tool tags.
    /// </summary>
    public IReadOnlyList<string> ToolTags { get; }

    /// <summary>
    /// Gets the skills.
    /// </summary>
    public IReadOnlyList<SkillReference> Skills { get; }

    /// <summary>
    /// Gets the metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Returns a copy with model hint.
    /// </summary>
    public AgentDefinition WithModelHint(string modelHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelHint);
        return Recreate(FSharpValueOption<string>.Some(modelHint), ToolTags, Skills);
    }

    /// <summary>
    /// Returns a copy with tool tags.
    /// </summary>
    public AgentDefinition WithToolTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return Recreate(ModelHint is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ModelHint), tags, Skills);
    }

    /// <summary>
    /// Returns a copy with skills.
    /// </summary>
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

/// <summary>
/// Represents the agent signature.
/// </summary>
public sealed class AgentSignature<TInput, TOutput>
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();
    private readonly Circuit.Core.Signature<TInput, TOutput> _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSignature{TInput, TOutput}"/> class.
    /// </summary>
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
    /// Gets the instructions.
    /// </summary>
    public string Instructions { get; }

    /// <summary>
    /// Gets the input json schema.
    /// </summary>
    public string InputJsonSchema { get; }

    /// <summary>
    /// Gets the output json schema.
    /// </summary>
    public string OutputJsonSchema { get; }

    /// <summary>
    /// Adds input validator.
    /// </summary>
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

    /// <summary>
    /// Adds output validator.
    /// </summary>
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

/// <summary>
/// Represents the tool definition.
/// </summary>
public sealed class ToolDefinition<TInput, TOutput>
{
    private readonly Circuit.Core.ToolDefinition<TInput, TOutput> _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolDefinition{TInput, TOutput}"/> class.
    /// </summary>
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
    /// Gets the input json schema.
    /// </summary>
    public string InputJsonSchema { get; }

    /// <summary>
    /// Gets the output json schema.
    /// </summary>
    public string OutputJsonSchema { get; }

    /// <summary>
    /// Gets the approval mode.
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; }

    /// <summary>
    /// Gets the approval policy.
    /// </summary>
    public string? ApprovalPolicy { get; }

    /// <summary>
    /// Adds input validator.
    /// </summary>
    public ToolDefinition<TInput, TOutput> AddInputValidator(IContractValidator<TInput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new ToolDefinition<TInput, TOutput>(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                Circuit.Core.Contract<TInput>.Create(CloneContractJsonOptions(_inner.Input), _inner.Input.Validators.Append(CoreAdapters.ToCore(validator))),
                _inner.Output,
                (Circuit.Core.ApprovalMode)ApprovalMode,
                ApprovalPolicy is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ApprovalPolicy),
                CreateHandler(_inner)));
    }

    /// <summary>
    /// Adds output validator.
    /// </summary>
    public ToolDefinition<TInput, TOutput> AddOutputValidator(IContractValidator<TOutput> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return new ToolDefinition<TInput, TOutput>(
            Circuit.Core.ToolDefinition<TInput, TOutput>.Create(
                Id,
                Version,
                Description,
                _inner.Input,
                Circuit.Core.Contract<TOutput>.Create(CloneContractJsonOptions(_inner.Output), _inner.Output.Validators.Append(CoreAdapters.ToCore(validator))),
                (Circuit.Core.ApprovalMode)ApprovalMode,
                ApprovalPolicy is null ? FSharpValueOption<string>.None : FSharpValueOption<string>.Some(ApprovalPolicy),
                CreateHandler(_inner)));
    }

    /// <summary>
    /// Returns a copy with approval.
    /// </summary>
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

    /// <summary>
    /// Returns a copy with approval policy.
    /// </summary>
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

    /// <summary>
    /// Executes to resolved tool.
    /// </summary>
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

    private static JsonSerializerOptions CloneContractJsonOptions<TContract>(Circuit.Core.Contract<TContract> contract)
    {
        var options = new JsonSerializerOptions(contract.JsonSerializerOptions);
        options.MakeReadOnly();
        return options;
    }
}
