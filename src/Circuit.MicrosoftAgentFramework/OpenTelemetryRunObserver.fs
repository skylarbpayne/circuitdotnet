namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core

/// Configures what payloads <see cref="T:Circuit.MicrosoftAgentFramework.OpenTelemetryRunObserver" /> records.
/// <remarks>
/// Enabling prompt, input, output, or tool-argument capture can emit sensitive user and system data into traces and logs.
/// Redaction is best-effort and should not be treated as a hard security boundary.
/// </remarks>
[<Sealed>]
type OpenTelemetryRunObserverOptions() =
    /// Gets or sets whether prompts are attached to the root run activity.
    member val CapturePrompt = false with get, set

    /// Gets or sets whether serialized inputs are attached to the root run activity.
    member val CaptureInput = false with get, set

    /// Gets or sets whether serialized outputs are attached to terminal run activities.
    member val CaptureOutput = false with get, set

    /// Gets or sets whether serialized tool arguments are attached to tool activities.
    member val CaptureToolArguments = false with get, set

    /// Gets or sets an optional redactor applied before captured payloads are recorded.
    member val Redactor: Func<string, string> = null with get, set

module private OpenTelemetryRunObserverInternals =
    [<Literal>]
    let PromptTag = "circuit.prompt"

    [<Literal>]
    let InputTag = "circuit.input"

    [<Literal>]
    let OutputTag = "circuit.output"

    [<Literal>]
    let ToolArgumentsTag = "circuit.tool.arguments"

    let Runs = TelemetryContracts.Meter.CreateCounter<int64>("circuit.runs")

    let RunDuration =
        TelemetryContracts.Meter.CreateHistogram<double>("circuit.run.duration", "s")

    let RunsActive =
        TelemetryContracts.Meter.CreateUpDownCounter<int64>("circuit.runs.active")

    let Tools = TelemetryContracts.Meter.CreateCounter<int64>("circuit.tools")

    let ToolDuration =
        TelemetryContracts.Meter.CreateHistogram<double>("circuit.tool.duration", "s")

    let WorkflowSteps =
        TelemetryContracts.Meter.CreateCounter<int64>("circuit.workflow.steps")

    let WorkflowStepDuration =
        TelemetryContracts.Meter.CreateHistogram<double>("circuit.workflow.step.duration", "s")

    let ValidationFailures =
        TelemetryContracts.Meter.CreateCounter<int64>("circuit.validation.failures")

    let ApprovalsRequested =
        TelemetryContracts.Meter.CreateCounter<int64>("circuit.approvals.requested")

    let StructuredOutputRepairs =
        TelemetryContracts.Meter.CreateCounter<int64>("circuit.structured_output.repairs")

    let statusFromFailure (failure: AgentFailure) =
        if isNull failure then
            "success"
        elif failure.Code = AgentFailureCode.Cancelled then
            "cancelled"
        else
            "failure"

    let maybeAdd (tags: ResizeArray<KeyValuePair<string, obj>>) name value =
        if not (String.IsNullOrWhiteSpace value) then
            tags.Add(KeyValuePair(name, box value))

    let createMetricTagTemplate (event: RunEventEnvelope) =
        let tags = ResizeArray<KeyValuePair<string, obj>>()
        maybeAdd tags "gen_ai.agent.name" event.AgentName
        maybeAdd tags "circuit.definition.id" event.DefinitionId
        maybeAdd tags "circuit.definition.version" event.DefinitionVersion
        maybeAdd tags "circuit.operation.kind" (string event.OperationKind)
        maybeAdd tags "gen_ai.request.model" event.RequestModel
        tags.ToArray()

    let createMetricTags (baseTags: KeyValuePair<string, obj>[]) status =
        let tags = ResizeArray<KeyValuePair<string, obj>>(baseTags.Length + 1)

        for tag in baseTags do
            tags.Add(tag)

        tags.Add(KeyValuePair("circuit.status", box status))
        tags.ToArray()

    let applyCommonTraceTags (activity: Activity) (event: RunEventEnvelope) =
        activity.SetTag("gen_ai.operation.name", event.OperationName) |> ignore
        activity.SetTag("gen_ai.agent.name", event.AgentName) |> ignore
        activity.SetTag("circuit.run.id", event.RunId) |> ignore
        activity.SetTag("circuit.definition.id", event.DefinitionId) |> ignore
        activity.SetTag("circuit.definition.version", event.DefinitionVersion) |> ignore
        activity.SetTag("circuit.operation.id", event.OperationId) |> ignore
        activity.SetTag("circuit.operation.kind", string event.OperationKind) |> ignore

        if not (String.IsNullOrWhiteSpace event.RequestModel) then
            activity.SetTag("gen_ai.request.model", event.RequestModel) |> ignore

    let applyFailureTags (activity: Activity) (failure: AgentFailure) =
        if not (isNull activity) && not (isNull failure) then
            let errorType = string failure.Code
            activity.SetTag("error.type", errorType) |> ignore
            activity.SetTag("circuit.status", statusFromFailure failure) |> ignore
            activity.SetStatus(ActivityStatusCode.Error, errorType) |> ignore

    let applySuccessTags (activity: Activity) =
        if not (isNull activity) then
            activity.SetTag("circuit.status", "success") |> ignore
            activity.SetStatus(ActivityStatusCode.Ok) |> ignore

    let captureSensitive (options: OpenTelemetryRunObserverOptions) enabled value =
        if not enabled || String.IsNullOrEmpty value then
            ValueNone
        else
            let redacted =
                if isNull options.Redactor then
                    value
                else
                    options.Redactor.Invoke(value)

            if String.IsNullOrEmpty redacted then
                ValueNone
            else
                ValueSome redacted

    type OperationState =
        { Activity: Activity
          StartedAt: DateTimeOffset
          OperationKind: RunOperationKind
          MetricTagTemplate: KeyValuePair<string, obj>[] }

    type RunState =
        { RootActivity: Activity
          StartedAt: DateTimeOffset
          StartMetricTags: KeyValuePair<string, obj>[]
          Operations: ConcurrentDictionary<string, OperationState> }

