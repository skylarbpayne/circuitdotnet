# Testing

`CircuitDotNet.Testing` gives you deterministic helpers for unit and integration tests.

Use it for:

- queued scripted outputs and streams;
- recorded input/schema/options snapshots;
- monotonic event and terminal-event assertions;
- observer capture without a live provider.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/Testing.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Testing.cs)]

## Failure behavior

- An exhausted `ScriptedRuntime` queue throws `ScriptedResponseExhaustedException`.
- Scripted failure responses surface as normal Circuit failures.
- Assertion helpers fail fast when sequence or terminal-event invariants break.

## Cancellation behavior

`ScriptedResponses.WaitForCancellation()` lets you test cancellation paths deterministically.

## Security notes

- Recorded calls include serialized input, schema, and options snapshots. Treat them as test artifacts that may contain sensitive data.
- `RecordingRunObserver` is for tests; do not expose it as a public audit log without additional controls.

## What Circuit does not guarantee

- that a scripted test proves live-provider compatibility;
- network or provider timing realism;
- end-to-end fidelity for adapter-specific provider session behavior unless you exercise the real adapter.
