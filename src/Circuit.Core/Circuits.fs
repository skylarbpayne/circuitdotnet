namespace Circuit.Core

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module private CircuitValidation =
    let nonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let positive name value =
        if value < 1 then
            invalidArg name $"{name} must be at least 1."

        value

    let hash (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let typeName (value: Type) =
        if isNull value.AssemblyQualifiedName then
            value.FullName
        else
            value.AssemblyQualifiedName

/// Identifies one stable item lane in a Circuit run.
[<Struct; CustomEquality; NoComparison>]
type ItemKey =
    private
    | ItemKey of string

    /// Gets the stable lane-key text.
    member this.Value = let (ItemKey value) = this in value

    /// <summary>Gets the to string value.</summary>
    override this.ToString() = this.Value

    /// <summary>Gets the equals value.</summary>
    override this.Equals(other) =
        match other with
        | :? ItemKey as candidate -> StringComparer.Ordinal.Equals(this.Value, candidate.Value)
        | _ -> false

    /// <summary>Gets the get hash code value.</summary>
    override this.GetHashCode() =
        StringComparer.Ordinal.GetHashCode(this.Value)

    /// <summary>Gets the create value.</summary>
    static member Create(value: string) =
        ItemKey(CircuitValidation.nonBlank "value" value)

/// Describes whether a Circuit can participate in durable checkpoints.
type CircuitCheckpointability =
    /// The graph is durably checkpointable.
    | Checkpointable = 0
    /// Checkpointability depends on value codecs.
    | CodecDependent = 1
    /// The graph contains a non-durable source.
    | NotCheckpointable = 2

/// Supplies a page from a durable asynchronous Circuit source.
[<Sealed>]
type CircuitSourcePage<'Item>(items: IReadOnlyList<'Item>, continuationToken: string voption, completed: bool) =
    do
        if isNull items then
            nullArg "items"

    /// <summary>Gets the items value.</summary>
    member _.Items = items
    /// <summary>Gets the continuation token value.</summary>
    member _.ContinuationToken = continuationToken
    /// Gets whether this page is the final source page.
    member _.Completed = completed

/// Produces cursor-aware source pages that can be rebuilt during checkpoint resume.
type IResumableCircuitSource<'Input, 'Item> =
    /// Reads the next durable source page after the supplied continuation token.
    abstract ReadAsync:
        input: 'Input * continuationToken: string voption * cancellationToken: CancellationToken ->
            ValueTask<CircuitSourcePage<'Item>>

/// Provides ambient information to trusted code nodes.
[<Sealed>]
type CircuitContext
    internal
    (
        runId: RunId,
        nodePath: string,
        itemKey: ItemKey voption,
        idempotencyKey: string,
        options: RunOptions,
        cancellationToken: CancellationToken
    ) =
    /// <summary>Gets the run id value.</summary>
    member _.RunId = runId
    /// <summary>Gets the node path value.</summary>
    member _.NodePath = nodePath
    /// <summary>Gets the item key value.</summary>
    member _.ItemKey = itemKey
    /// <summary>Gets the idempotency key value.</summary>
    member _.IdempotencyKey = idempotencyKey
    /// <summary>Gets the options value.</summary>
    member _.Options = options
    /// <summary>Gets the cancellation token value.</summary>
    member _.CancellationToken = cancellationToken

/// Represents either a successful lane value or an expected Circuit failure.
type Outcome<'T> =
    /// The evaluation produced a value.
    | Succeeded of 'T
    /// The evaluation produced an expected failure.
    | Failed of CircuitFailure

/// Carries structural and adapter metadata for one node response.
[<Sealed>]
type ResponseMetadata
    internal
    (
        itemKey: ItemKey voption,
        sourceOrdinal: int64 voption,
        sourceOrder: IReadOnlyList<int64>,
        runId: RunId,
        nodePath: string,
        usage: RunUsage,
        session: CircuitSession voption,
        attempt: int,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset,
        idempotencyKey: string
    ) =
    let frozenSourceOrder =
        if isNull sourceOrder then
            nullArg "sourceOrder"

        sourceOrder |> Seq.toArray |> Array.AsReadOnly :> IReadOnlyList<int64>

    /// <summary>Gets the item key value.</summary>
    member _.ItemKey = itemKey
    /// <summary>Gets the source ordinal value.</summary>
    member _.SourceOrdinal = sourceOrdinal
    /// Gets the complete hierarchical source ordinal path from outermost to innermost source.
    member _.SourceOrder = frozenSourceOrder
    /// <summary>Gets the run id value.</summary>
    member _.RunId = runId
    /// <summary>Gets the node path value.</summary>
    member _.NodePath = nodePath
    /// <summary>Gets the usage value.</summary>
    member _.Usage = usage
    /// <summary>Gets the session value.</summary>
    member _.Session = session
    /// <summary>Gets the attempt value.</summary>
    member _.Attempt = attempt
    /// <summary>Gets the started at value.</summary>
    member _.StartedAt = startedAt
    /// <summary>Gets the completed at value.</summary>
    member _.CompletedAt = completedAt
    /// <summary>Gets the idempotency key value.</summary>
    member _.IdempotencyKey = idempotencyKey