/// Emits Circuit run telemetry as OpenTelemetry traces and metrics.
/// <param name="options">Optional capture settings. When omitted, only structural telemetry is emitted.</param>
[<Sealed>]
type OpenTelemetryRunObserver(?options: OpenTelemetryRunObserverOptions) =
    let options = defaultArg options (OpenTelemetryRunObserverOptions())

    let states =
        ConcurrentDictionary<string, OpenTelemetryRunObserverInternals.RunState>()

    let stopOperation
        (timestamp: DateTimeOffset)
        (failure: AgentFailure)
        (state: OpenTelemetryRunObserverInternals.OperationState)
        =
        let duration = max 0.0 (timestamp - state.StartedAt).TotalSeconds
        let status = OpenTelemetryRunObserverInternals.statusFromFailure failure

        let metricTags =
            OpenTelemetryRunObserverInternals.createMetricTags state.MetricTagTemplate status

        match state.OperationKind with
        | RunOperationKind.Tool ->
            OpenTelemetryRunObserverInternals.Tools.Add(1L, metricTags)
            OpenTelemetryRunObserverInternals.ToolDuration.Record(duration, metricTags)
        | RunOperationKind.WorkflowStep ->
            OpenTelemetryRunObserverInternals.WorkflowSteps.Add(1L, metricTags)
            OpenTelemetryRunObserverInternals.WorkflowStepDuration.Record(duration, metricTags)
        | _ -> ()

        if isNull failure then
            OpenTelemetryRunObserverInternals.applySuccessTags state.Activity
        else
            OpenTelemetryRunObserverInternals.applyFailureTags state.Activity failure

        if not (isNull state.Activity) then
            state.Activity.Stop()

    let stopOutstandingOperations (event: RunEventEnvelope) (runState: OpenTelemetryRunObserverInternals.RunState) =
        for KeyValue(operationId, state) in runState.Operations do
            if runState.Operations.TryRemove(operationId) |> fst then
                stopOperation event.Timestamp event.Failure state

    interface IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            match event.Kind with
            | AgentRunEventKind.RunStarted ->
                let rootActivity =
                    TelemetryContracts.ActivitySource.StartActivity(event.OperationName, ActivityKind.Internal)

                if not (isNull rootActivity) then
                    OpenTelemetryRunObserverInternals.applyCommonTraceTags rootActivity event

                    match
                        OpenTelemetryRunObserverInternals.captureSensitive options options.CapturePrompt event.Prompt
                    with
                    | ValueSome prompt ->
                        rootActivity.SetTag(OpenTelemetryRunObserverInternals.PromptTag, prompt)
                        |> ignore
                    | ValueNone -> ()

                    match
                        OpenTelemetryRunObserverInternals.captureSensitive options options.CaptureInput event.Input
                    with
                    | ValueSome input ->
                        rootActivity.SetTag(OpenTelemetryRunObserverInternals.InputTag, input) |> ignore
                    | ValueNone -> ()

                let startMetricTags =
                    OpenTelemetryRunObserverInternals.createMetricTags
                        (OpenTelemetryRunObserverInternals.createMetricTagTemplate event)
                        "in_progress"

                OpenTelemetryRunObserverInternals.RunsActive.Add(1L, startMetricTags)

                let startedAt =
                    if event.StartedAt.HasValue then
                        event.StartedAt.Value
                    else
                        event.Timestamp

                states[event.RunId] <-
                    { RootActivity = rootActivity
                      StartedAt = startedAt
                      StartMetricTags = startMetricTags
                      Operations = ConcurrentDictionary<string, OpenTelemetryRunObserverInternals.OperationState>() }

                ValueTask()

            | AgentRunEventKind.ToolStarted
            | AgentRunEventKind.StepStarted ->
                match states.TryGetValue(event.RunId) with
                | true, runState ->
                    let parentContext =
                        if isNull runState.RootActivity then
                            ActivityContext()
                        else
                            runState.RootActivity.Context

                    let activity =
                        TelemetryContracts.ActivitySource.StartActivity(
                            event.OperationName,
                            ActivityKind.Internal,
                            parentContext
                        )

                    if not (isNull activity) then
                        OpenTelemetryRunObserverInternals.applyCommonTraceTags activity event

                        if event.Kind = AgentRunEventKind.ToolStarted then
                            match
                                OpenTelemetryRunObserverInternals.captureSensitive
                                    options
                                    options.CaptureToolArguments
                                    event.ToolArguments
                            with
                            | ValueSome arguments ->
                                activity.SetTag(OpenTelemetryRunObserverInternals.ToolArgumentsTag, arguments)
                                |> ignore
                            | ValueNone -> ()

                    runState.Operations[event.OperationId] <-
                        { Activity = activity
                          StartedAt = event.Timestamp
                          OperationKind = event.OperationKind
                          MetricTagTemplate = OpenTelemetryRunObserverInternals.createMetricTagTemplate event }

                    ValueTask()
                | _ -> ValueTask()

            | AgentRunEventKind.ToolCompleted
            | AgentRunEventKind.StepCompleted ->
                match states.TryGetValue(event.RunId) with
                | true, runState ->
                    match runState.Operations.TryRemove(event.OperationId) with
                    | true, state -> stopOperation event.Timestamp event.Failure state
                    | _ -> ()

                    ValueTask()
                | _ -> ValueTask()

            | AgentRunEventKind.ApprovalRequested ->
                let metricTags =
                    OpenTelemetryRunObserverInternals.createMetricTags
                        (OpenTelemetryRunObserverInternals.createMetricTagTemplate event)
                        "requested"

                OpenTelemetryRunObserverInternals.ApprovalsRequested.Add(1L, metricTags)
                ValueTask()

            | AgentRunEventKind.RunCompleted
            | AgentRunEventKind.RunFailed ->
                match states.TryRemove(event.RunId) with
                | true, runState ->
                    stopOutstandingOperations event runState

                    let duration = max 0.0 (event.Timestamp - runState.StartedAt).TotalSeconds
                    let status = OpenTelemetryRunObserverInternals.statusFromFailure event.Failure

                    let metricTags =
                        OpenTelemetryRunObserverInternals.createMetricTags
                            (OpenTelemetryRunObserverInternals.createMetricTagTemplate event)
                            status

                    OpenTelemetryRunObserverInternals.Runs.Add(1L, metricTags)
                    OpenTelemetryRunObserverInternals.RunDuration.Record(duration, metricTags)
                    OpenTelemetryRunObserverInternals.RunsActive.Add(-1L, runState.StartMetricTags)

                    if not (isNull event.Failure) && event.Failure.Code = AgentFailureCode.Validation then
                        OpenTelemetryRunObserverInternals.ValidationFailures.Add(1L, metricTags)

                    if event.Repaired then
                        OpenTelemetryRunObserverInternals.StructuredOutputRepairs.Add(1L, metricTags)

                    if not (isNull runState.RootActivity) then
                        match
                            OpenTelemetryRunObserverInternals.captureSensitive
                                options
                                options.CaptureOutput
                                event.Output
                        with
                        | ValueSome output ->
                            runState.RootActivity.SetTag(OpenTelemetryRunObserverInternals.OutputTag, output)
                            |> ignore
                        | ValueNone -> ()

                        if isNull event.Failure then
                            OpenTelemetryRunObserverInternals.applySuccessTags runState.RootActivity
                        else
                            OpenTelemetryRunObserverInternals.applyFailureTags runState.RootActivity event.Failure

                        runState.RootActivity.Stop()

                    ValueTask()
                | _ -> ValueTask()

            | _ -> ValueTask()
