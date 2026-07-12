namespace Circuit.Core

open System
open System.Collections.Generic
open System.Collections.Frozen
open System.Threading
open System.Threading.Tasks

/// Controls whether a tool call requires explicit approval before execution.
[<RequireQualifiedAccess>]
type ApprovalMode =
    /// Never request approval. Use only for tools that are already safe for automatic execution.
    | Never = 0
    /// Always request approval before the tool runs.
    | Always = 1
    /// Defer the approval decision to a named runtime policy.
    | ByPolicy = 2

module private ToolValidation =
    let private toolTagCharacters =
        Set.ofList ([ 'a' .. 'z' ] @ [ '0' .. '9' ] @ [ '.'; '_'; '-' ])

    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let validateToolTag (value: string) =
        let normalized = requireNonBlank "tags" value

        if
            normalized
            |> Seq.exists (fun character -> not (toolTagCharacters.Contains character))
        then
            invalidArg "tags" "Tool tags must contain only lowercase letters, digits, '.', '_', or '-'."

        normalized

    let validateApprovalPolicy (approval: ApprovalMode) (approvalPolicy: string voption) =
        match approvalPolicy with
        | ValueSome value when String.IsNullOrWhiteSpace value ->
            invalidArg "approvalPolicy" "approvalPolicy cannot be blank when provided."
        | ValueSome _ when approval <> ApprovalMode.ByPolicy ->
            invalidArg "approvalPolicy" "approvalPolicy can only be set when approval is ByPolicy."
        | _ -> approvalPolicy

    let createTypeIssue (expectedType: Type) =
        [| { Path = "$"
             Code = "type"
             Message = $"Value must be assignable to '{expectedType.FullName}'." } |]
        :> IReadOnlyList<ValidationIssue>

/// Provides the ambient inputs available to a tool invocation.
[<Sealed>]
type ToolContext
    internal
    (
        runId: RunId,
        tenantId: string voption,
        userId: string voption,
        services: IServiceProvider,
        cancellationToken: CancellationToken
    ) =
    do
        if isNull services then
            nullArg "services"

    /// Gets the owning run identifier.
    member _.RunId = runId

    /// Gets the tenant identifier for the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier for the run, if any.
    member _.UserId = userId

    /// Gets the ambient service provider.
    member _.Services = services

    /// Gets the cancellation token for the current invocation.
    member _.CancellationToken = cancellationToken

/// Provides the ambient inputs available while resolving tools for a run.
[<Sealed>]
type ToolResolutionContext
    internal (runId: RunId, tenantId: string voption, userId: string voption, services: IServiceProvider) =
    do
        if isNull services then
            nullArg "services"

    /// Gets the owning run identifier.
    member _.RunId = runId

    /// Gets the tenant identifier for the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier for the run, if any.
    member _.UserId = userId

    /// Gets the ambient service provider.
    member _.Services = services

