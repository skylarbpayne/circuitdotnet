namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open Circuit.Core
open Circuit.FSharp

[<AllowNullLiteral>]
type ValidatedInput() =
    [<property: Required>]
    member val Message = "" with get, set

    [<property: Required>]
    member val Severity = "" with get, set

[<AllowNullLiteral>]
type ValidatedOutput() =
    [<property: Required>]
    member val Summary = "" with get, set

type SeverityValidator() =
    interface IContractValidator<ValidatedInput> with
        member _.Validate value =
            if value.Severity = "low" || value.Severity = "high" then
                [||]
            else
                [| { Path = "$.severity"
                     Code = "validation"
                     Message = "Severity must be low or high." } |]

type OutputValidator() =
    interface IContractValidator<ValidatedOutput> with
        member _.Validate value =
            if value.Summary.Length <= 200 then
                [||]
            else
                [| { Path = "$.summary"
                     Code = "validation"
                     Message = "Summary must be 200 characters or fewer." } |]

module SignaturesValidationExample =
    let create () =
        Signature.create<ValidatedInput, ValidatedOutput>
            "validation.signature"
            "1.0.0"
            "Validated reply"
            "Return only the validated output."
        |> Signature.withInputValidator (SeverityValidator())
        |> Signature.withOutputValidator (OutputValidator())
