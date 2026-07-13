namespace Circuit.Core

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Threading
open System.Threading.Tasks

/// Controls how strictly a runtime must obtain native structured output from its provider.
type StructuredOutputPolicy =
    /// Require the primary provider call to satisfy the output contract without any repair pass.
    | NativeOnly = 0
    /// Allow a secondary model pass to repair otherwise-decodable output when the runtime supports it.
    | AllowSecondaryModelRepair = 1

/// Controls whether observers should treat prompts, inputs, outputs, and tool arguments as sensitive.
type SensitiveDataMode =
    /// Expose data normally unless an observer applies its own redaction policy.
    | Standard = 0
    /// Prefer redaction or omission for sensitive payloads where the runtime and observers support it.
    | Redact = 1

/// Represents runtime-owned conversational or provider state that can be carried between runs.
/// <remarks>
/// Session payloads are opaque to callers. Persist them only through
/// <see cref="M:Circuit.Core.ICircuitRuntime.SerializeSessionAsync(Circuit.Core.AgentDefinition,Circuit.Core.CircuitSession,System.Threading.CancellationToken)" />
/// and recreate them only through
/// <see cref="M:Circuit.Core.ICircuitRuntime.DeserializeSessionAsync(Circuit.Core.AgentDefinition,System.Text.Json.JsonElement,System.Threading.CancellationToken)" />.
/// </remarks>
[<Sealed>]
type CircuitSession
    internal
    (
        id: string,
        metadata: IReadOnlyDictionary<string, string>,
        adapterId: string voption,
        definitionFingerprint: string voption,
        providerSession: obj voption
    ) =
    /// Gets the runtime-defined session identifier.
    member _.Id = id

    /// Gets caller-visible session metadata.
    member _.Metadata = metadata

    /// <summary>Gets the internal value.</summary>
    member internal _.AdapterId = adapterId
    /// <summary>Gets the internal value.</summary>
    member internal _.DefinitionFingerprint = definitionFingerprint
    /// <summary>Gets the internal value.</summary>
    member internal _.ProviderSession = providerSession

/// Reports provider token usage for a completed run.
/// <remarks>Values are provider-reported and may be approximate.</remarks>
[<Sealed>]
type RunUsage internal (inputTokens: int, outputTokens: int) =
    /// Gets the input token count.
    member _.InputTokens = inputTokens

    /// Gets the output token count.
    member _.OutputTokens = outputTokens

    /// Gets the total token count.
    member _.TotalTokens = inputTokens + outputTokens

type private EmptyServiceProvider private () =
    interface IServiceProvider with
        member _.GetService(_serviceType) = null

    static member Instance = EmptyServiceProvider() :> IServiceProvider

module private RunValidation =
    let copyTags (tags: seq<KeyValuePair<string, string>>) =
        let entries = tags |> Seq.toArray

        if entries.Length > 32 then
            invalidArg "tags" "No more than 32 tags are allowed."

        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for entry in entries do
            if String.IsNullOrWhiteSpace entry.Key then
                invalidArg "tags" "Tag keys cannot be blank."

            if entry.Key.Length > 64 then
                invalidArg "tags" "Tag keys must be 64 characters or fewer."

            if entry.Key.StartsWith("circuit.", StringComparison.Ordinal) then
                invalidArg "tags" "Tag keys beginning with 'circuit.' are reserved."

            if isNull entry.Value then
                nullArg "tags"

            if entry.Value.Length > 256 then
                invalidArg "tags" "Tag values must be 256 characters or fewer."

            if dictionary.ContainsKey entry.Key then
                invalidArg "tags" "Duplicate tag keys are not allowed."

            dictionary.Add(entry.Key, entry.Value)

        dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

