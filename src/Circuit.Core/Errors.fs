namespace Circuit.Core

open System
open System.Text.Json.Serialization

type CircuitFailureCode =
    | Validation = 0
    | StructuredOutputUnsupported = 1
    | Decode = 2
    | Provider = 3
    | Tool = 4
    | ApprovalRequired = 5
    | Skill = 6
    | Workflow = 7
    | CheckpointMismatch = 8
    | Cancelled = 9

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

    member _.Code = code
    member _.Message = message
    member _.RunId = runId
    member _.OperationId = operationId
    member _.RequestId = requestId

    [<JsonIgnore>]
    member _.Exception = innerException

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
    | Workflow of CircuitFailure
    | CheckpointMismatch of CircuitFailure
    | Cancelled of CircuitFailure

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
        | CircuitFailureCode.Workflow -> Workflow failure
        | CircuitFailureCode.CheckpointMismatch -> CheckpointMismatch failure
        | CircuitFailureCode.Cancelled -> Cancelled failure
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
        | Workflow failure
        | CheckpointMismatch failure
        | Cancelled failure -> failure

type CircuitResult<'T> =
    private
    | SuccessCase of 'T
    | ErrorCase of CircuitError

    member this.IsSuccess =
        match this with
        | SuccessCase _ -> true
        | ErrorCase _ -> false

    member this.Value =
        match this with
        | SuccessCase value -> value
        | ErrorCase _ -> raise (InvalidOperationException("The result does not contain a value."))

    member this.Failure =
        match this with
        | SuccessCase _ -> raise (InvalidOperationException("The result does not contain a failure."))
        | ErrorCase error -> CircuitError.toFailure error

    member this.TryGetValue(result: byref<'T>) =
        match this with
        | SuccessCase value ->
            result <- value
            true
        | ErrorCase _ ->
            result <- Unchecked.defaultof<'T>
            false

    static member Success(value: 'T) = SuccessCase value

    static member Error(failure: CircuitFailure) =
        if isNull (box failure) then
            nullArg "failure"

        ErrorCase(CircuitError.ofFailure failure)
