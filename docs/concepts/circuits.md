# Circuits

A Circuit is a typed boundary around an agent interaction.

At minimum, a circuit has:

- an `AgentDefinition` that names the agent and its instructions;
- a `Signature` or `AgentSignature<TInput,TOutput>` that fixes the input and output contracts;
- a runtime that executes the request;
- optional tools, skills, sessions, observers, and workflows around the run.

Use a single run when one typed request is enough. Use an F# lightweight program when you want local composition without durable checkpoints. Use a workflow when you need explicit steps, approvals, pause/resume, or checkpoints.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/LightweightPrograms.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Workflows.cs)]

## Design notes

- Contracts are part of the circuit, not a comment beside it.
- Validation runs before provider execution and again around tool boundaries.
- Workflow checkpoints bind to a workflow fingerprint, not only an ID string.

## What Circuit does not guarantee

- that two different providers interpret the same instructions the same way;
- that a successful single run is durable or replayable later;
- that every runtime exposes every feature surface;
- that untyped side effects inside tools can be reversed automatically.
