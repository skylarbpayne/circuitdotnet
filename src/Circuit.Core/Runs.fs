namespace Circuit.Core

open System
open System.Collections.Frozen
open System.Collections.Generic

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

    member internal _.AdapterId = adapterId
    member internal _.DefinitionFingerprint = definitionFingerprint
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
        services: IServiceProvider
    ) =
    do
        if isNull services then
            nullArg "services"

        if isNull tags then
            nullArg "tags"

    /// Gets the session to continue, if any.
    member _.Session = session

    /// Gets the tenant identifier associated with the run, if any.
    member _.TenantId = tenantId

    /// Gets the user identifier associated with the run, if any.
    member _.UserId = userId

    /// Gets arbitrary caller-supplied tags.
    member _.Tags = tags

    /// Gets the structured-output policy requested for the run.
    member _.StructuredOutputPolicy = structuredOutputPolicy

    /// Gets the sensitive-data handling preference for observers.
    member _.SensitiveDataMode = sensitiveDataMode

    /// Gets the ambient service provider available to resolvers, tools, and skills.
    member _.Services = services

    /// Gets the default run options.
    static member Default =
        RunOptions(
            ValueNone,
            ValueNone,
            ValueNone,
            RunValidation.copyTags Array.empty<KeyValuePair<string, string>>,
            StructuredOutputPolicy.NativeOnly,
            SensitiveDataMode.Standard,
            EmptyServiceProvider.Instance
        )

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

/// Describes a pending approval request emitted by a run.
/// <remarks>
/// <see cref="P:Circuit.Core.ApprovalRequest.ArgumentsJson" /> may be omitted when the runtime cannot safely serialize
/// arguments or intentionally withholds them from observers.
/// </remarks>
[<Sealed>]
type ApprovalRequest internal (requestId: string, toolName: string, argumentsJson: string voption) =
    do
        if String.IsNullOrWhiteSpace requestId then
            invalidArg "requestId" "requestId cannot be blank."

        if String.IsNullOrWhiteSpace toolName then
            invalidArg "toolName" "toolName cannot be blank."

    /// Gets the runtime-generated approval request identifier.
    member _.RequestId = requestId

    /// Gets the tool name that triggered the approval.
    member _.ToolName = toolName

    /// Gets the serialized tool arguments when they are available.
    member _.ArgumentsJson = argumentsJson

/// Identifies the kind of streaming event emitted during a run.
type RunEventKind =
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
type RunEvent<'T>
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
