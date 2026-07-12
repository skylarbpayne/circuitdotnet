# Microsoft Agent Framework adapter

`CircuitDotNet.MicrosoftAgentFramework` is Circuit's built-in runtime adapter today.

It provides:

- `MafRuntime` for direct runtime use;
- `AddMafRuntime` and `AddCircuit` DI helpers;
- tool resolution and approval hooks;
- skill resolution and optional script-runner hooks;
- native structured output with optional one-pass repair;
- workflow execution, HITL, and checkpoints;
- OpenTelemetry observer integration.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/MafAdapter.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/MafAdapter.cs)]

## Operational notes

- The C# `CircuitClientBuilder` uses this adapter under the hood.
- Policy-based tool approval and skill-script execution are configured on `MafRuntimeOptions`.
- Session support depends on the underlying `IChatClient`/agent implementation being able to round-trip provider state.
- The OpenTelemetry observer lives in this package because it follows adapter events.

## Known limitations

- This is the only built-in adapter in the repository today.
- Live-provider compatibility must be proven per provider/model pair.
- Script execution still requires a host-supplied runner and host-side sandboxing if you need isolation.