/// Supplies optional execution settings for a single run.
/// <remarks>
/// The runtime snapshots these options when a run starts. Tags are telemetry hints, not authorization controls.
/// Services are ambient process-local dependencies and are never serialized into sessions or checkpoints.
/// </remarks>
[<Sealed>]
type RunOptions
    internal
    (
        session: CircuitSession voption,
        tenantId: string voption,
        userId: string voption,
        tags: IReadOnlyDictionary<string, string>,
        structuredOutputPolicy: StructuredOutputPolicy,
        sensitiveDataMode: SensitiveDataMode,
        services: IServiceProvider,
        maxConcurrency: int,
        eventBufferCapacity: int,
        maxDynamicDepth: int,
        maxDynamicNodes: int,
        maxApprovalRounds: int,
        maxSourcePageSize: int,
        maxSourcePages: int,
        maxCheckpointBytes: int,
        disposalDrainTimeout: TimeSpan
    ) =
    do
        if isNull services then
            nullArg "services"

        if isNull tags then
            nullArg "tags"

        if maxConcurrency < 1 then
            invalidArg "maxConcurrency" "maxConcurrency must be at least 1."

        if eventBufferCapacity < 1 then
            invalidArg "eventBufferCapacity" "eventBufferCapacity must be at least 1."

        if maxDynamicDepth < 1 then
            invalidArg "maxDynamicDepth" "maxDynamicDepth must be at least 1."

        if maxDynamicNodes < 1 then
            invalidArg "maxDynamicNodes" "maxDynamicNodes must be at least 1."

        if maxApprovalRounds < 1 then
            invalidArg "maxApprovalRounds" "maxApprovalRounds must be at least 1."

        if maxSourcePageSize < 1 then
            invalidArg "maxSourcePageSize" "maxSourcePageSize must be at least 1."

        if maxSourcePages < 1 then
            invalidArg "maxSourcePages" "maxSourcePages must be at least 1."

        if maxCheckpointBytes < 1024 then
            invalidArg "maxCheckpointBytes" "maxCheckpointBytes must be at least 1024."

        if
            disposalDrainTimeout < TimeSpan.Zero
            && disposalDrainTimeout <> Timeout.InfiniteTimeSpan
        then
            invalidArg "disposalDrainTimeout" "disposalDrainTimeout must be non-negative or infinite."

    new(session, tenantId, userId, tags, structuredOutputPolicy, sensitiveDataMode, services) =
        RunOptions(
            session,
            tenantId,
            userId,
            tags,
            structuredOutputPolicy,
            sensitiveDataMode,
            services,
            8,
            128,
            16,
            1024,
            16,
            256,
            1024,
            4 * 1024 * 1024,
            TimeSpan.FromSeconds 5.0
        )

    /// <summary>Gets the session value.</summary>
    member _.Session = session
    /// <summary>Gets the tenant id value.</summary>
    member _.TenantId = tenantId
    /// <summary>Gets the user id value.</summary>
    member _.UserId = userId
    /// <summary>Gets the tags value.</summary>
    member _.Tags = tags
    /// <summary>Gets the structured output policy value.</summary>
    member _.StructuredOutputPolicy = structuredOutputPolicy
    /// <summary>Gets the sensitive data mode value.</summary>
    member _.SensitiveDataMode = sensitiveDataMode
    /// <summary>Gets the services value.</summary>
    member _.Services = services
    /// <summary>Gets the max concurrency value.</summary>
    member _.MaxConcurrency = maxConcurrency
    /// <summary>Gets the event buffer capacity value.</summary>
    member _.EventBufferCapacity = eventBufferCapacity
    /// <summary>Gets the max dynamic depth value.</summary>
    member _.MaxDynamicDepth = maxDynamicDepth
    /// <summary>Gets the max dynamic nodes value.</summary>
    member _.MaxDynamicNodes = maxDynamicNodes
    /// <summary>Gets the max approval rounds value.</summary>
    member _.MaxApprovalRounds = maxApprovalRounds
    /// <summary>Gets the max source page size value.</summary>
    member _.MaxSourcePageSize = maxSourcePageSize
    /// Gets the maximum number of pages a resumable source may read across one checkpoint lineage.
    member _.MaxSourcePages = maxSourcePages
    /// <summary>Gets the max checkpoint bytes value.</summary>
    member _.MaxCheckpointBytes = maxCheckpointBytes
    /// <summary>Gets the disposal drain timeout value.</summary>
    member _.DisposalDrainTimeout = disposalDrainTimeout

    /// <summary>Gets the private value.</summary>
    member private _.Copy
        (
            ?newSession,
            ?policy,
            ?concurrency,
            ?eventCapacity,
            ?dynamicDepth,
            ?dynamicNodes,
            ?approvalRounds,
            ?sourcePageSize,
            ?sourcePages,
            ?checkpointBytes,
            ?drainTimeout
        ) =
        RunOptions(
            defaultArg newSession session,
            tenantId,
            userId,
            tags,
            defaultArg policy structuredOutputPolicy,
            sensitiveDataMode,
            services,
            defaultArg concurrency maxConcurrency,
            defaultArg eventCapacity eventBufferCapacity,
            defaultArg dynamicDepth maxDynamicDepth,
            defaultArg dynamicNodes maxDynamicNodes,
            defaultArg approvalRounds maxApprovalRounds,
            defaultArg sourcePageSize maxSourcePageSize,
            defaultArg sourcePages maxSourcePages,
            defaultArg checkpointBytes maxCheckpointBytes,
            defaultArg drainTimeout disposalDrainTimeout
        )

    /// <summary>Gets the with session value.</summary>
    member this.WithSession(value: CircuitSession) =
        if isNull (box value) then
            nullArg "session"

        this.Copy(newSession = ValueSome value)

    /// <summary>Gets the with structured output policy value.</summary>
    member this.WithStructuredOutputPolicy(value: StructuredOutputPolicy) =
        match value with
        | StructuredOutputPolicy.NativeOnly
        | StructuredOutputPolicy.AllowSecondaryModelRepair -> this.Copy(policy = value)
        | _ -> raise (ArgumentOutOfRangeException("policy", value, "Unsupported structured-output policy."))

    /// <summary>Gets the with max concurrency value.</summary>
    member this.WithMaxConcurrency(value: int) = this.Copy(concurrency = value)
    /// <summary>Gets the with event buffer capacity value.</summary>
    member this.WithEventBufferCapacity(value: int) = this.Copy(eventCapacity = value)

    /// <summary>Gets the with limits value.</summary>
    member this.WithLimits(maximumDynamicNodes: int, maximumApprovalRounds: int) =
        this.Copy(dynamicNodes = maximumDynamicNodes, approvalRounds = maximumApprovalRounds)

    /// <summary>Gets the with limits value.</summary>
    member this.WithLimits
        (maximumDynamicDepth: int, maximumDynamicNodes: int, maximumApprovalRounds: int, maximumSourcePageSize: int)
        =
        this.Copy(
            dynamicDepth = maximumDynamicDepth,
            dynamicNodes = maximumDynamicNodes,
            approvalRounds = maximumApprovalRounds,
            sourcePageSize = maximumSourcePageSize
        )

    /// Creates a copy with all dynamic, approval, and resumable-source bounds replaced.
    member this.WithLimits
        (
            maximumDynamicDepth: int,
            maximumDynamicNodes: int,
            maximumApprovalRounds: int,
            maximumSourcePageSize: int,
            maximumSourcePages: int
        ) =
        this.Copy(
            dynamicDepth = maximumDynamicDepth,
            dynamicNodes = maximumDynamicNodes,
            approvalRounds = maximumApprovalRounds,
            sourcePageSize = maximumSourcePageSize,
            sourcePages = maximumSourcePages
        )

    /// <summary>Gets the with max checkpoint bytes value.</summary>
    member this.WithMaxCheckpointBytes(value: int) = this.Copy(checkpointBytes = value)
    /// <summary>Gets the with disposal drain timeout value.</summary>
    member this.WithDisposalDrainTimeout(value: TimeSpan) = this.Copy(drainTimeout = value)

    /// <summary>Gets the default value.</summary>
    static member Default =
        RunOptions(
            ValueNone,
            ValueNone,
            ValueNone,
            RunValidation.copyTags Array.empty<KeyValuePair<string, string>>,
            StructuredOutputPolicy.NativeOnly,
            SensitiveDataMode.Standard,
            EmptyServiceProvider.Instance,
            8,
            128,
            16,
            1024,
            16,
            256,
            1024,
            4 * 1024 * 1024,
            TimeSpan.FromSeconds 5.0
        )

