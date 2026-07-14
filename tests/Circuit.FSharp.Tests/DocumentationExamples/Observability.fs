module DocumentationExamples.Observability

open System.Threading
open Circuit.Core
open Circuit.FSharp

let definition: Circuit<unit, string> =
    Circuit.value "ok" |> Circuit.define "docs-observability" "1.0.0"

let run (runtime: ICircuitRuntime) =
    Circuit.run runtime definition () RunOptions.Default CancellationToken.None
