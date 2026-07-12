# Getting started in F#

Use `CircuitDotNet.FSharp` when you want F#-first construction of agents, signatures, lightweight programs, or core workflow primitives.

## Install

Add these packages to the host project:

- `CircuitDotNet.FSharp`
- `CircuitDotNet.Testing` for deterministic tests or offline samples
- `CircuitDotNet.MicrosoftAgentFramework` when you want the built-in MAF runtime

## First run

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/GettingStarted.fs)]

This example uses `ScriptedRuntime` so it compiles and runs offline.

## Failure behavior

- Invalid input fails before provider execution with `Validation`.
- Decode failures surface as `Decode`.
- Provider/runtime failures surface as `Provider`, `Tool`, `Skill`, or `Workflow` depending on the boundary.

## Cancellation behavior

Cancellation flows through `Agent.run`, tool handlers, workflow steps, and F# lightweight programs through the active `CancellationToken`.

## Security notes

- `RunOptions.Default` uses standard sensitivity mode.
- Skill scripts are runtime-specific and are not sandboxed by Circuit.
- Tool handlers run as your process code.

## What Circuit does not guarantee

- a high-level F# host builder for every runtime option;
- durable recovery for single runs;
- provider compatibility beyond the tested adapter/runtime combination.