/// Supplies process-local dependencies when resuming a serialized checkpoint.
/// <remarks>Services are never serialized; the receiving process must explicitly rebind them.</remarks>
[<Sealed>]
type ResumeOptions(services: IServiceProvider) =
    do
        if isNull services then
            nullArg "services"

    /// Gets the process-local service provider used by resumed code and tool nodes.
    member _.Services = services

    /// Creates resume options with an empty service provider.
    static member Default = ResumeOptions(EmptyServiceProvider.Instance)

/// Captures the final outcome of a completed run.
[<Sealed>]
type RunResult<'T>
    internal
    (
        runId: RunId,
        result: CircuitResult<'T>,
        usage: RunUsage,
        session: CircuitSession voption,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset
    ) =
    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the success or failure payload.
    member _.Result = result

    /// Gets provider-reported usage.
    member _.Usage = usage

    /// Gets the resulting session state when the runtime produced one.
    member _.Session = session

    /// Gets the run start time in UTC.
    member _.StartedAt = startedAt

    /// Gets the run completion time in UTC.
    member _.CompletedAt = completedAt

/// Describes a graph approval prompt and its immutable host-routing metadata.
[<AllowNullLiteral; Sealed>]
type ApprovalPrompt(title: string, message: string, metadata: IEnumerable<KeyValuePair<string, string>>) =
    let frozen =
        if isNull metadata then
            nullArg "metadata"

        let values = Dictionary<string, string>(StringComparer.Ordinal)

        for item in metadata do
            if String.IsNullOrWhiteSpace item.Key then
                invalidArg "metadata" "Metadata keys cannot be blank."

            if isNull item.Value then
                nullArg "metadata"

            if not (values.TryAdd(item.Key, item.Value)) then
                invalidArg "metadata" "Duplicate metadata keys are not allowed."

        ReadOnlyDictionary(values) :> IReadOnlyDictionary<string, string>

    do
        if String.IsNullOrWhiteSpace title then
            invalidArg "title" "title cannot be blank."

        if isNull message then
            nullArg "message"

    /// Gets the approval title shown to the host.
    member _.Title = title

    /// Gets the approval message shown to the host.
    member _.Message = message

    /// Gets immutable authorization, audit, or routing metadata.
    member _.Metadata = frozen

    /// Creates an approval prompt without routing metadata.
    static member Create(title, message) =
        ApprovalPrompt(title, message, Seq.empty)