/// Describes a strongly typed tool contract and implementation.
/// <remarks>
/// Approval settings are metadata only. Runtimes may still impose stricter policy or reject unsafe tool configurations.
/// </remarks>
[<Sealed>]
type ToolDefinition<'Input, 'Output>
    internal
    (
        name: DefinitionId,
        version: SemanticVersion,
        description: string,
        input: Contract<'Input>,
        output: Contract<'Output>,
        approval: ApprovalMode,
        approvalPolicy: string voption,
        invokeAsync: Func<ToolContext, 'Input, Task<'Output>>
    ) =
    do
        if isNull (box input) then
            nullArg "input"

        if isNull (box output) then
            nullArg "output"

        if isNull invokeAsync then
            nullArg "invokeAsync"

        ToolValidation.requireNonBlank "description" description |> ignore
        ToolValidation.validateApprovalPolicy approval approvalPolicy |> ignore

    /// Gets the tool identifier.
    member _.Name = name

    /// Gets the tool version.
    member _.Version = version

    /// Gets the human-readable tool description.
    member _.Description = description

    /// Gets the validated input contract.
    member _.Input = input

    /// Gets the validated output contract.
    member _.Output = output

    /// Gets the approval behavior requested for the tool.
    member _.Approval = approval

    /// Gets the named approval policy when <see cref="P:Circuit.Core.ToolDefinition`2.Approval" /> is <see cref="F:Circuit.Core.ApprovalMode.ByPolicy" />.
    member _.ApprovalPolicy = approvalPolicy

    /// Invokes the tool implementation.
    /// <param name="context">The tool invocation context.</param>
    /// <param name="input">The validated tool input.</param>
    member _.InvokeAsync(context: ToolContext, input: 'Input) = invokeAsync.Invoke(context, input)

    /// Creates a tool definition with explicit approval metadata.
    static member Create
        (
            id: string,
            version: string,
            description: string,
            input: Contract<'Input>,
            output: Contract<'Output>,
            approval: ApprovalMode,
            approvalPolicy: string voption,
            invokeAsync: Func<ToolContext, 'Input, Task<'Output>>
        ) =
        ToolDefinition(
            DefinitionId.Create id,
            SemanticVersion.Parse version,
            ToolValidation.requireNonBlank "description" description,
            input,
            output,
            approval,
            ToolValidation.validateApprovalPolicy approval approvalPolicy,
            invokeAsync
        )

    /// Creates a tool definition that always requests approval.
    static member Create
        (
            id: string,
            version: string,
            description: string,
            input: Contract<'Input>,
            output: Contract<'Output>,
            invokeAsync: Func<ToolContext, 'Input, Task<'Output>>
        ) =
        ToolDefinition.Create(id, version, description, input, output, ApprovalMode.Always, ValueNone, invokeAsync)

type internal IResolvedToolExecutor =
    abstract InputType: Type
    abstract OutputType: Type
    abstract InputSchema: SchemaDocument
    abstract OutputSchema: SchemaDocument
    abstract ValidateInput: obj -> IReadOnlyList<ValidationIssue>
    abstract ValidateOutput: obj -> IReadOnlyList<ValidationIssue>
    abstract InvokeAsync: ToolContext * obj -> Task<obj>

type internal ResolvedToolExecutor<'Input, 'Output>(definition: ToolDefinition<'Input, 'Output>) =
    interface IResolvedToolExecutor with
        member _.InputType = definition.Input.ValueType
        member _.OutputType = definition.Output.ValueType
        member _.InputSchema = definition.Input.Schema
        member _.OutputSchema = definition.Output.Schema

        member _.ValidateInput(value: obj) =
            match value with
            | :? 'Input as typed -> definition.Input.Validate typed
            | _ -> ToolValidation.createTypeIssue definition.Input.ValueType

        member _.ValidateOutput(value: obj) =
            match value with
            | :? 'Output as typed -> definition.Output.Validate typed
            | _ -> ToolValidation.createTypeIssue definition.Output.ValueType

        member _.InvokeAsync(context: ToolContext, value: obj) =
            match value with
            | :? 'Input as typed ->
                definition
                    .InvokeAsync(context, typed)
                    .ContinueWith(
                        Func<Task<'Output>, obj>(fun completed -> box (completed.GetAwaiter().GetResult())),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
            | _ ->
                Task.FromException<obj>(
                    InvalidOperationException($"Tool '{definition.Name.Value}' received an invalid input type.")
                )

/// Represents a resolved runtime-ready tool descriptor.
/// <remarks>
/// Resolved tools erase generic type parameters so runtimes can reason about tool catalogs dynamically.
/// </remarks>
[<Sealed>]
type ResolvedTool
    internal
    (
        name: DefinitionId,
        version: SemanticVersion,
        description: string,
        approval: ApprovalMode,
        approvalPolicy: string voption,
        tags: IReadOnlySet<string>,
        executor: IResolvedToolExecutor
    ) =
    /// Gets the tool identifier.
    member _.Name = name

    /// Gets the tool version.
    member _.Version = version

    /// Gets the human-readable tool description.
    member _.Description = description

    /// Gets the approval behavior requested for the tool.
    member _.Approval = approval

    /// Gets the named approval policy when one was configured.
    member _.ApprovalPolicy = approvalPolicy

    /// Gets the tool tags used during matching.
    member _.Tags = tags

    /// Gets the runtime input CLR type.
    member _.InputType = executor.InputType

    /// Gets the runtime output CLR type.
    member _.OutputType = executor.OutputType

    /// Gets the input JSON Schema.
    member _.InputSchema = executor.InputSchema

    /// Gets the output JSON Schema.
    member _.OutputSchema = executor.OutputSchema

    member internal _.ValidateInput(value: obj) = executor.ValidateInput value
    member internal _.ValidateOutput(value: obj) = executor.ValidateOutput value
    member internal _.InvokeAsync(context: ToolContext, value: obj) = executor.InvokeAsync(context, value)

    /// Creates a resolved tool from a typed definition and explicit tags.
    static member Create(definition: ToolDefinition<'Input, 'Output>, tags: IEnumerable<string>) =
        if isNull (box definition) then
            nullArg "definition"

        if isNull tags then
            nullArg "tags"

        let normalizedTags = tags |> Seq.map ToolValidation.validateToolTag |> Seq.toArray
        let tagSet = HashSet<string>(StringComparer.Ordinal)

        for tag in normalizedTags do
            if not (tagSet.Add tag) then
                invalidArg "tags" "Duplicate tool tags are not allowed."

        ResolvedTool(
            definition.Name,
            definition.Version,
            definition.Description,
            definition.Approval,
            definition.ApprovalPolicy,
            tagSet.ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>,
            ResolvedToolExecutor<'Input, 'Output>(definition)
        )

    /// Creates a resolved tool without tags.
    static member Create(definition: ToolDefinition<'Input, 'Output>) =
        ResolvedTool.Create(definition, Seq.empty)

/// Resolves the tool catalog available to a run.
type IToolResolver =
    /// Resolves tools for the supplied run context.
    abstract ResolveAsync:
        context: ToolResolutionContext * cancellationToken: CancellationToken -> ValueTask<IReadOnlyList<ResolvedTool>>

/// Returns a fixed tool list for every resolution request.
[<Sealed>]
type StaticToolResolver(tools: IEnumerable<ResolvedTool>) =
    let snapshot =
        if isNull tools then
            nullArg "tools"

        tools |> Seq.toArray

    interface IToolResolver with
        member _.ResolveAsync(_context, _cancellationToken) =
            ValueTask<IReadOnlyList<ResolvedTool>>(snapshot :> IReadOnlyList<ResolvedTool>)

/// Resolves tools through a caller-supplied delegate.
[<Sealed>]
type DelegateToolResolver
    (resolver: Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>) =
    do
        if isNull resolver then
            nullArg "resolver"

    interface IToolResolver with
        member _.ResolveAsync(context, cancellationToken) =
            resolver.Invoke(context, cancellationToken)

module internal ToolResolution =
    let private emptyTools = Array.empty<ResolvedTool> :> IReadOnlyList<ResolvedTool>

    let private validateResolvedTool (tool: ResolvedTool) =
        if isNull (box tool) then
            invalidOp "Tool resolvers cannot return null tool entries."

        ToolValidation.requireNonBlank "tool.Name" tool.Name.Value |> ignore
        ToolValidation.requireNonBlank "tool.Description" tool.Description |> ignore

        ToolValidation.validateApprovalPolicy tool.Approval tool.ApprovalPolicy
        |> ignore

        if isNull tool.Tags then
            invalidOp "Resolved tool tags cannot be null."

        if isNull tool.InputType then
            invalidOp "Resolved tool input type cannot be null."

        if isNull tool.OutputType then
            invalidOp "Resolved tool output type cannot be null."

        if isNull (box tool.InputSchema) then
            invalidOp "Resolved tool input schema cannot be null."

        if isNull (box tool.OutputSchema) then
            invalidOp "Resolved tool output schema cannot be null."

    let private appendResolvedTools
        (tools: ResizeArray<ResolvedTool>)
        (identities: HashSet<string>)
        (resolvedTools: IReadOnlyList<ResolvedTool>)
        =
        if isNull resolvedTools then
            invalidOp "Tool resolvers cannot return null tool lists."

        for tool in resolvedTools do
            validateResolvedTool tool

            let identity = $"{tool.Name.Value}:{tool.Version.Value.Major}"

            if not (identities.Add identity) then
                invalidOp
                    $"Duplicate tool identity '{tool.Name.Value}' with major version '{tool.Version.Value.Major}' was resolved."

            tools.Add tool

    let resolveAllAsync
        (resolvers: IReadOnlyList<IToolResolver>)
        (context: ToolResolutionContext)
        (cancellationToken: CancellationToken)
        =
        try
            if isNull resolvers then
                nullArg "resolvers"

            if isNull (box context) then
                nullArg "context"

            if resolvers.Count = 0 then
                Task.FromResult(emptyTools)
            else
                let tasks = Array.zeroCreate<Task<IReadOnlyList<ResolvedTool>>> resolvers.Count

                for index = 0 to resolvers.Count - 1 do
                    let resolver = resolvers[index]

                    if isNull (box resolver) then
                        invalidOp "Tool resolvers cannot contain null entries."

                    tasks[index] <- resolver.ResolveAsync(context, cancellationToken).AsTask()

                (Task.WhenAll tasks)
                    .ContinueWith(
                        Func<Task<IReadOnlyList<ResolvedTool>[]>, IReadOnlyList<ResolvedTool>>(fun completed ->
                            let resolvedBatches = completed.GetAwaiter().GetResult()
                            let tools = ResizeArray<ResolvedTool>()
                            let identities = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                            for resolvedTools in resolvedBatches do
                                appendResolvedTools tools identities resolvedTools

                            tools.ToArray() :> IReadOnlyList<ResolvedTool>),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    )
        with ex ->
            Task.FromException<IReadOnlyList<ResolvedTool>>(ex)
