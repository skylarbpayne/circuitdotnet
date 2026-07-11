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
type ResolvedTool internal (name: string, tool: AIFunction, tags: IReadOnlySet<string>, requiresApproval: bool) =
    member _.Name = name
    member _.Tool = tool
    member _.Tags = tags
    member _.RequiresApproval = requiresApproval

[<Sealed>]
type ResolvedSkill internal (reference: SkillReference, provider: AIContextProvider) =
    member _.Reference = reference
    member _.Provider = provider

type IToolResolver =
    abstract ResolveToolsAsync:
        context: RunContext * cancellationToken: CancellationToken -> ValueTask<IReadOnlyList<ResolvedTool>>

type ISkillResolver =
    abstract ResolveSkillsAsync:
        context: RunContext * cancellationToken: CancellationToken -> ValueTask<IReadOnlyList<ResolvedSkill>>

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
    let serializerOptions =
        let options = CircuitJson.createOptions ()
        options.MakeReadOnly()
        options

    let emptyToolResolvers = Array.empty<IToolResolver> :> IReadOnlyList<IToolResolver>

    let emptySkillResolvers =
        Array.empty<ISkillResolver> :> IReadOnlyList<ISkillResolver>

    let emptyObservers = Array.empty<IRunObserver> :> IReadOnlyList<IRunObserver>

    member val DefaultModelId: string voption = ValueNone with get, set
    member val JsonSerializerOptions: JsonSerializerOptions = serializerOptions with get, set
    member val SecondaryStructuredOutputClient: IChatClient voption = ValueNone with get, set
    member val ToolResolvers: IReadOnlyList<IToolResolver> = emptyToolResolvers with get, set
    member val SkillResolvers: IReadOnlyList<ISkillResolver> = emptySkillResolvers with get, set
    member val Observers: IReadOnlyList<IRunObserver> = emptyObservers with get, set
