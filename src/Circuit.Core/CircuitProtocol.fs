namespace Circuit.Core

open System
open System.Buffers
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Describes immutable run identity and definition correlation.
[<Sealed>]
type RunInfo
    (
        runId: RunId,
        lineageId: string,
        definitionId: DefinitionId,
        definitionVersion: SemanticVersion,
        fingerprint: string,
        startedAt: DateTimeOffset
    ) =
    /// <summary>Gets the run id value.</summary>
    member _.RunId = runId
    /// <summary>Gets the lineage id value.</summary>
    member _.LineageId = lineageId
    /// <summary>Gets the definition id value.</summary>
    member _.DefinitionId = definitionId
    /// <summary>Gets the definition version value.</summary>
    member _.DefinitionVersion = definitionVersion
    /// <summary>Gets the fingerprint value.</summary>
    member _.Fingerprint = fingerprint
    /// <summary>Gets the started at value.</summary>
    member _.StartedAt = startedAt

/// Describes one graph node evaluation without exposing its payload.
[<Sealed>]
type NodeInfo(nodeId: string, nodePath: string, itemKey: ItemKey voption, attempt: int, timestamp: DateTimeOffset) =
    /// <summary>Gets the node id value.</summary>
    member _.NodeId = nodeId
    /// <summary>Gets the node path value.</summary>
    member _.NodePath = nodePath
    /// <summary>Gets the item key value.</summary>
    member _.ItemKey = itemKey
    /// <summary>Gets the attempt value.</summary>
    member _.Attempt = attempt
    /// <summary>Gets the timestamp value.</summary>
    member _.Timestamp = timestamp

/// Carries observational provider progress that never triggers downstream work.
[<Sealed>]
type CircuitOutputDelta(nodePath: string, itemKey: ItemKey voption, text: string, timestamp: DateTimeOffset) =
    /// <summary>Gets the node path value.</summary>
    member _.NodePath = nodePath
    /// <summary>Gets the item key value.</summary>
    member _.ItemKey = itemKey
    /// <summary>Gets the text value.</summary>
    member _.Text = text
    /// <summary>Gets the timestamp value.</summary>
    member _.Timestamp = timestamp

/// Describes a completed node response without exposing its typed payload.
[<Sealed>]
type UntypedResponse(isSuccess: bool, failure: CircuitFailure voption, metadata: ResponseMetadata) =
    /// Gets whether the untyped node response succeeded.
    member _.IsSuccess = isSuccess
    /// <summary>Gets the failure value.</summary>
    member _.Failure = failure
    /// <summary>Gets the metadata value.</summary>
    member _.Metadata = metadata

/// Summarizes a completed Circuit run.
[<Sealed>]
type RunSummary
    (
        outputCount: int,
        succeededCount: int,
        failedCount: int,
        usage: RunUsage,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset
    ) =
    /// <summary>Gets the output count value.</summary>
    member _.OutputCount = outputCount
    /// <summary>Gets the succeeded count value.</summary>
    member _.SucceededCount = succeededCount
    /// <summary>Gets the failed count value.</summary>
    member _.FailedCount = failedCount
    /// <summary>Gets the usage value.</summary>
    member _.Usage = usage
    /// <summary>Gets the started at value.</summary>
    member _.StartedAt = startedAt
    /// <summary>Gets the completed at value.</summary>
    member _.CompletedAt = completedAt

/// Represents one event from the unified Circuit execution kernel.
type CircuitEvent<'Output> =
    /// The scheduler accepted the run.
    | RunStarted of RunInfo
    /// A graph node started.
    | NodeStarted of NodeInfo
    /// Observational provider text was emitted.
    | OutputDelta of CircuitOutputDelta
    /// A completed root response was produced.
    | OutputProduced of ItemKey voption * Response<'Output>
    /// One lane requires a matching host response.
    | ApprovalRequested of ApprovalRequest
    /// A graph node completed.
    | NodeCompleted of NodeInfo * UntypedResponse
    /// The run emitted its sole terminal event.
    | RunCompleted of Response<RunSummary>

