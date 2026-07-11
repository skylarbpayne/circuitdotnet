namespace Circuit.Core

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

module private WorkflowValidation =
    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let freezeMetadata (metadata: seq<KeyValuePair<string, string>>) =
        if isNull (box metadata) then
            nullArg "metadata"

        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for entry in metadata do
            if String.IsNullOrWhiteSpace entry.Key then
                invalidArg "metadata" "Metadata keys cannot be blank."

            if isNull entry.Value then
                nullArg "metadata"

            if dictionary.ContainsKey entry.Key then
                invalidArg "metadata" "Duplicate metadata keys are not allowed."

            dictionary[entry.Key] <- entry.Value

        ReadOnlyDictionary(dictionary) :> IReadOnlyDictionary<string, string>

    let typeId (valueType: Type) =
        if isNull valueType then
            "<null>"
        else
            valueType.AssemblyQualifiedName

    let stableIdSuffix (value: string) =
        Convert.ToHexString(Encoding.UTF8.GetBytes value).ToLowerInvariant()

    let duplicateKeys (values: string list) =
        let seen = HashSet<string>(StringComparer.Ordinal)
        let duplicates = ResizeArray<string>()

        for value in values do
            if not (isNull value) && not (seen.Add value) && not (duplicates.Contains value) then
                duplicates.Add value

        duplicates |> Seq.toList

[<AllowNullLiteral; Sealed>]
type ApprovalPrompt(title: string, message: string, metadata: IEnumerable<KeyValuePair<string, string>>) =
    let frozenMetadata = WorkflowValidation.freezeMetadata metadata

    do
        WorkflowValidation.requireNonBlank "title" title |> ignore

        if isNull message then
            nullArg "message"

    member _.Title = title
    member _.Message = message
    member _.Metadata = frozenMetadata

    static member Create(title: string, message: string) =
        ApprovalPrompt(title, message, Seq.empty)

[<AllowNullLiteral; Sealed>]
type ApprovalResponse(requestId: string, approved: bool, note: string) =
    do
        WorkflowValidation.requireNonBlank "requestId" requestId |> ignore

        if not (isNull note) && String.IsNullOrWhiteSpace note then
            invalidArg "note" "note cannot be blank when provided."

    member _.RequestId = requestId
    member _.Approved = approved
    member _.Note = note

    static member Create(requestId: string, approved: bool) =
        ApprovalResponse(requestId, approved, null)

[<Sealed>]
type WorkflowContext
    internal
    (
        runId: RunId,
        definitionId: DefinitionId,
        definitionVersion: SemanticVersion,
        stepId: string,
        cancellationToken: CancellationToken
    ) =
    member _.RunId = runId
    member _.DefinitionId = definitionId
    member _.DefinitionVersion = definitionVersion
    member _.StepId = stepId
    member _.CancellationToken = cancellationToken

[<Sealed>]
type WorkflowRunOptions internal (sessionId: string voption) =
    member _.SessionId = sessionId

    static member Default = WorkflowRunOptions(ValueNone)

[<Sealed>]
type WorkflowValidationIssue(nodeId: string, code: string, message: string) =
    do
        WorkflowValidation.requireNonBlank "code" code |> ignore
        WorkflowValidation.requireNonBlank "message" message |> ignore

    member _.NodeId = nodeId
    member _.Code = code
    member _.Message = message