/// Describes a pending approval request emitted by a run.
/// <remarks>
/// <see cref="P:Circuit.Core.ApprovalRequest.ArgumentsJson" /> may be omitted when the runtime cannot safely serialize
/// arguments or intentionally withholds them from observers.
/// </remarks>
[<Sealed>]
type ApprovalRequest
    internal (requestId: string, toolName: string, argumentsJson: string voption, prompt: ApprovalPrompt voption) =
    do
        if String.IsNullOrWhiteSpace requestId then
            invalidArg "requestId" "requestId cannot be blank."

        if String.IsNullOrWhiteSpace toolName then
            invalidArg "toolName" "toolName cannot be blank."

    internal new(requestId, toolName, argumentsJson) = ApprovalRequest(requestId, toolName, argumentsJson, ValueNone)

    /// Gets the runtime-generated approval request identifier.
    member _.RequestId = requestId

    /// Gets the tool name that triggered the approval.
    member _.ToolName = toolName

    /// Gets the serialized tool arguments when this is a provider-tool approval.
    member _.ArgumentsJson = argumentsJson

    /// Gets the complete graph approval prompt, including immutable metadata, when applicable.
    member _.Prompt = prompt

/// Represents the operator's response to an approval request.
[<AllowNullLiteral; Sealed>]
type ApprovalResponse(requestId: string, approved: bool, note: string) =
    do
        if String.IsNullOrWhiteSpace requestId then
            invalidArg "requestId" "requestId cannot be blank."

        if not (isNull note) && String.IsNullOrWhiteSpace note then
            invalidArg "note" "note cannot be blank when provided."

    /// Gets the approval request identifier being answered.
    member _.RequestId = requestId

    /// Gets whether the request was approved.
    member _.Approved = approved

    /// Gets the optional operator note.
    member _.Note = note

    /// Creates an approval response without a note.
    static member Create(requestId: string, approved: bool) =
        ApprovalResponse(requestId, approved, null)

/// Identifies the kind of streaming event emitted during a run.
type internal RunEventKind =
    /// The run has been accepted and assigned an identifier.
    | RunStarted = 0
    /// A text delta was emitted for the current output.
    | OutputDelta = 1
    /// A tool invocation started.
    | ToolStarted = 2
    /// A tool invocation completed.
    | ToolCompleted = 3
    /// The run paused and requires an approval response.
    | ApprovalRequested = 4
    /// A workflow step started.
    | StepStarted = 5
    /// A workflow step completed.
    | StepCompleted = 6
    /// A workflow emitted an intermediate typed value.
    | IntermediateOutput = 7
    /// The run completed successfully.
    | RunCompleted = 8
    /// The run terminated with a failure.
    | RunFailed = 9

/// Represents one event in a streaming run.
/// <remarks>
/// Only the members relevant to <see cref="P:Circuit.Core.RunEvent`1.Kind" /> are populated.
/// Exactly one terminal event is expected for a well-formed stream.
/// </remarks>
[<Sealed>]
type internal RunEvent<'T>
    internal
    (
        sequence: int64,
        runId: RunId,
        timestamp: DateTimeOffset,
        kind: RunEventKind,
        operationId: string voption,
        textDelta: string voption,
        value: 'T voption,
        failure: CircuitFailure voption,
        approval: ApprovalRequest voption
    ) =
    /// Gets the zero-based event sequence within the run.
    member _.Sequence = sequence

    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the event timestamp in UTC.
    member _.Timestamp = timestamp

    /// Gets the event kind.
    member _.Kind = kind

    /// Gets the tool or workflow operation identifier associated with the event when available.
    member _.OperationId = operationId

    /// Gets the streamed text delta for <see cref="F:Circuit.Core.RunEventKind.OutputDelta" /> events.
    member _.TextDelta = textDelta

    /// Gets the typed output payload carried by completion or intermediate-output events.
    member _.Value = value

    /// Gets the failure payload carried by failed terminal events.
    member _.Failure = failure

    /// Gets the approval payload carried by approval-requested events.
    member _.Approval = approval

    /// <summary>Gets the val value.</summary>
    member val internal RuntimeUsage = RunUsage(0, 0) with get, set
    /// <summary>Gets the val value.</summary>
    member val internal RuntimeSession: CircuitSession voption = ValueNone with get, set

