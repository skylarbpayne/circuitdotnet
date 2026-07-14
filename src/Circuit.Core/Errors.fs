namespace Circuit.Core

open System
open System.Text.Json.Serialization

/// Classifies the high-level reason a Circuit run or graph operation failed.
type CircuitFailureCode =
    /// Input or output validation rejected data before the operation could continue.
    | Validation = 0
    /// The selected provider could not guarantee the requested structured-output contract.
    | StructuredOutputUnsupported = 1
    /// Provider output was received but could not be decoded into the requested contract.
    | Decode = 2
    /// The underlying provider call failed for reasons outside Circuit's contract validation.
    | Provider = 3
    /// A tool lookup, approval, invocation, or result validation step failed.
    | Tool = 4
    /// The run paused because a tool invocation required an explicit approval response.
    | ApprovalRequired = 5
    /// Skill resolution, file loading, resource access, or script execution failed.
    | Skill = 6
    /// Scheduler compilation, graph execution, or resume processing failed.
    | Engine = 7
    /// A checkpoint could not be resumed because it no longer matched the target Circuit definition.
    | CheckpointMismatch = 8
    /// Cancellation was requested before the operation could complete successfully.
    | Cancelled = 9
    /// The Circuit or source cannot be represented by the configured checkpoint codec.
    | NotCheckpointable = 10
    /// An execution projection observed a different root cardinality than it requires.
    | Cardinality = 11
    /// A finite or resumable source produced the same stable item key more than once.
    | DuplicateItemKey = 12
    /// A configured scheduler or graph resource bound was exhausted.
    | ResourceLimit = 13
    /// A runtime-generated Circuit failed validation or replay fingerprint verification.
    | GeneratedGraphIntegrity = 14
    /// An approval response was unknown, mismatched, or had already been consumed.
    | InvalidApprovalResponse = 15

/// Describes a failed Circuit operation in a serializable, runtime-neutral form.
/// <remarks>
/// Optional identifiers are populated only when the runtime can attribute the failure to a run,
/// operation, or approval request. <see cref="P:Circuit.Core.CircuitFailure.Exception" /> is kept for diagnostics but is not serialized.
/// </remarks>
[<Sealed>]
type CircuitFailure
    internal
    (
        code: CircuitFailureCode,
        message: string,
        runId: RunId voption,
        operationId: string voption,
        requestId: string voption,
        innerException: exn voption
    ) =
    do
        if String.IsNullOrWhiteSpace message then
            invalidArg "message" "Failure messages cannot be blank."

    /// Gets the coarse failure classification.
    member _.Code = code

    /// Gets a human-readable failure message suitable for logs or test assertions.
    member _.Message = message

    /// Gets the run that produced the failure when one is known.
    member _.RunId = runId

    /// Gets the tool or workflow operation identifier associated with the failure when available.
    member _.OperationId = operationId

    /// Gets the approval request identifier associated with the failure when available.
    member _.RequestId = requestId

    /// Gets the underlying exception when the runtime preserved one.
    /// <remarks>This property is ignored during JSON serialization.</remarks>
    [<JsonIgnore>]
    member _.Exception = innerException

    /// Creates an application-defined expected failure without run correlation.
    static member Create(code: CircuitFailureCode, message: string) =
        CircuitFailure(code, message, ValueNone, ValueNone, ValueNone, ValueNone)

module internal Failure =
    let create code message runId operationId requestId innerException =
        CircuitFailure(code, message, runId, operationId, requestId, innerException)

type private CircuitError =
    | Validation of CircuitFailure
    | StructuredOutputUnsupported of CircuitFailure
    | Decode of CircuitFailure
    | Provider of CircuitFailure
    | Tool of CircuitFailure
    | ApprovalRequired of CircuitFailure
    | Skill of CircuitFailure
    | Engine of CircuitFailure
    | CheckpointMismatch of CircuitFailure
    | Cancelled of CircuitFailure
    | NotCheckpointable of CircuitFailure
    | Cardinality of CircuitFailure
    | DuplicateItemKey of CircuitFailure
    | ResourceLimit of CircuitFailure
    | GeneratedGraphIntegrity of CircuitFailure
    | InvalidApprovalResponse of CircuitFailure

