namespace Circuit.Testing

open System
open System.Collections.Generic
open Circuit
open Circuit.Core

module internal AssertionHelpers =
    let invalidAssertion message = invalidOp message

    let requireEvents name (events: IEnumerable<'T>) =
        if isNull events then
            nullArg name

        events |> Seq.toArray

    let isTerminalKind =
        function
        | RunEventKind.RunCompleted
        | RunEventKind.RunFailed -> true
        | _ -> false

    let isTerminalEnvelopeKind =
        function
        | AgentRunEventKind.RunCompleted
        | AgentRunEventKind.RunFailed -> true
        | _ -> false

/// Assertion helpers for Circuit test event streams.
[<AbstractClass; Sealed>]
type RunAssertions private () =
    /// Verifies the number of terminal core run events.
    static member AssertTerminalEventCount<'T>(events: IEnumerable<RunEvent<'T>>, expectedCount: int) =
        let snapshot = AssertionHelpers.requireEvents "events" events

        let actualCount =
            snapshot
            |> Array.sumBy (fun event -> if AssertionHelpers.isTerminalKind event.Kind then 1 else 0)

        if actualCount <> expectedCount then
            AssertionHelpers.invalidAssertion (
                $"Expected {expectedCount} terminal run event(s) but found {actualCount}."
            )

    /// Verifies the number of terminal façade run events.
    static member AssertTerminalEventCount<'T>(events: IEnumerable<AgentRunEvent<'T>>, expectedCount: int) =
        let snapshot = AssertionHelpers.requireEvents "events" events

        let actualCount =
            snapshot
            |> Array.sumBy (fun event ->
                if AssertionHelpers.isTerminalEnvelopeKind event.Kind then
                    1
                else
                    0)

        if actualCount <> expectedCount then
            AssertionHelpers.invalidAssertion (
                $"Expected {expectedCount} terminal run event(s) but found {actualCount}."
            )

    /// Verifies the number of terminal observer events.
    static member AssertTerminalEventCount(events: IEnumerable<RecordedObserverEvent>, expectedCount: int) =
        let snapshot = AssertionHelpers.requireEvents "events" events

        let actualCount =
            snapshot
            |> Array.sumBy (fun event ->
                if AssertionHelpers.isTerminalEnvelopeKind event.Kind then
                    1
                else
                    0)

        if actualCount <> expectedCount then
            AssertionHelpers.invalidAssertion (
                $"Expected {expectedCount} terminal observer event(s) but found {actualCount}."
            )

    /// Verifies that core run events use contiguous zero-based sequence numbers.
    static member AssertMonotonicSequence<'T>(events: IEnumerable<RunEvent<'T>>) =
        let snapshot = AssertionHelpers.requireEvents "events" events

        snapshot
        |> Array.iteri (fun index event ->
            let expected = int64 index

            if event.Sequence <> expected then
                AssertionHelpers.invalidAssertion (
                    $"Expected event sequence {expected} at index {index}, but found {event.Sequence}."
                ))

    /// Verifies that façade run events use contiguous zero-based sequence numbers.
    static member AssertMonotonicSequence<'T>(events: IEnumerable<AgentRunEvent<'T>>) =
        let snapshot = AssertionHelpers.requireEvents "events" events

        snapshot
        |> Array.iteri (fun index event ->
            let expected = int64 index

            if event.Sequence <> expected then
                AssertionHelpers.invalidAssertion (
                    $"Expected event sequence {expected} at index {index}, but found {event.Sequence}."
                ))

    /// Verifies the order in which observer operations first appeared.
    static member AssertOperationOrder
        (events: IEnumerable<RecordedObserverEvent>, [<ParamArray>] expectedOperationNames: string[])
        =
        let snapshot = AssertionHelpers.requireEvents "events" events

        if isNull expectedOperationNames then
            nullArg "expectedOperationNames"

        if expectedOperationNames |> Array.exists String.IsNullOrWhiteSpace then
            invalidArg "expectedOperationNames" "Expected operation names cannot contain blank entries."

        let seen = HashSet<string>(StringComparer.Ordinal)

        let actualOperationNames =
            snapshot
            |> Array.choose (fun event ->
                if
                    event.OperationKind = RunOperationKind.Run
                    || String.IsNullOrWhiteSpace event.OperationId
                    || not (seen.Add event.OperationId)
                then
                    None
                else
                    Some event.OperationName)

        if actualOperationNames <> expectedOperationNames then
            let actual = String.Join(", ", actualOperationNames)
            let expected = String.Join(", ", expectedOperationNames)

            AssertionHelpers.invalidAssertion ($"Expected operation order [{expected}] but found [{actual}].")