/// Represents a live agent execution that may stream events and pause for approval.
/// <remarks>
/// This handle owns the lifetime of a paused run. Disposing an event enumerator does not dispose the run.
/// The event stream supports one active consumer at a time. Replacing an enumerator sequentially continues with unread
/// and future events; concurrent enumerators compete for events and are not a broadcast mechanism.
/// Disposing the handle invokes its disposal delegate at most once; that delegate is responsible for cancelling
/// or closing any event enumerators that were acquired before disposal. Accessing <see cref="P:Circuit.Core.AgentRun`1.Events" />
/// or calling <see cref="M:Circuit.Core.AgentRun`1.RespondAsync(Circuit.Core.ApprovalResponse,System.Threading.CancellationToken)" />
/// after disposal throws <see cref="T:System.ObjectDisposedException" />.
/// </remarks>
[<Sealed>]
type internal AgentRun<'Output>
    internal
    (
        runId: RunId,
        events: IAsyncEnumerable<RunEvent<'Output>>,
        respondAsync: ApprovalResponse * CancellationToken -> ValueTask,
        disposeAsync: unit -> ValueTask
    ) =
    let mutable disposed = 0

    do
        if String.IsNullOrWhiteSpace runId.Value then
            invalidArg "runId" "runId must be a valid run identifier."

        if isNull (box events) then
            nullArg "events"

        if isNull (box respondAsync) then
            nullArg "respondAsync"

        if isNull (box disposeAsync) then
            nullArg "disposeAsync"

    let throwIfDisposed () =
        if Volatile.Read(&disposed) <> 0 then
            raise (ObjectDisposedException("AgentRun"))

    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the event stream for the live agent run.
    /// <remarks>
    /// Disposing an event enumerator does not dispose the run handle. Use one active enumerator at a time; sequential
    /// replacement continues with unread and future events, while concurrent enumerators compete for events.
    /// Accessing this property after the handle is disposed throws <see cref="T:System.ObjectDisposedException" />.
    /// </remarks>
    member _.Events =
        throwIfDisposed ()
        events

    /// Responds to a pending approval request whose identifier matches the response.
    /// <remarks>
    /// A response requires a pending matching request; otherwise the runtime fails the operation. Calling this method
    /// after disposal throws <see cref="T:System.ObjectDisposedException" />.
    /// </remarks>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="response" /> is null.</exception>
    member _.RespondAsync(response: ApprovalResponse, cancellationToken) =
        throwIfDisposed ()

        if isNull response then
            nullArg "response"

        respondAsync (response, cancellationToken)

    /// Creates a live agent run handle from runtime-provided delegates.
    /// <param name="runId">The valid identifier assigned to the run.</param>
    /// <param name="events">The non-null event stream owned by the runtime.</param>
    /// <param name="respondAsync">The non-null delegate that forwards approval responses.</param>
    /// <param name="disposeAsync">The non-null delegate that releases the live run.</param>
    /// <exception cref="T:System.ArgumentException"><paramref name="runId" /> is not valid.</exception>
    /// <exception cref="T:System.ArgumentNullException">A stream or delegate argument is null.</exception>
    static member Create
        (
            runId: RunId,
            events: IAsyncEnumerable<RunEvent<'Output>>,
            respondAsync: Func<ApprovalResponse, CancellationToken, ValueTask>,
            disposeAsync: Func<ValueTask>
        ) =
        if isNull respondAsync then
            nullArg "respondAsync"

        if isNull disposeAsync then
            nullArg "disposeAsync"

        AgentRun<'Output>(
            runId,
            events,
            (fun (response, cancellationToken) -> respondAsync.Invoke(response, cancellationToken)),
            (fun () -> disposeAsync.Invoke())
        )

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            if Interlocked.CompareExchange(&disposed, 1, 0) = 0 then
                disposeAsync ()
            else
                ValueTask()
