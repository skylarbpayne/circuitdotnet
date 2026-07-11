namespace Circuit.Core

open System
open System.Collections.Generic
open System.Collections.Frozen
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type ApprovalMode =
    | Never = 0
    | Always = 1
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

    member _.RunId = runId
    member _.TenantId = tenantId
    member _.UserId = userId
    member _.Services = services
    member _.CancellationToken = cancellationToken

[<Sealed>]
type ToolResolutionContext
    internal (runId: RunId, tenantId: string voption, userId: string voption, services: IServiceProvider) =
    do
        if isNull services then
            nullArg "services"

    member _.RunId = runId
    member _.TenantId = tenantId
    member _.UserId = userId
    member _.Services = services

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

    member _.Name = name
    member _.Version = version
    member _.Description = description
    member _.Input = input
    member _.Output = output
    member _.Approval = approval
    member _.ApprovalPolicy = approvalPolicy
    member _.InvokeAsync(context: ToolContext, input: 'Input) = invokeAsync.Invoke(context, input)

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
            task {
                match value with
                | :? 'Input as typed ->
                    let! output = definition.InvokeAsync(context, typed)
                    return box output
                | _ ->
                    return
                        raise (
                            InvalidOperationException($"Tool '{definition.Name.Value}' received an invalid input type.")
                        )
            }

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
    member _.Name = name
    member _.Version = version
    member _.Description = description
    member _.Approval = approval
    member _.ApprovalPolicy = approvalPolicy
    member _.Tags = tags
    member _.InputType = executor.InputType
    member _.OutputType = executor.OutputType
    member _.InputSchema = executor.InputSchema
    member _.OutputSchema = executor.OutputSchema

    member internal _.ValidateInput(value: obj) = executor.ValidateInput value
    member internal _.ValidateOutput(value: obj) = executor.ValidateOutput value
    member internal _.InvokeAsync(context: ToolContext, value: obj) = executor.InvokeAsync(context, value)

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

    static member Create(definition: ToolDefinition<'Input, 'Output>) =
        ResolvedTool.Create(definition, Seq.empty)

type IToolResolver =
    abstract ResolveAsync:
        context: ToolResolutionContext * cancellationToken: CancellationToken -> ValueTask<IReadOnlyList<ResolvedTool>>

[<Sealed>]
type StaticToolResolver(tools: IEnumerable<ResolvedTool>) =
    let snapshot =
        if isNull tools then
            nullArg "tools"

        tools |> Seq.toArray

    interface IToolResolver with
        member _.ResolveAsync(_context, _cancellationToken) =
            ValueTask<IReadOnlyList<ResolvedTool>>(snapshot :> IReadOnlyList<ResolvedTool>)

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

    let resolveAllAsync
        (resolvers: IReadOnlyList<IToolResolver>)
        (context: ToolResolutionContext)
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull resolvers then
                nullArg "resolvers"

            if isNull (box context) then
                nullArg "context"

            if resolvers.Count = 0 then
                return emptyTools
            else
                let tools = ResizeArray<ResolvedTool>()
                let identities = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                for resolver in resolvers do
                    if isNull (box resolver) then
                        invalidOp "Tool resolvers cannot contain null entries."

                    let! resolvedTools = resolver.ResolveAsync(context, cancellationToken).AsTask()

                    if isNull resolvedTools then
                        invalidOp "Tool resolvers cannot return null tool lists."

                    for tool in resolvedTools do
                        validateResolvedTool tool

                        let identity = $"{tool.Name.Value}:{tool.Version.Value.Major}"

                        if not (identities.Add identity) then
                            invalidOp
                                $"Duplicate tool identity '{tool.Name.Value}' with major version '{tool.Version.Value.Major}' was resolved."

                        tools.Add tool

                return tools.ToArray() :> IReadOnlyList<ResolvedTool>
        }
