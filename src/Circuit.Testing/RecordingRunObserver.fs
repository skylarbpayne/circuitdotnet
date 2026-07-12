namespace Circuit.Testing

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Circuit

/// Represents one observer event captured by <see cref="T:Circuit.Testing.RecordingRunObserver" />.
[<Sealed>]
type RecordedObserverEvent
    internal
    (
        runId: string,
        timestamp: DateTimeOffset,
        kind: AgentRunEventKind,
        operationId: string,
        operationName: string,
        operationKind: RunOperationKind,
        failureCode: string,
        approvalRequestId: string,
        repaired: bool
    ) =
    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the event timestamp in UTC.
    member _.Timestamp = timestamp

    /// Gets the observer event kind.
    member _.Kind = kind

    /// Gets the associated operation identifier when one exists.
    member _.OperationId = operationId

    /// Gets the associated operation name.
    member _.OperationName = operationName

    /// Gets the operation kind.
    member _.OperationKind = operationKind

    /// Gets the failure code for failed events, or an empty string otherwise.
    member _.FailureCode = failureCode

    /// Gets the approval request identifier for approval events, or an empty string otherwise.
    member _.ApprovalRequestId = approvalRequestId

    /// Gets whether a structured-output repair pass was used for the run.
    member _.Repaired = repaired

module internal RecordingRunObserverInternals =
    let fromEnvelope (event: RunEventEnvelope) =
        RecordedObserverEvent(
            event.RunId,
            event.Timestamp,
            event.Kind,
            event.OperationId,
            event.OperationName,
            event.OperationKind,
            (if isNull event.Failure then
                 String.Empty
             else
                 string event.Failure.Code),
            (if isNull event.Approval then
                 String.Empty
             else
                 event.Approval.RequestId),
            event.Repaired
        )

/// Captures observer events in memory for assertions.
[<Sealed>]
type RecordingRunObserver() =
    let events = ConcurrentQueue<RecordedObserverEvent>()

    /// Gets the recorded events in insertion order.
    member _.Events = events.ToArray() :> IReadOnlyList<RecordedObserverEvent>

    /// Removes all recorded events.
    member _.Clear() =
        let mutable removed = Unchecked.defaultof<RecordedObserverEvent>

        while events.TryDequeue(&removed) do
            ()

    interface IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            if isNull event then
                nullArg "event"

            events.Enqueue(RecordingRunObserverInternals.fromEnvelope event)
            ValueTask()