module internal WorkflowGraph =
    type ICodeHandler =
        abstract InputType: Type
        abstract OutputType: Type
        abstract InvokeAsync: WorkflowContext * obj * CancellationToken -> Task<obj>

    type CodeHandler<'Input, 'Output>(handler: WorkflowContext -> 'Input -> Task<'Output>) =
        interface ICodeHandler with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>

            member _.InvokeAsync(context, input, _cancellationToken) =
                task {
                    let! output = handler context (unbox<'Input> input)
                    return box output
                }

    type ISelectorHandler =
        abstract InputType: Type
        abstract Invoke: obj -> string

    type SelectorHandler<'Input>(selector: 'Input -> string) =
        interface ISelectorHandler with
            member _.InputType = typeof<'Input>
            member _.Invoke(input) = selector (unbox<'Input> input)

    type IAggregateHandler =
        abstract ItemType: Type
        abstract OutputType: Type
        abstract InvokeAsync: obj list * CancellationToken -> Task<obj>

    type AggregateHandler<'Item, 'Output>(aggregate: 'Item list -> Task<'Output>) =
        interface IAggregateHandler with
            member _.ItemType = typeof<'Item>
            member _.OutputType = typeof<'Output>

            member _.InvokeAsync(items, _cancellationToken) =
                task {
                    let typedItems = items |> List.map unbox<'Item>
                    let! output = aggregate typedItems
                    return box output
                }

    type IPromptHandler =
        abstract InputType: Type
        abstract Invoke: obj -> ApprovalPrompt

    type PromptHandler<'Input>(prompt: 'Input -> ApprovalPrompt) =
        interface IPromptHandler with
            member _.InputType = typeof<'Input>
            member _.Invoke(input) = prompt (unbox<'Input> input)

    type ILoopConditionHandler =
        abstract InputType: Type
        abstract Invoke: obj -> bool

    type LoopConditionHandler<'Input>(predicate: 'Input -> bool) =
        interface ILoopConditionHandler with
            member _.InputType = typeof<'Input>
            member _.Invoke(input) = predicate (unbox<'Input> input)

    type IErasedSignature =
        abstract InputType: Type
        abstract OutputType: Type
        abstract SignatureId: DefinitionId
        abstract SignatureVersion: SemanticVersion
        abstract Value: obj

    type ErasedSignature<'Input, 'Output>(signature: Signature<'Input, 'Output>) =
        interface IErasedSignature with
            member _.InputType = typeof<'Input>
            member _.OutputType = typeof<'Output>
            member _.SignatureId = signature.Id
            member _.SignatureVersion = signature.Version
            member _.Value = box signature

    [<Struct>]
    type Edge = { SourceId: string; TargetId: string }

    type BranchSelection<'T> = { Key: string; Value: 'T }

    type ParallelBranchResult<'T> = { BranchIndex: int; Value: 'T }

    type ParallelAggregateDispatch<'T> = { IsComplete: bool; Value: 'T }

    type LoopDecision<'T> = { Continue: bool; Value: 'T }

    type NodeKind =
        | Code of ICodeHandler
        | Agent of AgentDefinition * IErasedSignature
        | ChoiceSelector of selector: ISelectorHandler * cases: string list * hasDefault: bool
        | ChoiceCaseAdapter of caseKey: string
        | ChoiceDefaultAdapter
        | ParallelFanOut of parallelId: string * branchCount: int * maxConcurrency: int
        | ParallelBranchAdapter of branchIndex: int
        | ParallelCollector of parallelId: string * branchIndex: int * branchCount: int
        | ParallelAggregate of
            parallelId: string *
            branchCount: int *
            maxConcurrency: int *
            aggregate: IAggregateHandler
        | RequestPrompt of IPromptHandler
        | RequestPort
        | LoopGuard of loopId: string * maxIterations: int * predicate: ILoopConditionHandler
        | LoopContinueAdapter
        | LoopExit

    type Node =
        { Id: string
          InputType: Type
          OutputType: Type
          Kind: NodeKind }

    type Fragment =
        { Nodes: Node list
          Edges: Edge list
          EntryId: string
          TerminalIds: string list }

    let nodeKindName kind =
        match kind with
        | Code _ -> "code"
        | Agent _ -> "agent"
        | ChoiceSelector _ -> "choice-selector"
        | ChoiceCaseAdapter _ -> "choice-case"
        | ChoiceDefaultAdapter -> "choice-default"
        | ParallelFanOut _ -> "parallel-fanout"
        | ParallelBranchAdapter _ -> "parallel-branch"
        | ParallelCollector _ -> "parallel-collector"
        | ParallelAggregate _ -> "parallel-aggregate"
        | RequestPrompt _ -> "request"
        | RequestPort -> "request-port"
        | LoopGuard _ -> "loop-guard"
        | LoopContinueAdapter -> "loop-continue"
        | LoopExit -> "loop-exit"

    module FingerprintJson =
        let private addString (node: JsonObject) (name: string) (value: string) =
            node[name] <-
                if isNull value then
                    null
                else
                    JsonValue.Create value :> JsonNode

        let private addInt (node: JsonObject) (name: string) (value: int) =
            node[name] <- JsonValue.Create value :> JsonNode

        let private addBool (node: JsonObject) (name: string) (value: bool) =
            node[name] <- JsonValue.Create value :> JsonNode

        let private addStringArray (node: JsonObject) (name: string) (values: string list) =
            let array = JsonArray()

            for value in values do
                array.Add(
                    if isNull value then
                        null
                    else
                        JsonValue.Create value :> JsonNode
                )
                |> ignore

            node[name] <- array :> JsonNode

        let private nodeContract kind =
            let contract = JsonObject()
            addString contract "name" (nodeKindName kind)

            match kind with
            | Code _
            | RequestPrompt _
            | RequestPort
            | ChoiceDefaultAdapter
            | LoopContinueAdapter
            | LoopExit -> ()
            | Agent(agent, signature) ->
                let agentObject = JsonObject()
                addString agentObject "id" agent.Id.Value
                addString agentObject "version" (agent.Version.ToString())
                contract["agent"] <- agentObject

                let signatureObject = JsonObject()
                addString signatureObject "id" signature.SignatureId.Value
                addString signatureObject "version" (signature.SignatureVersion.ToString())
                contract["signature"] <- signatureObject
            | ChoiceSelector(_, cases, hasDefault) ->
                addStringArray contract "cases" cases
                addBool contract "default" hasDefault
            | ChoiceCaseAdapter caseKey -> addString contract "case" caseKey
            | ParallelFanOut(parallelId, branchCount, maxConcurrency) ->
                addString contract "parallelId" parallelId
                addInt contract "branchCount" branchCount
                addInt contract "maxConcurrency" maxConcurrency
            | ParallelBranchAdapter branchIndex -> addInt contract "branchIndex" branchIndex
            | ParallelCollector(parallelId, branchIndex, branchCount) ->
                addString contract "parallelId" parallelId
                addInt contract "branchIndex" branchIndex
                addInt contract "branchCount" branchCount
            | ParallelAggregate(parallelId, branchCount, maxConcurrency, _) ->
                addString contract "parallelId" parallelId
                addInt contract "branchCount" branchCount
                addInt contract "maxConcurrency" maxConcurrency
            | LoopGuard(loopId, maxIterations, _) ->
                addString contract "loopId" loopId
                addInt contract "maxIterations" maxIterations

            contract :> JsonNode

        let private nodeFingerprint node =
            let fingerprint = JsonObject()
            addString fingerprint "id" node.Id
            fingerprint["kind"] <- nodeContract node.Kind
            addString fingerprint "inputType" (WorkflowValidation.typeId node.InputType)
            addString fingerprint "outputType" (WorkflowValidation.typeId node.OutputType)
            fingerprint :> JsonNode

        let private edgeFingerprint edge =
            let fingerprint = JsonObject()
            addString fingerprint "source" edge.SourceId
            addString fingerprint "target" edge.TargetId
            fingerprint :> JsonNode

        let compute
            (definitionId: DefinitionId)
            (version: SemanticVersion)
            (inputType: Type)
            (outputType: Type)
            (nodes: Node list)
            (edges: Edge list)
            (entryId: string)
            (terminalIds: string list)
            =
            let root = JsonObject()
            root["formatVersion"] <- JsonValue.Create 1

            let definition = JsonObject()
            addString definition "id" definitionId.Value
            addString definition "version" (version.ToString())
            addString definition "inputType" (WorkflowValidation.typeId inputType)
            addString definition "outputType" (WorkflowValidation.typeId outputType)
            root["definition"] <- definition

            let topology = JsonObject()
            addString topology "entryId" entryId
            addStringArray topology "terminalIds" terminalIds
            root["topology"] <- topology

            let nodeArray = JsonArray()

            nodes
            |> List.sortBy (fun node -> node.Id)
            |> List.iter (fun node -> nodeArray.Add(nodeFingerprint node) |> ignore)

            root["nodes"] <- nodeArray

            let edgeArray = JsonArray()

            edges
            |> List.sortBy (fun edge -> edge.SourceId, edge.TargetId)
            |> List.iter (fun edge -> edgeArray.Add(edgeFingerprint edge) |> ignore)

            root["edges"] <- edgeArray

            let payload = root.ToJsonString()
            use sha = SHA256.Create()
            let bytes = Encoding.UTF8.GetBytes payload |> sha.ComputeHash
            Convert.ToHexString(bytes).ToLowerInvariant()