/// Captures one node or run projection response.
[<Sealed>]
type Response<'T> internal (outcome: Outcome<'T>, metadata: ResponseMetadata) =
    do
        if isNull (box metadata) then
            nullArg "metadata"

    /// <summary>Gets the outcome value.</summary>
    member _.Outcome = outcome
    /// <summary>Gets the metadata value.</summary>
    member _.Metadata = metadata

    /// Gets whether the response succeeded.
    member _.IsSuccess =
        match outcome with
        | Succeeded _ -> true
        | Failed _ -> false

    /// <summary>Gets the successful response value.</summary>
    member _.Value =
        match outcome with
        | Succeeded value -> value
        | Failed _ -> invalidOp "The response does not contain a value."

    /// <summary>Gets the failure value.</summary>
    member _.Failure =
        match outcome with
        | Failed value -> value
        | Succeeded _ -> invalidOp "The response does not contain a failure."

    static member internal Create(outcome, metadata) = Response<'T>(outcome, metadata)

/// Creates responses for trusted code and aggregate handlers.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Response =
    /// Creates a successful response using the current code-node context.
    let succeed (context: CircuitContext) value =
        if isNull (box context) then
            nullArg "context"

        let now = DateTimeOffset.UtcNow

        let metadata =
            ResponseMetadata(
                context.ItemKey,
                ValueNone,
                Array.empty,
                context.RunId,
                context.NodePath,
                RunUsage(0, 0),
                ValueNone,
                1,
                now,
                now,
                context.IdempotencyKey
            )

        Response<_>.Create(Succeeded value, metadata)

    /// Creates a failed response using the current code-node context.
    let fail (context: CircuitContext) (failure: CircuitFailure) =
        if isNull (box context) then
            nullArg "context"

        if isNull (box failure) then
            nullArg "failure"

        let now = DateTimeOffset.UtcNow

        let metadata =
            ResponseMetadata(
                context.ItemKey,
                ValueNone,
                Array.empty,
                context.RunId,
                context.NodePath,
                RunUsage(0, 0),
                ValueNone,
                1,
                now,
                now,
                context.IdempotencyKey
            )

        Response<_>.Create(Failed failure, metadata)

/// Describes a validation problem in an immutable Circuit graph.
[<Sealed>]
type CircuitValidationIssue(nodeId: string, code: string, message: string) =
    /// <summary>Gets the node id value.</summary>
    member _.NodeId = nodeId
    /// Gets the machine-readable validation code.
    member _.Code = code
    /// Gets the human-readable validation message.
    member _.Message = message

/// Classifies an immutable Circuit graph node without exposing executable handlers or payloads.
type CircuitNodeKind =
    /// An agent provider leaf.
    | Agent = 0
    /// A trusted host-code leaf.
    | Code = 1
    /// An immutable serialized constant.
    | Value = 2
    /// A finite source.
    | Items = 3
    /// A non-durable asynchronous source.
    | AsyncSource = 4
    /// A durable cursor-aware source.
    | ResumableSource = 5
    /// A static pipeline edge.
    | Then = 6
    /// A dynamic child factory.
    | Dynamic = 7
    /// A failure-capturing node.
    | Attempt = 8
    /// A lane recovery node.
    | Recover = 9
    /// A static branch selector.
    | Branch = 10
    /// A bounded branch merge.
    | Merge = 11
    /// A bounded loop.
    | Loop = 12
    /// A host approval pause.
    | Approval = 13
    /// A lane aggregation node.
    | Aggregate = 14
    /// A stable graph naming node.
    | Named = 15

/// Describes the statically inferred number of outputs produced for one admitted input.
type CircuitCardinality =
    /// Exactly one output per admitted input.
    | ExactlyOne = 0
    /// Zero or more outputs per admitted input.
    | Many = 1

/// Provides one immutable, non-executable node view from a Circuit graph.
[<Sealed>]
type CircuitNodeDescriptor
    internal
    (
        path: string,
        id: string,
        kind: CircuitNodeKind,
        version: string voption,
        cardinality: CircuitCardinality,
        checkpointability: CircuitCheckpointability,
        concurrencyLimit: int voption,
        iterationLimit: int voption,
        children: IReadOnlyList<string>
    ) =
    let frozenChildren =
        children |> Seq.toArray |> Array.AsReadOnly :> IReadOnlyList<string>

    /// Gets the stable graph path for this node.
    member _.Path = path
    /// Gets the stable local node identity.
    member _.Id = id
    /// Gets the non-executable node classification.
    member _.Kind = kind
    /// Gets the semantic node version when the node is versioned.
    member _.Version = version
    /// Gets the statically inferred output cardinality.
    member _.Cardinality = cardinality
    /// Gets the checkpointability of this node subtree.
    member _.Checkpointability = checkpointability
    /// Gets the node-local concurrency bound for dynamic or merge nodes.
    member _.ConcurrencyLimit = concurrencyLimit
    /// Gets the node-local iteration bound for loop nodes.
    member _.IterationLimit = iterationLimit
    /// Gets child node paths in deterministic topology order.
    member _.Children = frozenChildren

