namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

[<Sealed>]
type internal ResolvedMafTool(tool: Circuit.Core.ResolvedTool, modelName: string, mafFunction: AIFunction) =
    member _.Tool = tool
    member _.ModelName = modelName
    member _.MafFunction = mafFunction
    member _.Tags = tool.Tags

    member _.RequiresApproval =
        tool.Approval = ApprovalMode.Always || tool.Approval = ApprovalMode.ByPolicy

[<Sealed>]
type ToolApprovalContext
    internal (runContext: RunContext, tool: Circuit.Core.ResolvedTool, arguments: IReadOnlyDictionary<string, obj>) =
    let toolView = Circuit.ResolvedTool.FromCore tool

    member _.RunContext = runContext
    member _.Tool = toolView
    member _.Arguments = arguments

type IToolApprovalPolicy =
    abstract IsApprovedAsync: policyName: string * context: ToolApprovalContext -> ValueTask<bool>

[<Sealed>]
type MafObservedEvent
    internal
    (
        runId: RunId,
        timestamp: DateTimeOffset,
        kind: RunEventKind,
        operationId: string voption,
        textDelta: string voption,
        failure: CircuitFailure voption,
        approval: ApprovalRequest voption
    ) =
    member _.RunId = runId
    member _.Timestamp = timestamp
    member _.Kind = kind
    member _.OperationId = operationId
    member _.TextDelta = textDelta
    member _.Failure = failure
    member _.Approval = approval

[<Sealed>]
type MafRunObservation
    internal
    (
        context: RunContext,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset,
        repaired: bool,
        usage: RunUsage,
        session: CircuitSession voption,
        failure: CircuitFailure voption,
        diagnosticMetadata: IReadOnlyDictionary<string, string>
    ) =
    member _.Context = context
    member _.StartedAt = startedAt
    member _.CompletedAt = completedAt
    member _.Repaired = repaired
    member _.Usage = usage
    member _.Session = session
    member _.Failure = failure
    member _.DiagnosticMetadata = diagnosticMetadata

type IRunObserver =
    abstract OnRunStartedAsync:
        context: RunContext * startedAt: DateTimeOffset * cancellationToken: CancellationToken -> ValueTask

    abstract OnRunEventAsync: event: MafObservedEvent * cancellationToken: CancellationToken -> ValueTask
    abstract OnRunCompletedAsync: observation: MafRunObservation * cancellationToken: CancellationToken -> ValueTask

module internal MafObserver =
    let notifyStartedAsync
        (observers: IReadOnlyList<IRunObserver>)
        (context: RunContext)
        (startedAt: DateTimeOffset)
        (cancellationToken: CancellationToken)
        =
        task {
            for observer in observers do
                try
                    do! observer.OnRunStartedAsync(context, startedAt, cancellationToken).AsTask()
                with _ ->
                    ()
        }

    let notifyEventAsync
        (observers: IReadOnlyList<IRunObserver>)
        (runId: RunId)
        (kind: RunEventKind)
        (operationId: string voption)
        (textDelta: string voption)
        (failure: CircuitFailure voption)
        (approval: ApprovalRequest voption)
        (cancellationToken: CancellationToken)
        =
        task {
            if observers.Count > 0 then
                let observedEvent =
                    MafObservedEvent(runId, DateTimeOffset.UtcNow, kind, operationId, textDelta, failure, approval)

                for observer in observers do
                    try
                        do! observer.OnRunEventAsync(observedEvent, cancellationToken).AsTask()
                    with _ ->
                        ()
        }

    let notifyCompletedAsync
        (observers: IReadOnlyList<IRunObserver>)
        (observation: MafRunObservation)
        (cancellationToken: CancellationToken)
        =
        task {
            for observer in observers do
                try
                    do! observer.OnRunCompletedAsync(observation, cancellationToken).AsTask()
                with _ ->
                    ()
        }