[<Sealed>]
type WorkflowStep<'Input, 'Output> internal (fragment: WorkflowGraph.Fragment) =
    member internal _.Fragment = fragment

[<Sealed>]
type WorkflowDefinition<'Input, 'Output>
    internal
    (
        id: DefinitionId,
        version: SemanticVersion,
        nodes: WorkflowGraph.Node list,
        edges: WorkflowGraph.Edge list,
        entryId: string,
        terminalIds: string list
    ) =
    member _.Id = id
    member _.Version = version
    member internal _.Nodes = nodes
    member internal _.Edges = edges
    member internal _.EntryId = entryId
    member internal _.TerminalIds = terminalIds
    member internal _.InputType = typeof<'Input>
    member internal _.OutputType = typeof<'Output>

    member internal this.Fingerprint =
        WorkflowGraph.FingerprintJson.compute id version this.InputType this.OutputType nodes edges entryId terminalIds

[<Sealed>]
type WorkflowCheckpoint<'Output>
    internal
    (
        definitionId: DefinitionId,
        definitionVersion: SemanticVersion,
        fingerprint: string,
        sessionId: string,
        checkpointId: string,
        createdAt: DateTimeOffset,
        payload: JsonElement
    ) =
    let envelopeJson =
        let root = JsonObject()
        root["formatVersion"] <- JsonValue.Create 1
        root["definitionId"] <- JsonValue.Create definitionId.Value
        root["definitionVersion"] <- JsonValue.Create(definitionVersion.ToString())
        root["fingerprint"] <- JsonValue.Create fingerprint
        root["sessionId"] <- JsonValue.Create sessionId
        root["checkpointId"] <- JsonValue.Create checkpointId
        root["createdAt"] <- JsonValue.Create createdAt
        root["payload"] <- JsonNode.Parse(payload.GetRawText())
        root.ToJsonString()

    member _.DefinitionId = definitionId
    member _.DefinitionVersion = definitionVersion
    member _.CreatedAt = createdAt
    member internal _.Fingerprint = fingerprint
    member internal _.SessionId = sessionId
    member internal _.CheckpointId = checkpointId
    member internal _.Payload = payload.Clone()

    member _.Serialize() =
        use document = JsonDocument.Parse(envelopeJson)
        document.RootElement.Clone()

    static member internal Deserialize(state: JsonElement) =
        let expectProperty (name: string) =
            let mutable property = Unchecked.defaultof<JsonElement>

            if not (state.TryGetProperty(name, &property)) then
                invalidArg "state" $"Checkpoint envelope is missing the '{name}' property."

            property

        let formatVersion = (expectProperty "formatVersion").GetInt32()

        if formatVersion <> 1 then
            invalidArg "state" $"Unsupported checkpoint format version '{formatVersion}'."

        let definitionId = DefinitionId.Create((expectProperty "definitionId").GetString())

        let definitionVersion =
            SemanticVersion.Parse((expectProperty "definitionVersion").GetString())

        let fingerprint = (expectProperty "fingerprint").GetString()
        let sessionId = (expectProperty "sessionId").GetString()
        let checkpointId = (expectProperty "checkpointId").GetString()
        let createdAt = (expectProperty "createdAt").GetDateTimeOffset()
        let payload = expectProperty "payload"

        WorkflowCheckpoint<'Output>(
            definitionId,
            definitionVersion,
            fingerprint,
            sessionId,
            checkpointId,
            createdAt,
            payload.Clone()
        )