/// Provides an immutable, non-executable description of a Circuit definition.
[<Sealed>]
type CircuitGraphDescriptor
    internal
    (
        nodes: IReadOnlyList<CircuitNodeDescriptor>,
        cardinality: CircuitCardinality,
        checkpointability: CircuitCheckpointability,
        fingerprint: string,
        validationIssues: IReadOnlyList<CircuitValidationIssue>
    ) =
    let frozenNodes =
        nodes |> Seq.toArray |> Array.AsReadOnly :> IReadOnlyList<CircuitNodeDescriptor>

    let frozenIssues =
        validationIssues |> Seq.toArray |> Array.AsReadOnly :> IReadOnlyList<CircuitValidationIssue>

    /// Gets all nodes in deterministic pre-order topology traversal.
    member _.Nodes = frozenNodes
    /// Gets the root definition's inferred output cardinality.
    member _.Cardinality = cardinality
    /// Gets the root definition's checkpointability.
    member _.Checkpointability = checkpointability
    /// Gets the same behavior fingerprint enforced by checkpoint resume.
    member _.Fingerprint = fingerprint
    /// Gets static validation findings without executing handlers or reading payloads.
    member _.ValidationIssues = frozenIssues
    /// Gets whether static graph validation found no issues.
    member _.IsValid = frozenIssues.Count = 0

