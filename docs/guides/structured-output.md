# Structured output

Circuit treats structured output as a contract, not a best-effort string parse.

Use `StructuredOutputPolicy.NativeOnly` when the provider must satisfy the schema in one pass. Use `AllowSecondaryModelRepair` when you want one explicit repair call through a separately configured `SecondaryStructuredOutputClient`.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/StructuredOutput.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/StructuredOutput.cs)]

## Failure behavior

- Malformed JSON, wrapper-shape mismatches, and null-for-non-nullable results surface as `Decode`.
- If repair is requested but no repair client is configured, Circuit fails with `StructuredOutputUnsupported` before provider execution.
- Repair is single-pass. A failed repair still fails the run.

## Cancellation behavior

Cancellation stops the active provider call or repair call and surfaces a cancelled terminal result/event.

## Security notes

- In `SensitiveDataMode.Redact`, Circuit avoids exposing original provider payloads in public failure messages.
- Repair re-sends the original response text to the repair model. Treat that as a second disclosure boundary.

## What Circuit does not guarantee

- semantic correctness just because the JSON shape matched;
- provider-native structured output support for every model;
- repeated repair attempts or automatic fallback across many models.