[<Sealed>]
type WorkflowRun<'Output>
    internal
    (
        runId: RunId,
        events: IAsyncEnumerable<RunEvent<'Output>>,
        respondAsync: ApprovalResponse * CancellationToken -> ValueTask,
        checkpointAsync: CancellationToken -> ValueTask<WorkflowCheckpoint<'Output>>,
        disposeAsync: unit -> ValueTask
    ) =
    member _.RunId = runId
    member _.Events = events

    member _.RespondAsync(response, cancellationToken) =
        respondAsync (response, cancellationToken)

    member _.CreateCheckpointAsync(cancellationToken) = checkpointAsync cancellationToken

    interface IAsyncDisposable with
        member _.DisposeAsync() = disposeAsync ()

type IWorkflowRuntime =
    abstract RunAsync<'Input, 'Output> :
        definition: WorkflowDefinition<'Input, 'Output> *
        input: 'Input *
        options: WorkflowRunOptions *
        cancellationToken: CancellationToken ->
            Task<RunResult<'Output>>

    abstract StartAsync<'Input, 'Output> :
        definition: WorkflowDefinition<'Input, 'Output> *
        input: 'Input *
        options: WorkflowRunOptions *
        cancellationToken: CancellationToken ->
            Task<WorkflowRun<'Output>>

    abstract ResumeAsync<'Input, 'Output> :
        definition: WorkflowDefinition<'Input, 'Output> *
        checkpoint: WorkflowCheckpoint<'Output> *
        cancellationToken: CancellationToken ->
            Task<WorkflowRun<'Output>>

