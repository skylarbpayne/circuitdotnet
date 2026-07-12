# Signatures and validation

A signature describes one typed request/response pair.

Circuit generates JSON Schema for both sides and applies validation in two layers:

1. built-in Data Annotations validation; and
2. any extra `IContractValidator` validators you add.

That validation happens before the provider call for run input, before tool execution for tool input, and after tool/provider decoding for typed outputs.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/SignaturesValidation.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/SignaturesValidation.cs)]

## Failure shape

Validation failures surface as `Validation` / `AgentFailureCode.Validation` with one or more `ValidationIssue` entries at the contract boundary.

## What Circuit does not guarantee

- semantic correctness beyond the validators you declare;
- cross-version schema compatibility unless you preserve it yourself;
- automatic migration of old payloads into new contract shapes.