module internal CircuitGraph =
    type ICircuitDefinition =
        abstract NodeObject: obj
        abstract DefinitionFingerprint: string

    type ICodeHandler =
        abstract InputType: Type
        abstract OutputType: Type
        abstract InvokeAsync: CircuitContext * obj -> Task<obj>

    type CodeHandler<'Input, 'Output>(handler: CircuitContext -> 'Input -> Task<Response<'Output>>) =
        interface ICodeHandler with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>

            member _.InvokeAsync(context, value) =
                task {
                    let! response = handler context (unbox<'Input> value)
                    return box response
                }

    type IAgentHandler =
        abstract InputType: Type
        abstract OutputType: Type
        abstract Agent: AgentDefinition
        abstract SignatureId: DefinitionId
        abstract SignatureVersion: SemanticVersion
        abstract Signature: obj

    type AgentHandler<'Input, 'Output>(agent: AgentDefinition, signature: Signature<'Input, 'Output>) =
        interface IAgentHandler with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>
            member _.Agent = agent
            member _.SignatureId = signature.Id
            member _.SignatureVersion = signature.Version
            member _.Signature = box signature

    type IItemsHandler =
        abstract InputType: Type
        abstract ItemType: Type
        abstract Invoke: obj -> obj array
        abstract Key: obj * int64 -> string

    type ItemsHandler<'Input, 'Item>(items: 'Input -> IReadOnlyList<'Item>, key: 'Item -> string voption) =
        interface IItemsHandler with
            member _.InputType = typeof<'Input>
            member _.ItemType = typeof<'Item>

            member _.Invoke(value) =
                items (unbox<'Input> value) |> Seq.map box |> Seq.toArray

            member _.Key(value, ordinal) =
                match key (unbox<'Item> value) with
                | ValueSome stable -> CircuitValidation.nonBlank "key" stable
                | ValueNone -> string ordinal

    type IAsyncSourceHandler =
        abstract InputType: Type
        abstract ItemType: Type
        abstract Invoke: obj -> obj

    type AsyncSourceHandler<'Input, 'Item>(source: 'Input -> IAsyncEnumerable<'Item>) =
        interface IAsyncSourceHandler with
            member _.InputType = typeof<'Input>
            member _.ItemType = typeof<'Item>
            member _.Invoke(value) = box (source (unbox<'Input> value))

    type IResumableSourceHandler =
        abstract InputType: Type
        abstract ItemType: Type
        abstract ReadAsync: obj * string voption * CancellationToken -> Task<obj array * string voption * bool>

    type ResumableSourceHandler<'Input, 'Item>(source: IResumableCircuitSource<'Input, 'Item>) =
        interface IResumableSourceHandler with
            member _.InputType = typeof<'Input>
            member _.ItemType = typeof<'Item>

            member _.ReadAsync(value, cursor, cancellationToken) =
                task {
                    let! page = source.ReadAsync(unbox<'Input> value, cursor, cancellationToken).AsTask()
                    return page.Items |> Seq.map box |> Seq.toArray, page.ContinuationToken, page.Completed
                }

    type IDynamicHandler =
        abstract InputType: Type
        abstract OutputType: Type
        abstract Key: obj -> string
        abstract Build: obj -> Node * string

    and DynamicHandler<'Input, 'Output>(key: 'Input -> string, factory: 'Input -> obj) =
        interface IDynamicHandler with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>

            member _.Key(value) =
                CircuitValidation.nonBlank "key" (key (unbox<'Input> value))

            member _.Build(value) =
                let child = factory (unbox<'Input> value)

                if isNull child then
                    invalidOp "A dynamic Circuit factory returned null."

                let definition = child :?> ICircuitDefinition
                definition.NodeObject :?> Node, definition.DefinitionFingerprint

    and IRecoverHandler =
        abstract OutputType: Type
        abstract Invoke: CircuitFailure -> obj

    and RecoverHandler<'Output>(handler: CircuitFailure -> 'Output) =
        interface IRecoverHandler with
            member _.OutputType = typeof<'Output>
            member _.Invoke(failure) = box (handler failure)

    and IBranchHandler =
        abstract InputType: Type
        abstract OutputType: Type
        abstract Select: obj -> string
        abstract Cases: IReadOnlyDictionary<string, Node>
        abstract Default: Node voption

    and BranchHandler<'Input, 'Output>
        (selector: 'Input -> string, cases: IReadOnlyDictionary<string, Node>, defaultCase: Node voption) =
        interface IBranchHandler with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>
            member _.Select(value) = selector (unbox<'Input> value)
            member _.Cases = cases
            member _.Default = defaultCase

    and ILoopHandler =
        abstract ValueType: Type
        abstract Continue: obj -> bool
        abstract Body: Node

    and LoopHandler<'Value>(predicate: 'Value -> bool, body: Node) =
        interface ILoopHandler with
            member _.ValueType = typeof<'Value>
            member _.Continue(value) = predicate (unbox<'Value> value)
            member _.Body = body

    and IApprovalPromptHandler =
        abstract InputType: Type
        abstract Invoke: obj -> ApprovalPrompt

    and ApprovalPromptHandler<'Input>(handler: 'Input -> ApprovalPrompt) =
        interface IApprovalPromptHandler with
            member _.InputType = typeof<'Input>
            member _.Invoke(value) = handler (unbox<'Input> value)

    and IAggregateHandler =
        abstract ItemType: Type
        abstract OutputType: Type
        abstract InvokeAsync: CircuitContext * obj array * CancellationToken -> Task<obj>

    and AggregateHandler<'Item, 'Output>
        (handler: CircuitContext -> IReadOnlyList<Response<'Item>> -> CancellationToken -> Task<Response<'Output>>) =
        interface IAggregateHandler with
            member _.ItemType = typeof<'Item>
            member _.OutputType = typeof<'Output>

            member _.InvokeAsync(context, values, cancellationToken) =
                task {
                    let typed = values |> Array.map unbox<Response<'Item>> |> Array.AsReadOnly
                    let! response = handler context typed cancellationToken
                    return box response
                }

    and Node =
        | Agent of id: string * version: string * IAgentHandler
        | Code of id: string * version: string * ICodeHandler
        | Value of id: string * outputType: Type * serializedValue: string
        | Items of id: string * version: string * IItemsHandler
        | AsyncSource of id: string * version: string * IAsyncSourceHandler
        | ResumableSource of id: string * version: string * IResumableSourceHandler
        | Then of Node * Node
        | Dynamic of id: string * version: string * maxConcurrency: int * IDynamicHandler * Node
        | Attempt of id: string * Node
        | Recover of id: string * version: string * IRecoverHandler * Node
        | Branch of id: string * version: string * IBranchHandler
        | Merge of id: string * version: string * maxConcurrency: int * Node array
        | Loop of id: string * version: string * maxIterations: int * ILoopHandler
        | Approval of id: string * version: string * IApprovalPromptHandler
        | Aggregate of id: string * version: string * IAggregateHandler * Node
        | Named of id: string * Node

    and [<Sealed>] Circuit<'Input, 'Output>
        internal (id: DefinitionId, version: SemanticVersion, node: Node, checkpointability: CircuitCheckpointability) =
        let fingerprint =
            let rec describe node =
                match node with
                | Agent(nodeId, nodeVersion, handler) ->
                    $"agent|{nodeId}|{nodeVersion}|{handler.Agent.Id}|{handler.Agent.Version}|{handler.SignatureId}|{handler.SignatureVersion}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.OutputType}"
                | Code(nodeId, nodeVersion, handler) ->
                    $"code|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.OutputType}"
                | Value(nodeId, outputType, serializedValue) ->
                    $"value|{nodeId}|{CircuitValidation.typeName outputType}|{serializedValue}"
                | Items(nodeId, nodeVersion, handler) ->
                    $"items|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.ItemType}"
                | AsyncSource(nodeId, nodeVersion, handler) ->
                    $"async-source|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.ItemType}"
                | ResumableSource(nodeId, nodeVersion, handler) ->
                    $"source|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.ItemType}"
                | Then(left, right) -> $"then({describe left})({describe right})"
                | Dynamic(nodeId, nodeVersion, maximum, handler, previous) ->
                    $"dynamic|{nodeId}|{nodeVersion}|{maximum}|{CircuitValidation.typeName handler.InputType}|{CircuitValidation.typeName handler.OutputType}|({describe previous})"
                | Attempt(nodeId, previous) -> $"attempt|{nodeId}|({describe previous})"
                | Recover(nodeId, nodeVersion, handler, previous) ->
                    $"recover|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.OutputType}|({describe previous})"
                | Branch(nodeId, nodeVersion, handler) ->
                    let cases =
                        handler.Cases
                        |> Seq.sortBy _.Key
                        |> Seq.map (fun item -> $"{item.Key}=({describe item.Value})")
                        |> String.concat ";"

                    let fallback =
                        match handler.Default with
                        | ValueSome value -> describe value
                        | ValueNone -> "none"

                    $"branch|{nodeId}|{nodeVersion}|{cases}|default={fallback}"
                | Merge(nodeId, nodeVersion, maximum, branches) ->
                    let branchText = branches |> Array.map describe |> String.concat ";"
                    $"merge|{nodeId}|{nodeVersion}|{maximum}|{branchText}"
                | Loop(nodeId, nodeVersion, maximum, handler) ->
                    $"loop|{nodeId}|{nodeVersion}|{maximum}|({describe handler.Body})"
                | Approval(nodeId, nodeVersion, handler) ->
                    $"approval|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.InputType}"
                | Aggregate(nodeId, nodeVersion, handler, previous) ->
                    $"aggregate|{nodeId}|{nodeVersion}|{CircuitValidation.typeName handler.ItemType}|{CircuitValidation.typeName handler.OutputType}|({describe previous})"
                | Named(nodeId, child) -> $"named|{nodeId}|({describe child})"

            CircuitValidation.hash
                $"circuit|{id}|{version}|{CircuitValidation.typeName typeof<'Input>}|{CircuitValidation.typeName typeof<'Output>}|{describe node}"

        let graph =
            let combine left right =
                if
                    left = CircuitCheckpointability.NotCheckpointable
                    || right = CircuitCheckpointability.NotCheckpointable
                then
                    CircuitCheckpointability.NotCheckpointable
                elif
                    left = CircuitCheckpointability.CodecDependent
                    || right = CircuitCheckpointability.CodecDependent
                then
                    CircuitCheckpointability.CodecDependent
                else
                    CircuitCheckpointability.Checkpointable

            let rec analyze current =
                match current with
                | Agent _
                | Code _
                | Value _
                | Approval _ -> CircuitCardinality.ExactlyOne, CircuitCheckpointability.CodecDependent
                | Items _
                | ResumableSource _ -> CircuitCardinality.Many, CircuitCheckpointability.CodecDependent
                | AsyncSource _ -> CircuitCardinality.Many, CircuitCheckpointability.NotCheckpointable
                | Then(left, right) ->
                    let leftCardinality, leftCheckpointability = analyze left
                    let rightCardinality, rightCheckpointability = analyze right

                    let cardinality =
                        if
                            leftCardinality = CircuitCardinality.Many
                            || rightCardinality = CircuitCardinality.Many
                        then
                            CircuitCardinality.Many
                        else
                            CircuitCardinality.ExactlyOne

                    cardinality, combine leftCheckpointability rightCheckpointability
                | Dynamic(_, _, _, _, previous) ->
                    let _, previousCheckpointability = analyze previous
                    CircuitCardinality.Many, combine previousCheckpointability CircuitCheckpointability.CodecDependent
                | Attempt(_, previous)
                | Recover(_, _, _, previous)
                | Named(_, previous) -> analyze previous
                | Branch(_, _, handler) ->
                    let children =
                        seq {
                            yield! handler.Cases.Values

                            match handler.Default with
                            | ValueSome child -> yield child
                            | ValueNone -> ()
                        }
                        |> Seq.map analyze
                        |> Seq.toArray

                    let cardinality =
                        if children |> Array.exists (fun (value, _) -> value = CircuitCardinality.Many) then
                            CircuitCardinality.Many
                        else
                            CircuitCardinality.ExactlyOne

                    let checkpoint =
                        children
                        |> Array.fold
                            (fun state (_, value) -> combine state value)
                            CircuitCheckpointability.Checkpointable

                    cardinality, checkpoint
                | Merge(_, _, _, branches) ->
                    let children = branches |> Array.map analyze

                    let cardinality =
                        if
                            branches.Length > 1
                            || children |> Array.exists (fun (value, _) -> value = CircuitCardinality.Many)
                        then
                            CircuitCardinality.Many
                        else
                            CircuitCardinality.ExactlyOne

                    let checkpoint =
                        children
                        |> Array.fold
                            (fun state (_, value) -> combine state value)
                            CircuitCheckpointability.Checkpointable

                    cardinality, checkpoint
                | Loop(_, _, _, handler) ->
                    let _, childCheckpointability = analyze handler.Body
                    CircuitCardinality.ExactlyOne, childCheckpointability
                | Aggregate(_, _, _, previous) ->
                    let _, previousCheckpointability = analyze previous
                    CircuitCardinality.ExactlyOne, previousCheckpointability

            let nodes = ResizeArray<CircuitNodeDescriptor>()
            let issues = ResizeArray<CircuitValidationIssue>()
            let paths = HashSet<string>(StringComparer.Ordinal)

            let rec visit path current =
                let nodeId, kind, nodeVersion, concurrency, iterations, children =
                    match current with
                    | Agent(nodeId, _nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.Agent, ValueNone, ValueNone, ValueNone, []
                    | Code(nodeId, nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.Code, ValueSome nodeVersion, ValueNone, ValueNone, []
                    | Value(nodeId, _, _) -> nodeId, CircuitNodeKind.Value, ValueNone, ValueNone, ValueNone, []
                    | Items(nodeId, nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.Items, ValueSome nodeVersion, ValueNone, ValueNone, []
                    | AsyncSource(nodeId, nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.AsyncSource, ValueSome nodeVersion, ValueNone, ValueNone, []
                    | ResumableSource(nodeId, nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.ResumableSource, ValueSome nodeVersion, ValueNone, ValueNone, []
                    | Then(left, right) ->
                        "then",
                        CircuitNodeKind.Then,
                        ValueNone,
                        ValueNone,
                        ValueNone,
                        [ "previous", left; "next", right ]
                    | Dynamic(nodeId, nodeVersion, maximum, _handler, previous) ->
                        nodeId,
                        CircuitNodeKind.Dynamic,
                        ValueSome nodeVersion,
                        ValueSome maximum,
                        ValueNone,
                        [ "previous", previous ]
                    | Attempt(nodeId, previous) ->
                        nodeId, CircuitNodeKind.Attempt, ValueNone, ValueNone, ValueNone, [ "previous", previous ]
                    | Recover(nodeId, nodeVersion, _handler, previous) ->
                        nodeId,
                        CircuitNodeKind.Recover,
                        ValueSome nodeVersion,
                        ValueNone,
                        ValueNone,
                        [ "previous", previous ]
                    | Branch(nodeId, nodeVersion, handler) ->
                        let branchChildren =
                            [ yield!
                                  handler.Cases
                                  |> Seq.sortBy _.Key
                                  |> Seq.map (fun item -> $"case-{item.Key}", item.Value)
                              match handler.Default with
                              | ValueSome child -> yield "default", child
                              | ValueNone -> () ]

                        nodeId, CircuitNodeKind.Branch, ValueSome nodeVersion, ValueNone, ValueNone, branchChildren
                    | Merge(nodeId, nodeVersion, maximum, branches) ->
                        nodeId,
                        CircuitNodeKind.Merge,
                        ValueSome nodeVersion,
                        ValueSome maximum,
                        ValueNone,
                        (branches
                         |> Array.mapi (fun index child -> $"branch-{index}", child)
                         |> Array.toList)
                    | Loop(nodeId, nodeVersion, maximum, handler) ->
                        nodeId,
                        CircuitNodeKind.Loop,
                        ValueSome nodeVersion,
                        ValueNone,
                        ValueSome maximum,
                        [ "body", handler.Body ]
                    | Approval(nodeId, nodeVersion, _handler) ->
                        nodeId, CircuitNodeKind.Approval, ValueSome nodeVersion, ValueNone, ValueNone, []
                    | Aggregate(nodeId, nodeVersion, _handler, previous) ->
                        nodeId,
                        CircuitNodeKind.Aggregate,
                        ValueSome nodeVersion,
                        ValueNone,
                        ValueNone,
                        [ "previous", previous ]
                    | Named(nodeId, child) ->
                        nodeId, CircuitNodeKind.Named, ValueNone, ValueNone, ValueNone, [ "child", child ]

                if not (paths.Add path) then
                    issues.Add(CircuitValidationIssue(nodeId, "duplicate-node", $"Duplicate graph path '{path}'."))

                match concurrency with
                | ValueSome maximum when maximum < 1 ->
                    issues.Add(CircuitValidationIssue(nodeId, "concurrency", "Concurrency limits must be at least 1."))
                | _ -> ()

                match iterations with
                | ValueSome maximum when maximum < 1 ->
                    issues.Add(CircuitValidationIssue(nodeId, "iterations", "Iteration limits must be at least 1."))
                | _ -> ()

                let childPaths = children |> List.map (fun (segment, _) -> $"{path}/{segment}")
                let cardinality, nodeCheckpointability = analyze current

                nodes.Add(
                    CircuitNodeDescriptor(
                        path,
                        nodeId,
                        kind,
                        nodeVersion,
                        cardinality,
                        nodeCheckpointability,
                        concurrency,
                        iterations,
                        childPaths |> List.toArray
                    )
                )

                for (segment, child) in children do
                    visit $"{path}/{segment}" child

            visit id.Value node
            let cardinality, _ = analyze node
            CircuitGraphDescriptor(nodes.ToArray(), cardinality, checkpointability, fingerprint, issues.ToArray())

        member _.Id = id
        member _.Version = version
        member _.Checkpointability = checkpointability
        member internal _.Node = node
        member _.InputType = typeof<'Input>
        member _.OutputType = typeof<'Output>
        member _.Fingerprint = fingerprint
        member _.Graph = graph

        interface ICircuitDefinition with
            member _.NodeObject = box node
            member _.DefinitionFingerprint = fingerprint

open CircuitGraph

/// An immutable, inspectable, graph-backed executable Circuit definition.
[<Sealed>]
type Circuit<'Input, 'Output>
    internal
    (id: DefinitionId, version: SemanticVersion, node: CircuitGraph.Node, checkpointability: CircuitCheckpointability) =
    let inner =
        CircuitGraph.Circuit<'Input, 'Output>(id, version, node, checkpointability)

    /// <summary>Gets the id value.</summary>
    member _.Id = inner.Id
    /// <summary>Gets the version value.</summary>
    member _.Version = inner.Version
    /// <summary>Gets the checkpointability value.</summary>
    member _.Checkpointability = inner.Checkpointability
    /// <summary>Gets the input type value.</summary>
    member _.InputType = inner.InputType
    /// <summary>Gets the output type value.</summary>
    member _.OutputType = inner.OutputType
    /// Gets the stable behavior fingerprint enforced by checkpoint resume.
    member _.Fingerprint = inner.Fingerprint
    /// Gets an immutable, non-executable graph topology and resource description.
    member _.Graph = inner.Graph
    /// <summary>Gets the internal value.</summary>
    member internal _.Node = node

    interface CircuitGraph.ICircuitDefinition with
        member _.NodeObject = box node
        member _.DefinitionFingerprint = inner.Fingerprint

/// Builds and validates immutable Circuit graphs.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal CircuitDefinition =
    let private definition<'Input, 'Output> id version node checkpointability : Circuit<'Input, 'Output> =
        Circuit<'Input, 'Output>(
            DefinitionId.Create(CircuitValidation.nonBlank "id" id),
            SemanticVersion.Parse(CircuitValidation.nonBlank "version" version),
            node,
            checkpointability
        )

    let private validateNodeIdentity id version =
        DefinitionId.Create(CircuitValidation.nonBlank "id" id) |> ignore
        SemanticVersion.Parse(CircuitValidation.nonBlank "version" version) |> ignore

    let private validateLocalIdentity id =
        DefinitionId.Create(CircuitValidation.nonBlank "id" id) |> ignore

    let private combineCheckpointability left right =
        if
            left = CircuitCheckpointability.NotCheckpointable
            || right = CircuitCheckpointability.NotCheckpointable
        then
            CircuitCheckpointability.NotCheckpointable
        elif
            left = CircuitCheckpointability.CodecDependent
            || right = CircuitCheckpointability.CodecDependent
        then
            CircuitCheckpointability.CodecDependent
        else
            CircuitCheckpointability.Checkpointable

    /// Assigns an explicit root definition identity and semantic version.
    let define id version (circuit: Circuit<'Input, 'Output>) =
        if isNull (box circuit) then
            nullArg "circuit"

        definition<'Input, 'Output> id version circuit.Node circuit.Checkpointability

    /// Creates a one-node agent Circuit.
    let agent (agent: AgentDefinition) (signature: Signature<'Input, 'Output>) =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        let id = $"{agent.Id.Value}.{signature.Id.Value}"
        let version = $"{agent.Version}-{signature.Version}"
        let handler = AgentHandler<'Input, 'Output>(agent, signature) :> IAgentHandler
        definition<'Input, 'Output> id "1.0.0" (Agent(id, version, handler)) CircuitCheckpointability.CodecDependent

    /// Creates a trusted host-code leaf with durable identity.
    let code id version (handler: CircuitContext -> 'Input -> Task<Response<'Output>>) =
        CircuitValidation.nonBlank "id" id |> ignore
        CircuitValidation.nonBlank "version" version |> ignore

        if isNull (box handler) then
            nullArg "handler"

        definition<'Input, 'Output>
            id
            version
            (Code(id, version, CodeHandler<'Input, 'Output>(handler)))
            CircuitCheckpointability.CodecDependent

    /// Creates a constant one-output Circuit.
    let value (value: 'Output) : Circuit<'Input, 'Output> =
        let json = JsonSerializer.Serialize(box value, typeof<'Output>)
        // Keep only immutable serialized bytes in the graph. Every uncached evaluation
        // materializes its own object so escaped response mutation cannot affect another run.
        JsonSerializer.Deserialize(json, typeof<'Output>) |> ignore
        let token = CircuitValidation.hash json
        let id = "value-" + token.Substring(0, 16)

        definition<'Input, 'Output>
            id
            "1.0.0"
            (Value(id, typeof<'Output>, json))
            CircuitCheckpointability.CodecDependent

    /// Creates a finite source whose stable keys default to invariant source ordinals.
    let items id version (items: 'Input -> IReadOnlyList<'Item>) =
        if isNull (box items) then
            nullArg "items"

        let handler =
            ItemsHandler<'Input, 'Item>(items, fun _ -> ValueNone) :> IItemsHandler

        definition<'Input, 'Item> id version (Items(id, version, handler)) CircuitCheckpointability.CodecDependent

    /// Creates a finite source with explicit stable item keys.
    let keyedItems id version (key: 'Item -> string) (items: 'Input -> IReadOnlyList<'Item>) =
        if isNull (box key) then
            nullArg "key"

        if isNull (box items) then
            nullArg "items"

        let handler = ItemsHandler<'Input, 'Item>(items, key >> ValueSome) :> IItemsHandler
        definition<'Input, 'Item> id version (Items(id, version, handler)) CircuitCheckpointability.CodecDependent

    /// Creates a cursor-aware durable source.
    let source id version (source: IResumableCircuitSource<'Input, 'Item>) =
        if isNull (box source) then
            nullArg "source"

        let handler =
            ResumableSourceHandler<'Input, 'Item>(source) :> IResumableSourceHandler

        definition<'Input, 'Item>
            id
            version
            (ResumableSource(id, version, handler))
            CircuitCheckpointability.CodecDependent

    /// Creates a non-durable asynchronous source.
    let asyncSource id version (source: 'Input -> IAsyncEnumerable<'Item>) =
        if isNull (box source) then
            nullArg "source"

        let handler = AsyncSourceHandler<'Input, 'Item>(source) :> IAsyncSourceHandler

        definition<'Input, 'Item>
            id
            version
            (AsyncSource(id, version, handler))
            CircuitCheckpointability.NotCheckpointable

    /// Routes every successful upstream response immediately into the next Circuit.
    let thenStep (next: Circuit<'Middle, 'Output>) (previous: Circuit<'Input, 'Middle>) =
        if isNull (box next) then
            nullArg "next"

        if isNull (box previous) then
            nullArg "previous"

        Circuit<'Input, 'Output>(
            previous.Id,
            previous.Version,
            Then(previous.Node, next.Node),
            combineCheckpointability previous.Checkpointability next.Checkpointability
        )

    /// Materializes a deterministic child Circuit for each admitted item.
    let thenDynamic
        id
        version
        (key: 'Middle -> string)
        maxConcurrency
        (factory: 'Middle -> Circuit<'Middle, 'Output>)
        (previous: Circuit<'Input, 'Middle>)
        =
        validateNodeIdentity id version
        CircuitValidation.positive "maxConcurrency" maxConcurrency |> ignore

        if isNull (box key) then
            nullArg "key"

        if isNull (box factory) then
            nullArg "factory"

        if isNull (box previous) then
            nullArg "previous"

        let handler =
            DynamicHandler<'Middle, 'Output>(key, factory >> box) :> IDynamicHandler

        Circuit<'Input, 'Output>(
            previous.Id,
            previous.Version,
            Dynamic(id, version, maxConcurrency, handler, previous.Node),
            CircuitCheckpointability.CodecDependent
        )

    /// Turns each lane response into a successful value available to ordinary continuation.
    let attempt (previous: Circuit<'Input, 'Output>) =
        if isNull (box previous) then
            nullArg "previous"

        Circuit<'Input, Response<'Output>>(
            previous.Id,
            previous.Version,
            Attempt("attempt", previous.Node),
            previous.Checkpointability
        )

    /// Replaces failed lane outputs with values using a versioned trusted handler.
    let recover id version (handler: CircuitFailure -> 'Output) (previous: Circuit<'Input, 'Output>) =
        validateNodeIdentity id version

        if isNull (box handler) then
            nullArg "handler"

        if isNull (box previous) then
            nullArg "previous"

        let recovery = RecoverHandler<'Output>(handler) :> IRecoverHandler

        Circuit<'Input, 'Output>(
            previous.Id,
            previous.Version,
            Recover(id, version, recovery, previous.Node),
            previous.Checkpointability
        )

    /// Selects one statically inspectable branch.
    let branch
        id
        version
        (selector: 'Input -> string)
        (cases: IReadOnlyDictionary<string, Circuit<'Input, 'Output>>)
        (defaultCase: Circuit<'Input, 'Output> voption)
        =
        if isNull (box selector) then
            nullArg "selector"

        if isNull cases then
            nullArg "cases"

        let nodeCases = Dictionary<string, Node>(StringComparer.Ordinal)

        for item in cases do
            nodeCases.Add(item.Key, item.Value.Node)

        let handler =
            BranchHandler<'Input, 'Output>(
                selector,
                ReadOnlyDictionary(nodeCases),
                defaultCase |> ValueOption.map _.Node
            )
            :> IBranchHandler

        let checkpointability =
            Seq.append
                (cases.Values |> Seq.map _.Checkpointability)
                (defaultCase |> ValueOption.toList |> Seq.map _.Checkpointability)
            |> Seq.fold combineCheckpointability CircuitCheckpointability.Checkpointable

        definition<'Input, 'Output> id version (Branch(id, version, handler)) checkpointability

    /// Executes several branch Circuits with bounded concurrency and emits each branch response.
    let merge id version maxConcurrency (branches: IReadOnlyList<Circuit<'Input, 'Output>>) =
        CircuitValidation.positive "maxConcurrency" maxConcurrency |> ignore

        if isNull branches then
            nullArg "branches"

        if branches.Count = 0 then
            invalidArg "branches" "At least one branch is required."

        let checkpointability =
            branches
            |> Seq.map _.Checkpointability
            |> Seq.fold combineCheckpointability CircuitCheckpointability.Checkpointable

        definition<'Input, 'Output>
            id
            version
            (Merge(id, version, maxConcurrency, branches |> Seq.map _.Node |> Seq.toArray))
            checkpointability

    /// Repeats a body Circuit while a versioned predicate is true, up to a hard bound.
    let loop id version maxIterations (whileTrue: 'Value -> bool) (body: Circuit<'Value, 'Value>) =
        CircuitValidation.positive "maxIterations" maxIterations |> ignore

        if isNull (box whileTrue) then
            nullArg "whileTrue"

        if isNull (box body) then
            nullArg "body"

        let handler = LoopHandler<'Value>(whileTrue, body.Node) :> ILoopHandler
        definition<'Value, 'Value> id version (Loop(id, version, maxIterations, handler)) body.Checkpointability

    /// Pauses the affected lane until a matching single-use approval response arrives.
    let approval id version (prompt: 'Input -> ApprovalPrompt) =
        if isNull (box prompt) then
            nullArg "prompt"

        let handler = ApprovalPromptHandler<'Input>(prompt) :> IApprovalPromptHandler

        definition<'Input, ApprovalResponse>
            id
            version
            (Approval(id, version, handler))
            CircuitCheckpointability.CodecDependent

    /// Aggregates all predecessor lane responses into one versioned response.
    let aggregate
        id
        version
        (handler: CircuitContext -> IReadOnlyList<Response<'Item>> -> CancellationToken -> Task<Response<'Output>>)
        (previous: Circuit<'Input, 'Item>)
        =
        validateNodeIdentity id version

        if isNull (box handler) then
            nullArg "handler"

        if isNull (box previous) then
            nullArg "previous"

        let aggregate = AggregateHandler<'Item, 'Output>(handler) :> IAggregateHandler

        Circuit<'Input, 'Output>(
            previous.Id,
            previous.Version,
            Aggregate(id, version, aggregate, previous.Node),
            previous.Checkpointability
        )

    /// Supplies a distinct local node identity when a graph reuses the same leaf.
    let named id (circuit: Circuit<'Input, 'Output>) =
        validateLocalIdentity id

        if isNull (box circuit) then
            nullArg "circuit"

        Circuit<'Input, 'Output>(circuit.Id, circuit.Version, Named(id, circuit.Node), circuit.Checkpointability)

    /// Validates static graph shape and resource bounds without executing trusted handlers.
    let validate (circuit: Circuit<'Input, 'Output>) =
        if isNull (box circuit) then
            nullArg "circuit"

        circuit.Graph.ValidationIssues