module Workflow =
    let private singleNodeFragment (node: WorkflowGraph.Node) : WorkflowGraph.Fragment =
        { Nodes = [ node ]
          Edges = []
          EntryId = node.Id
          TerminalIds = [ node.Id ] }

    let private appendFragments
        (left: WorkflowGraph.Fragment)
        (right: WorkflowGraph.Fragment)
        : WorkflowGraph.Fragment =
        let linkingEdges =
            left.TerminalIds
            |> List.map (fun sourceId ->
                ({ SourceId = sourceId
                   TargetId = right.EntryId }
                : WorkflowGraph.Edge))

        { Nodes = left.Nodes @ right.Nodes
          Edges = left.Edges @ right.Edges @ linkingEdges
          EntryId = left.EntryId
          TerminalIds = right.TerminalIds }

    let private mergeFragments
        (entryId: string)
        (terminalIds: string list)
        (fragments: WorkflowGraph.Fragment list)
        : WorkflowGraph.Fragment =
        { Nodes = fragments |> List.collect _.Nodes
          Edges = fragments |> List.collect _.Edges
          EntryId = entryId
          TerminalIds = terminalIds }

    let code id handler =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box handler) then
            nullArg "handler"

        let node: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'Input>
              OutputType = typeof<'Output>
              Kind =
                WorkflowGraph.Code(WorkflowGraph.CodeHandler<'Input, 'Output>(handler) :> WorkflowGraph.ICodeHandler) }

        WorkflowStep<'Input, 'Output>(singleNodeFragment node)

    let agent id (agent: AgentDefinition) (signature: Signature<'Input, 'Output>) =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        let node: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'Input>
              OutputType = typeof<'Output>
              Kind =
                WorkflowGraph.Agent(
                    agent,
                    WorkflowGraph.ErasedSignature<'Input, 'Output>(signature) :> WorkflowGraph.IErasedSignature
                ) }

        WorkflowStep<'Input, 'Output>(singleNodeFragment node)

    let thenStep (step: WorkflowStep<'A, 'B>) (definition: WorkflowDefinition<'Input, 'A>) =
        if isNull (box step) then
            nullArg "step"

        if isNull (box definition) then
            nullArg "definition"

        let merged =
            appendFragments
                { Nodes = definition.Nodes
                  Edges = definition.Edges
                  EntryId = definition.EntryId
                  TerminalIds = definition.TerminalIds }
                step.Fragment

        WorkflowDefinition<'Input, 'B>(
            definition.Id,
            definition.Version,
            merged.Nodes,
            merged.Edges,
            merged.EntryId,
            merged.TerminalIds
        )

    let internal chooseCases
        id
        selector
        (cases: (string * WorkflowDefinition<'A, 'B>) list)
        (defaultCase: WorkflowDefinition<'A, 'B> option)
        =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box selector) then
            nullArg "selector"

        if isNull (box cases) then
            nullArg "cases"

        let selectorNode: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'A>
              OutputType = typeof<WorkflowGraph.BranchSelection<'A>>
              Kind =
                WorkflowGraph.ChoiceSelector(
                    WorkflowGraph.SelectorHandler<'A>(selector) :> WorkflowGraph.ISelectorHandler,
                    cases |> List.map fst,
                    defaultCase.IsSome
                ) }

        let fragments = ResizeArray<WorkflowGraph.Fragment>()
        fragments.Add(singleNodeFragment selectorNode)
        let extraEdges = ResizeArray<WorkflowGraph.Edge>()
        let terminalIds = ResizeArray<string>()

        cases
        |> List.iteri (fun index (caseKey, branch) ->
            let keyToken =
                if isNull caseKey then
                    "null"
                else
                    WorkflowValidation.stableIdSuffix caseKey

            let adapterId = $"{id}.case.{keyToken}.{index}"

            let adapterNode: WorkflowGraph.Node =
                { Id = adapterId
                  InputType = typeof<WorkflowGraph.BranchSelection<'A>>
                  OutputType = typeof<'A>
                  Kind = WorkflowGraph.ChoiceCaseAdapter caseKey }

            let fragment: WorkflowGraph.Fragment =
                { Nodes = adapterNode :: branch.Nodes
                  Edges =
                    ({ SourceId = adapterId
                       TargetId = branch.EntryId }
                    : WorkflowGraph.Edge)
                    :: branch.Edges
                  EntryId = adapterId
                  TerminalIds = branch.TerminalIds }

            fragments.Add fragment
            extraEdges.Add(({ SourceId = id; TargetId = adapterId }: WorkflowGraph.Edge))
            branch.TerminalIds |> List.iter terminalIds.Add)

        match defaultCase with
        | Some branch ->
            let adapterId = $"{id}.default"

            let adapterNode: WorkflowGraph.Node =
                { Id = adapterId
                  InputType = typeof<WorkflowGraph.BranchSelection<'A>>
                  OutputType = typeof<'A>
                  Kind = WorkflowGraph.ChoiceDefaultAdapter }

            let fragment: WorkflowGraph.Fragment =
                { Nodes = adapterNode :: branch.Nodes
                  Edges =
                    ({ SourceId = adapterId
                       TargetId = branch.EntryId }
                    : WorkflowGraph.Edge)
                    :: branch.Edges
                  EntryId = adapterId
                  TerminalIds = branch.TerminalIds }

            fragments.Add fragment
            extraEdges.Add(({ SourceId = id; TargetId = adapterId }: WorkflowGraph.Edge))
            branch.TerminalIds |> List.iter terminalIds.Add
        | None -> ()

        let merged = mergeFragments id (terminalIds |> Seq.toList) (fragments |> Seq.toList)

        WorkflowStep<'A, 'B>(
            { merged with
                Edges = merged.Edges @ (extraEdges |> Seq.toList) }
        )

    let choose
        id
        selector
        (cases: Map<string, WorkflowDefinition<'A, 'B>>)
        (defaultCase: WorkflowDefinition<'A, 'B> option)
        =
        chooseCases id selector (cases |> Map.toList) defaultCase

    let ``parallel`` id maxConcurrency (branches: WorkflowDefinition<'A, 'B> list) aggregate =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box branches) then
            nullArg "branches"

        if isNull (box aggregate) then
            nullArg "aggregate"

        let startNode: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'A>
              OutputType = typeof<'A>
              Kind = WorkflowGraph.ParallelFanOut(id, branches.Length, maxConcurrency) }

        let aggregateNode: WorkflowGraph.Node =
            { Id = $"{id}.aggregate"
              InputType = typeof<WorkflowGraph.ParallelBranchResult<'B>>
              OutputType = typeof<WorkflowGraph.ParallelAggregateDispatch<'C>>
              Kind =
                WorkflowGraph.ParallelAggregate(
                    id,
                    branches.Length,
                    maxConcurrency,
                    WorkflowGraph.AggregateHandler<'B, 'C>(aggregate) :> WorkflowGraph.IAggregateHandler
                ) }

        let pendingNode: WorkflowGraph.Node =
            { Id = $"{id}.pending"
              InputType = typeof<WorkflowGraph.ParallelAggregateDispatch<'C>>
              OutputType = typeof<unit>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<WorkflowGraph.ParallelAggregateDispatch<'C>, unit>(fun _ _ ->
                        Task.FromResult(()))
                    :> WorkflowGraph.ICodeHandler
                ) }

        let completeNode: WorkflowGraph.Node =
            { Id = $"{id}.complete"
              InputType = typeof<WorkflowGraph.ParallelAggregateDispatch<'C>>
              OutputType = typeof<'C>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<WorkflowGraph.ParallelAggregateDispatch<'C>, 'C>(fun _ value ->
                        if value.IsComplete then
                            Task.FromResult value.Value
                        else
                            raise (InvalidOperationException("Parallel aggregation did not complete.")))
                    :> WorkflowGraph.ICodeHandler
                ) }

        let fragments = ResizeArray<WorkflowGraph.Fragment>()
        fragments.Add(singleNodeFragment startNode)
        fragments.Add(singleNodeFragment aggregateNode)
        fragments.Add(singleNodeFragment pendingNode)
        fragments.Add(singleNodeFragment completeNode)
        let extraEdges = ResizeArray<WorkflowGraph.Edge>()

        branches
        |> List.iteri (fun index branch ->
            let adapterId = $"{id}.branch.{index}.input"
            let collectId = $"{id}.branch.{index}.collect"

            let adapterNode: WorkflowGraph.Node =
                { Id = adapterId
                  InputType = typeof<'A>
                  OutputType = typeof<'A>
                  Kind = WorkflowGraph.ParallelBranchAdapter index }

            let collectNode: WorkflowGraph.Node =
                { Id = collectId
                  InputType = typeof<'B>
                  OutputType = typeof<WorkflowGraph.ParallelBranchResult<'B>>
                  Kind = WorkflowGraph.ParallelCollector(id, index, branches.Length) }

            let branchFragment: WorkflowGraph.Fragment =
                { Nodes = adapterNode :: collectNode :: branch.Nodes
                  Edges =
                    [ ({ SourceId = adapterId
                         TargetId = branch.EntryId }
                      : WorkflowGraph.Edge)
                      yield! branch.Edges
                      for terminalId in branch.TerminalIds do
                          ({ SourceId = terminalId
                             TargetId = collectId }
                          : WorkflowGraph.Edge) ]
                  EntryId = adapterId
                  TerminalIds = [ collectId ] }

            fragments.Add branchFragment
            extraEdges.Add(({ SourceId = id; TargetId = adapterId }: WorkflowGraph.Edge)))

        for index in 0 .. branches.Length - 1 do
            extraEdges.Add(
                ({ SourceId = $"{id}.branch.{index}.collect"
                   TargetId = aggregateNode.Id }
                : WorkflowGraph.Edge)
            )

        extraEdges.Add(
            ({ SourceId = aggregateNode.Id
               TargetId = pendingNode.Id }
            : WorkflowGraph.Edge)
        )

        extraEdges.Add(
            ({ SourceId = aggregateNode.Id
               TargetId = completeNode.Id }
            : WorkflowGraph.Edge)
        )

        let merged = mergeFragments id [ completeNode.Id ] (fragments |> Seq.toList)

        WorkflowStep<'A, 'C>(
            { merged with
                Edges = merged.Edges @ (extraEdges |> Seq.toList) }
        )

    let request id prompt =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box prompt) then
            nullArg "prompt"

        let promptNode: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'A>
              OutputType = typeof<ApprovalPrompt>
              Kind =
                WorkflowGraph.RequestPrompt(WorkflowGraph.PromptHandler<'A>(prompt) :> WorkflowGraph.IPromptHandler) }

        let portNode: WorkflowGraph.Node =
            { Id = $"{id}.port"
              InputType = typeof<ApprovalPrompt>
              OutputType = typeof<ApprovalResponse>
              Kind = WorkflowGraph.RequestPort }

        WorkflowStep<'A, ApprovalResponse>(
            { Nodes = [ promptNode; portNode ]
              Edges =
                [ ({ SourceId = promptNode.Id
                     TargetId = portNode.Id }
                  : WorkflowGraph.Edge) ]
              EntryId = promptNode.Id
              TerminalIds = [ portNode.Id ] }
        )

    let loop id maxIterations whileTrue (body: WorkflowDefinition<'A, 'A>) =
        WorkflowValidation.requireNonBlank "id" id |> ignore

        if isNull (box whileTrue) then
            nullArg "whileTrue"

        if isNull (box body) then
            nullArg "body"

        let continueId = $"{id}.continue"
        let exitId = $"{id}.exit"

        let guardNode: WorkflowGraph.Node =
            { Id = id
              InputType = typeof<'A>
              OutputType = typeof<WorkflowGraph.LoopDecision<'A>>
              Kind =
                WorkflowGraph.LoopGuard(
                    id,
                    maxIterations,
                    WorkflowGraph.LoopConditionHandler<'A>(whileTrue) :> WorkflowGraph.ILoopConditionHandler
                ) }

        let continueNode: WorkflowGraph.Node =
            { Id = continueId
              InputType = typeof<WorkflowGraph.LoopDecision<'A>>
              OutputType = typeof<'A>
              Kind = WorkflowGraph.LoopContinueAdapter }

        let exitNode: WorkflowGraph.Node =
            { Id = exitId
              InputType = typeof<WorkflowGraph.LoopDecision<'A>>
              OutputType = typeof<'A>
              Kind = WorkflowGraph.LoopExit }

        let fragment: WorkflowGraph.Fragment =
            { Nodes = guardNode :: continueNode :: exitNode :: body.Nodes
              Edges =
                [ ({ SourceId = id; TargetId = continueId }: WorkflowGraph.Edge)
                  ({ SourceId = id; TargetId = exitId }: WorkflowGraph.Edge)
                  ({ SourceId = continueId
                     TargetId = body.EntryId }
                  : WorkflowGraph.Edge)
                  yield! body.Edges
                  for terminalId in body.TerminalIds do
                      ({ SourceId = terminalId; TargetId = id }: WorkflowGraph.Edge) ]
              EntryId = id
              TerminalIds = [ exitId ] }

        WorkflowStep<'A, 'A>(fragment)

    let define id version (first: WorkflowStep<'Input, 'Output>) =
        WorkflowValidation.requireNonBlank "id" id |> ignore
        WorkflowValidation.requireNonBlank "version" version |> ignore

        if isNull (box first) then
            nullArg "first"

        WorkflowDefinition<'Input, 'Output>(
            DefinitionId.Create id,
            SemanticVersion.Parse version,
            first.Fragment.Nodes,
            first.Fragment.Edges,
            first.Fragment.EntryId,
            first.Fragment.TerminalIds
        )

    let validate (definition: WorkflowDefinition<'Input, 'Output>) =
        if isNull (box definition) then
            nullArg "definition"

        let issues = ResizeArray<WorkflowValidationIssue>()
        let nodes = definition.Nodes
        let edges = definition.Edges

        nodes
        |> Seq.groupBy _.Id
        |> Seq.iter (fun (nodeId, groupedNodes) ->
            if Seq.length groupedNodes > 1 then
                issues.Add(WorkflowValidationIssue(nodeId, "duplicate-id", "Duplicate node IDs are not allowed.")))

        let uniqueNodes =
            nodes
            |> Seq.groupBy _.Id
            |> Seq.choose (fun (nodeId, groupedNodes) ->
                groupedNodes |> Seq.tryExactlyOne |> Option.map (fun node -> nodeId, node))
            |> dict

        if List.isEmpty definition.TerminalIds then
            issues.Add(
                WorkflowValidationIssue(definition.Id.Value, "missing-terminal", "The workflow has no terminal output.")
            )

        for edge in edges do
            if not (uniqueNodes.ContainsKey edge.SourceId) then
                issues.Add(WorkflowValidationIssue(edge.SourceId, "dangling-source", "The edge source does not exist."))

            if not (uniqueNodes.ContainsKey edge.TargetId) then
                issues.Add(WorkflowValidationIssue(edge.TargetId, "dangling-target", "The edge target does not exist."))

            if uniqueNodes.ContainsKey edge.SourceId && uniqueNodes.ContainsKey edge.TargetId then
                let sourceNode = uniqueNodes[edge.SourceId]
                let targetNode = uniqueNodes[edge.TargetId]

                if sourceNode.OutputType <> targetNode.InputType then
                    issues.Add(
                        WorkflowValidationIssue(
                            edge.TargetId,
                            "type-mismatch",
                            $"Node '{sourceNode.Id}' outputs '{WorkflowValidation.typeId sourceNode.OutputType}' but '{targetNode.Id}' expects '{WorkflowValidation.typeId targetNode.InputType}'."
                        )
                    )

        if uniqueNodes.ContainsKey definition.EntryId then
            let entryNode = uniqueNodes[definition.EntryId]

            if entryNode.InputType <> definition.InputType then
                issues.Add(
                    WorkflowValidationIssue(
                        entryNode.Id,
                        "entry-type",
                        "The workflow entry type does not match the definition input type."
                    )
                )
        else
            issues.Add(
                WorkflowValidationIssue(definition.EntryId, "missing-entry", "The workflow entry node does not exist.")
            )

        for terminalId in definition.TerminalIds do
            if uniqueNodes.ContainsKey terminalId then
                let terminalNode = uniqueNodes[terminalId]

                if terminalNode.OutputType <> definition.OutputType then
                    issues.Add(
                        WorkflowValidationIssue(
                            terminalNode.Id,
                            "terminal-type",
                            "The workflow terminal output type does not match the definition output type."
                        )
                    )
            else
                issues.Add(
                    WorkflowValidationIssue(
                        terminalId,
                        "missing-terminal-node",
                        "The workflow terminal node does not exist."
                    )
                )

        let outgoing = edges |> Seq.groupBy _.SourceId |> dict
        let reachable = HashSet<string>(StringComparer.Ordinal)

        let rec visit nodeId =
            if reachable.Add nodeId then
                match outgoing.TryGetValue nodeId with
                | true, nextEdges -> nextEdges |> Seq.iter (fun edge -> visit edge.TargetId)
                | _ -> ()

        if not (String.IsNullOrWhiteSpace definition.EntryId) then
            visit definition.EntryId

        for node in nodes do
            if not (reachable.Contains node.Id) then
                issues.Add(
                    WorkflowValidationIssue(
                        node.Id,
                        "unreachable",
                        "The node is unreachable from the workflow entry node."
                    )
                )

            match node.Kind with
            | WorkflowGraph.Code _ -> ()
            | WorkflowGraph.Agent _ -> ()
            | WorkflowGraph.ChoiceSelector(_, cases, _) ->
                if cases |> List.exists String.IsNullOrWhiteSpace then
                    issues.Add(WorkflowValidationIssue(node.Id, "branch-key", "Branch keys cannot be blank."))

                for duplicateKey in WorkflowValidation.duplicateKeys cases do
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "duplicate-branch-key",
                            $"Duplicate branch key '{duplicateKey}' is not allowed."
                        )
                    )
            | WorkflowGraph.ChoiceCaseAdapter caseKey ->
                if String.IsNullOrWhiteSpace caseKey then
                    issues.Add(WorkflowValidationIssue(node.Id, "branch-key", "Branch keys cannot be blank."))
            | WorkflowGraph.ChoiceDefaultAdapter -> ()
            | WorkflowGraph.ParallelFanOut(_, branchCount, maxConcurrency) ->
                if branchCount < 1 then
                    issues.Add(WorkflowValidationIssue(node.Id, "empty-parallel", "Parallel branches cannot be empty."))

                if maxConcurrency < 1 then
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "parallel-concurrency",
                            "Parallel maxConcurrency must be at least 1."
                        )
                    )
            | WorkflowGraph.ParallelBranchAdapter branchIndex ->
                if branchIndex < 0 then
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "parallel-branch-index",
                            "Parallel branch indices must be zero or greater."
                        )
                    )
            | WorkflowGraph.ParallelCollector(_, branchIndex, branchCount) ->
                if branchIndex < 0 then
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "parallel-branch-index",
                            "Parallel branch indices must be zero or greater."
                        )
                    )

                if branchCount < 1 then
                    issues.Add(WorkflowValidationIssue(node.Id, "empty-parallel", "Parallel branches cannot be empty."))
            | WorkflowGraph.ParallelAggregate(_, branchCount, maxConcurrency, _) ->
                if branchCount < 1 then
                    issues.Add(WorkflowValidationIssue(node.Id, "empty-parallel", "Parallel branches cannot be empty."))

                if maxConcurrency < 1 then
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "parallel-concurrency",
                            "Parallel maxConcurrency must be at least 1."
                        )
                    )
            | WorkflowGraph.RequestPrompt _ -> ()
            | WorkflowGraph.RequestPort -> ()
            | WorkflowGraph.LoopGuard(_, maxIterations, _) ->
                if maxIterations < 1 then
                    issues.Add(
                        WorkflowValidationIssue(
                            node.Id,
                            "loop-bound",
                            "Loops must declare a positive maxIterations bound."
                        )
                    )
            | WorkflowGraph.LoopContinueAdapter -> ()
            | WorkflowGraph.LoopExit -> ()

        issues.ToArray() :> IReadOnlyList<WorkflowValidationIssue>

    let run
        (runtime: IWorkflowRuntime)
        (definition: WorkflowDefinition<'Input, 'Output>)
        (input: 'Input)
        (options: WorkflowRunOptions)
        (cancellationToken: CancellationToken)
        =
        runtime.RunAsync(definition, input, options, cancellationToken)

    let start
        (runtime: IWorkflowRuntime)
        (definition: WorkflowDefinition<'Input, 'Output>)
        (input: 'Input)
        (options: WorkflowRunOptions)
        (cancellationToken: CancellationToken)
        =
        runtime.StartAsync(definition, input, options, cancellationToken)

    let resume
        (runtime: IWorkflowRuntime)
        (definition: WorkflowDefinition<'Input, 'Output>)
        (checkpoint: WorkflowCheckpoint<'Output>)
        (cancellationToken: CancellationToken)
        =
        runtime.ResumeAsync(definition, checkpoint, cancellationToken)