/// Represents an opaque resumable checkpoint for an exact Circuit definition.
[<Sealed>]
type CircuitCheckpoint<'Output>
    internal
    (
        definitionId: DefinitionId,
        definitionVersion: SemanticVersion,
        fingerprint: string,
        lineageId: string,
        createdAt: DateTimeOffset,
        payload: JsonElement,
        options: RunOptions voption
    ) =
    let payload = payload.Clone()

    /// <summary>Gets the definition id value.</summary>
    member _.DefinitionId = definitionId
    /// <summary>Gets the definition version value.</summary>
    member _.DefinitionVersion = definitionVersion
    /// <summary>Gets the fingerprint value.</summary>
    member _.Fingerprint = fingerprint
    /// <summary>Gets the lineage id value.</summary>
    member _.LineageId = lineageId
    /// <summary>Gets the created at value.</summary>
    member _.CreatedAt = createdAt.ToUniversalTime()
    member internal _.Payload = payload.Clone()
    member internal _.Options = options

    /// Serializes this checkpoint into a process-portable JSON envelope.
    member this.Serialize() =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteNumber("formatVersion", 1)
        writer.WriteString("definitionId", definitionId.Value)
        writer.WriteString("definitionVersion", definitionVersion.ToString())
        writer.WriteString("fingerprint", fingerprint)
        writer.WriteString("lineageId", lineageId)
        writer.WriteString("createdAt", this.CreatedAt)
        writer.WritePropertyName("payload")
        payload.WriteTo(writer)
        writer.WriteEndObject()
        writer.Flush()
        use document = JsonDocument.Parse(buffer.WrittenMemory)
        document.RootElement.Clone()

    /// Validates and deserializes a process-portable checkpoint envelope.
    static member Deserialize(state: JsonElement) =
        let malformed message = invalidArg "state" message

        if state.ValueKind <> JsonValueKind.Object then
            malformed "Checkpoint state must be a JSON object."

        let property (name: string) kind =
            let mutable value = Unchecked.defaultof<JsonElement>

            if not (state.TryGetProperty(name, &value)) || value.ValueKind <> kind then
                malformed $"Checkpoint envelope property '{name}' is missing or has the wrong JSON kind."

            value

        let format = property "formatVersion" JsonValueKind.Number

        if format.GetInt32() <> 1 then
            raise (ArgumentOutOfRangeException("state", "Unsupported checkpoint format version."))

        let id =
            DefinitionId.Create((property "definitionId" JsonValueKind.String).GetString())

        let version =
            SemanticVersion.Parse((property "definitionVersion" JsonValueKind.String).GetString())

        let fingerprint = (property "fingerprint" JsonValueKind.String).GetString()
        let lineage = (property "lineageId" JsonValueKind.String).GetString()
        let created = (property "createdAt" JsonValueKind.String).GetDateTimeOffset()
        let payload = property "payload" JsonValueKind.Object
        CircuitCheckpoint<'Output>(id, version, fingerprint, lineage, created, payload, ValueNone)

/// Owns a live Circuit event stream, approvals, checkpoint coordination, and bounded background work.
[<Sealed>]
type CircuitRun<'Output>
    internal
    (
        runId: RunId,
        events: IAsyncEnumerable<CircuitEvent<'Output>>,
        respondAsync: ApprovalResponse * CancellationToken -> ValueTask<Response<unit>>,
        checkpointAsync: CancellationToken -> ValueTask<Response<CircuitCheckpoint<'Output>>>,
        disposeAsync: unit -> ValueTask
    ) =
    let mutable disposed = 0

    let throwIfDisposed () =
        if Volatile.Read(&disposed) <> 0 then
            raise (ObjectDisposedException("CircuitRun"))

    /// <summary>Gets the run id value.</summary>
    member _.RunId = runId

    /// <summary>Gets the events value.</summary>
    member _.Events =
        throwIfDisposed ()
        events

    /// Atomically accepts one matching, pending approval decision.
    member _.RespondAsync(response: ApprovalResponse, cancellationToken: CancellationToken) =
        throwIfDisposed ()

        if isNull response then
            nullArg "response"

        respondAsync (response, cancellationToken)

    /// Captures one atomic durable cut of the active scheduler state.
    member _.CreateCheckpointAsync(cancellationToken: CancellationToken) =
        throwIfDisposed ()
        checkpointAsync cancellationToken

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            if Interlocked.CompareExchange(&disposed, 1, 0) = 0 then
                disposeAsync ()
            else
                ValueTask()
