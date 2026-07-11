namespace Circuit.Core

open System

module private ApprovalValidation =
    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

[<Sealed>]
type ApprovalRequest internal (requestId: string, toolName: string, argumentsJson: string voption) =
    member _.RequestId = ApprovalValidation.requireNonBlank "requestId" requestId
    member _.ToolName = ApprovalValidation.requireNonBlank "toolName" toolName
    member _.ArgumentsJson = argumentsJson
