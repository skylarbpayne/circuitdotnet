# Observability and redaction

`CircuitDotNet.MicrosoftAgentFramework` includes `OpenTelemetryRunObserver` for spans and metrics around runs, tools, approvals, and workflow steps.

By default, the observer records structure and timing, not raw prompt or payload bodies. You must opt in to prompt/input/output/tool-argument capture, and you can attach a redactor.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/Observability.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Observability.cs)]

## Failure behavior

- Observer dispatch failures are logged and do not fail the run.
- Terminal observer events still record failure status and repaired-run metadata when available.
- `ScriptedRuntime` does not emit the full MAF observer pipeline because it is a testing runtime, not the MAF adapter.

## Cancellation behavior

Cancelled runs emit cancelled terminal status and close active operation spans.

## Security notes

- Leave payload capture off unless you have a concrete need.
- Prefer `SensitiveDataMode.Redact` for user or secret-bearing runs.
- If you enable payload capture, your redactor becomes part of the security boundary.

## What Circuit does not guarantee

- automatic PII detection;
- compliance for your telemetry backend;
- redaction of data that your own tool or skill code logs outside Circuit.