[<Sealed>]
type MafRuntimeOptions() =
    let createDefaultSerializerOptions () =
        let options = CircuitJson.createOptions ()
        options.MakeReadOnly()
        options

    let snapshotJsonOptions (options: JsonSerializerOptions) =
        if isNull options then
            null
        else
            let snapshot = JsonSerializerOptions(options)
            snapshot.MakeReadOnly()
            snapshot

    let snapshotResolvers (resolvers: IReadOnlyList<'T>) =
        if isNull resolvers then
            null
        else
            resolvers |> Seq.toArray :> IReadOnlyList<'T>

    let emptyToolResolvers =
        Array.empty<Circuit.Core.IToolResolver> :> IReadOnlyList<Circuit.Core.IToolResolver>

    let emptySkillResolvers =
        Array.empty<Circuit.Core.ISkillResolver> :> IReadOnlyList<Circuit.Core.ISkillResolver>

    let emptyObservers = Array.empty<IRunObserver> :> IReadOnlyList<IRunObserver>

    let mutable isFrozen = false
    let mutable defaultModelId: string voption = ValueNone
    let mutable jsonSerializerOptions = createDefaultSerializerOptions ()
    let mutable secondaryStructuredOutputClient: IChatClient voption = ValueNone

    let mutable toolResolvers: IReadOnlyList<Circuit.Core.IToolResolver> =
        emptyToolResolvers

    let mutable toolApprovalPolicy: IToolApprovalPolicy voption = ValueNone

    let mutable skillResolvers: IReadOnlyList<Circuit.Core.ISkillResolver> =
        emptySkillResolvers

    let mutable skillScriptRunner: Circuit.Core.ISkillScriptRunner voption = ValueNone
    let mutable observers: IReadOnlyList<IRunObserver> = emptyObservers

    let throwIfFrozen () =
        if isFrozen then
            invalidOp "Options are frozen."

    member _.DefaultModelId
        with get () = defaultModelId
        and set value =
            throwIfFrozen ()
            defaultModelId <- value

    member _.JsonSerializerOptions
        with get () = jsonSerializerOptions
        and set value =
            throwIfFrozen ()
            jsonSerializerOptions <- value

    member _.SecondaryStructuredOutputClient
        with get () = secondaryStructuredOutputClient
        and set value =
            throwIfFrozen ()
            secondaryStructuredOutputClient <- value

    member _.ToolResolvers
        with get () = toolResolvers
        and set value =
            throwIfFrozen ()
            toolResolvers <- value

    member _.ToolApprovalPolicy
        with get () = toolApprovalPolicy
        and set value =
            throwIfFrozen ()
            toolApprovalPolicy <- value

    member _.SkillResolvers
        with get () = skillResolvers
        and set value =
            throwIfFrozen ()
            skillResolvers <- value

    member _.SkillScriptRunner
        with get () = skillScriptRunner
        and set value =
            throwIfFrozen ()
            skillScriptRunner <- value

    member _.Observers
        with get () = observers
        and set value =
            throwIfFrozen ()
            observers <- value

    member internal this.Freeze() =
        if not isFrozen then
            jsonSerializerOptions <- snapshotJsonOptions jsonSerializerOptions
            toolResolvers <- snapshotResolvers toolResolvers
            skillResolvers <- snapshotResolvers skillResolvers
            observers <- snapshotResolvers observers
            isFrozen <- true

    member internal this.Snapshot() =
        let snapshot = MafRuntimeOptions()
        snapshot.DefaultModelId <- defaultModelId
        snapshot.JsonSerializerOptions <- snapshotJsonOptions jsonSerializerOptions
        snapshot.SecondaryStructuredOutputClient <- secondaryStructuredOutputClient
        snapshot.ToolResolvers <- snapshotResolvers toolResolvers
        snapshot.ToolApprovalPolicy <- toolApprovalPolicy
        snapshot.SkillResolvers <- snapshotResolvers skillResolvers
        snapshot.SkillScriptRunner <- skillScriptRunner
        snapshot.Observers <- snapshotResolvers observers
        snapshot.Freeze()
        snapshot