module private CircuitError =
    let ofFailure (failure: CircuitFailure) =
        match failure.Code with
        | CircuitFailureCode.Validation -> Validation failure
        | CircuitFailureCode.StructuredOutputUnsupported -> StructuredOutputUnsupported failure
        | CircuitFailureCode.Decode -> Decode failure
        | CircuitFailureCode.Provider -> Provider failure
        | CircuitFailureCode.Tool -> Tool failure
        | CircuitFailureCode.ApprovalRequired -> ApprovalRequired failure
        | CircuitFailureCode.Skill -> Skill failure
        | CircuitFailureCode.Engine -> Engine failure
        | CircuitFailureCode.CheckpointMismatch -> CheckpointMismatch failure
        | CircuitFailureCode.Cancelled -> Cancelled failure
        | CircuitFailureCode.NotCheckpointable -> NotCheckpointable failure
        | CircuitFailureCode.Cardinality -> Cardinality failure
        | CircuitFailureCode.DuplicateItemKey -> DuplicateItemKey failure
        | CircuitFailureCode.ResourceLimit -> ResourceLimit failure
        | CircuitFailureCode.GeneratedGraphIntegrity -> GeneratedGraphIntegrity failure
        | CircuitFailureCode.InvalidApprovalResponse -> InvalidApprovalResponse failure
        | _ -> invalidArg "failure" "Unknown failure code."

    let toFailure error =
        match error with
        | Validation failure
        | StructuredOutputUnsupported failure
        | Decode failure
        | Provider failure
        | Tool failure
        | ApprovalRequired failure
        | Skill failure
        | Engine failure
        | CheckpointMismatch failure
        | Cancelled failure
        | NotCheckpointable failure
        | Cardinality failure
        | DuplicateItemKey failure
        | ResourceLimit failure
        | GeneratedGraphIntegrity failure
        | InvalidApprovalResponse failure -> failure

/// Represents either a successful value or a <see cref="T:Circuit.Core.CircuitFailure" />.
type CircuitResult<'T> =
    private
    | SuccessCase of 'T
    | ErrorCase of CircuitError

    /// Gets whether the result contains a value.
    member this.IsSuccess =
        match this with
        | SuccessCase _ -> true
        | ErrorCase _ -> false

    /// Gets the successful value.
    /// <exception cref="T:System.InvalidOperationException">The result is a failure.</exception>
    member this.Value =
        match this with
        | SuccessCase value -> value
        | ErrorCase _ -> raise (InvalidOperationException("The result does not contain a value."))

    /// Gets the failure payload.
    /// <exception cref="T:System.InvalidOperationException">The result is a success.</exception>
    member this.Failure =
        match this with
        | SuccessCase _ -> raise (InvalidOperationException("The result does not contain a failure."))
        | ErrorCase error -> CircuitError.toFailure error

    /// Attempts to extract the successful value without throwing.
    /// <param name="result">Receives the successful value or the default value for <typeparamref name="'T" />.</param>
    /// <returns><see langword="true" /> when the result is successful; otherwise <see langword="false" />.</returns>
    member this.TryGetValue(result: byref<'T>) =
        match this with
        | SuccessCase value ->
            result <- value
            true
        | ErrorCase _ ->
            result <- Unchecked.defaultof<'T>
            false

    /// Creates a successful result.
    /// <param name="value">The value to wrap.</param>
    static member Success(value: 'T) = SuccessCase value

    /// Creates a failed result.
    /// <param name="failure">The failure to wrap.</param>
    /// <exception cref="T:System.ArgumentNullException"><paramref name="failure" /> is <see langword="null" />.</exception>
    static member Error(failure: CircuitFailure) =
        if isNull (box failure) then
            nullArg "failure"

        ErrorCase(CircuitError.ofFailure failure)
