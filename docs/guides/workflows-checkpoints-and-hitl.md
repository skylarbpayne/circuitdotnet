# Pipelines, checkpoints, and human review

Circuit composition replaces the former separate workflow surface. The Core scheduler runs agent leaves, host code, pipelines, approvals, and resumed checkpoints through one `ICircuitRuntime`.

## Build one graph

Use `Circuit.thenStep` for static continuation and `Circuit.thenDynamic` when each item selects a different child graph. Dynamic factories require stable IDs, semantic versions, deterministic keys, and bounded concurrency. Circuit freezes and fingerprints every generated child before execution.

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/Workflows.fs)]

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Workflows.cs)]

## Handle lane failures

Ordinary continuation propagates a failed response without invoking downstream nodes. `Circuit.attempt` exposes the response as a successful value for explicit routing. `Circuit.recover` maps an expected failure to a replacement value. Unrelated lanes continue unless the graph explicitly cancels them.

## Human approval

`Circuit.approval` pauses only the affected lane. Use `Circuit.start` to observe `ApprovalRequested`, then call `CircuitRun.RespondAsync` with the matching request ID. Responses are single-use. The host remains responsible for operator identity, authorization, policy, auditing, and timeout behavior.

## Checkpoint and resume

Call `CircuitRun.CreateCheckpointAsync` at a host-selected barrier. The response can fail with `NotCheckpointable` when a graph contains a plain asynchronous source or when an admitted value cannot be encoded by the checkpoint codec.

Resume by supplying the same Circuit definition to `ICircuitRuntime.ResumeAsync`. Circuit verifies the root fingerprint, rebuilds dynamic children from saved item snapshots, compares child fingerprints, restores committed node responses, and replays in-flight leaves. Root output delivery remains at-least-once and retains stable item keys.

Always dispose abandoned `CircuitRun` handles so active provider work and bounded channels are cancelled and drained.
