namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open Circuit.Core
open Circuit.FSharp

[<AllowNullLiteral>]
type Classification() =
    [<property: Required>]
    member val Category = "" with get, set

[<AllowNullLiteral>]
type Summary() =
    [<property: Required>]
    member val Text = "" with get, set

module LightweightProgramsExample =
    let agent =
        AgentDefinition.create "triage.agent" "1.0.0" "Triage" "Classify the input."

    let signature =
        Signature.create<string, Classification> "triage.signature" "1.0.0" "Triage" "Return only the classification."

    let program =
        circuit {
            let! classification = Circuit.call agent signature "A billing ticket"

            return Summary(Text = $"Category={classification.Category}")
        }
