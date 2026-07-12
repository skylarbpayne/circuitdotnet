# Getting started in C#

Use `CircuitDotNet` when you want the C# façade over Circuit contracts, runs, workflows, and sessions.

## Install

Add these packages to the host project:

- `CircuitDotNet`
- `CircuitDotNet.MicrosoftAgentFramework`
- `CircuitDotNet.Testing` for deterministic tests or offline samples

## First run

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/GettingStarted.cs)]

The `CircuitClientBuilder` C# path is the recommended application-facing entry point.

## Failure behavior

- Invalid input fails before the provider call with `AgentFailureCode.Validation`.
- Provider decoding failures surface as `AgentFailureCode.Decode`.
- Missing runtime setup fails during client build or first use.

## Cancellation behavior

All public run APIs accept an optional `CancellationToken`. Streaming cancellation stops enumeration and returns a single cancelled terminal event.

## Security notes

- Tools run inside your process.
- Skills add guidance; they do not add isolation.
- Sessions are adapter-owned state and should be treated as sensitive data.

## What Circuit does not guarantee

- that every `IChatClient` implementation supports every Circuit feature;
- that a built client can move across runtimes or package versions without revalidation;
- that session state from one agent definition can be restored into another.
