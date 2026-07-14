# Circuits

A `Circuit<'Input,'Output>` is the only executable definition. It is an immutable, inspectable graph with a stable ID, semantic version, structural fingerprint, inferred cardinality, checkpointability metadata, and bounded resource settings. Its read-only `Graph` descriptor exposes non-executable node paths, identities, kinds, versions, child topology, concurrency/iteration limits, validation findings, and fingerprint; it never exposes handlers or constant payloads.

A single agent is a one-node Circuit. Static and dynamic pipelines, branches, merges, loops, approvals, recovery, and aggregation use the same graph and the same Core-owned scheduler.

## Execution model

`ICircuitRuntime.StartAsync` starts the event-stream kernel. The other execution forms are projections:

- `Circuit.run` requires exactly one root `OutputProduced` event;
- `Circuit.collect` returns all lane responses in completion order;
- `Circuit.collectSourceOrder` resequences the final collection lexicographically by the complete outer-to-inner source ordinal path;
- `Circuit.stream` yields completed root responses immediately;
- `Circuit.start` exposes node events, approvals, checkpoints, and disposal.

Every run emits exactly one terminal `RunCompleted` event. Expected provider, validation, approval, checkpoint, and cancellation failures are represented by `Response<'T>` rather than a separate terminal event shape.

## Composition

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/LightweightPrograms.fs)]

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Workflows.cs)]

Finite sources use stable ordinal keys by default; `Circuit.keyedItems` supplies explicit keys. Nested lanes compose parent and child identity and preserve a hierarchical source-order path through checkpoints. `Circuit.asyncSource` is executable but deliberately not checkpointable. Dynamic factories require explicit IDs and versions and are rebuilt and fingerprint-checked during resume.

Successful lane responses enter ordinary continuations immediately. Failed lanes bypass ordinary continuation until `Circuit.attempt` or `Circuit.recover` handles them. Provider deltas are observational and never trigger downstream work.

## Durability boundary

A checkpoint snapshots admitted item values, stable keys and hierarchical ordinals, completed node responses, dynamic child fingerprints, source cursors, pending scheduler state, full graph approval prompts (including frozen host-routing metadata), and adapter-owned sessions through `ICircuitRuntime.SerializeSessionAsync`. On resume, session payloads are validated against their owning agent and restored through `DeserializeSessionAsync` before any leaf executes. In-flight leaves are replayed after the checkpoint barrier. Committed internal node work is restored from the journal, while completed root output may be replayed with the same item key for caller deduplication.

`RunOptions.Services` is process-local capability state and is never serialized. Every resume accepts explicit `ResumeOptions`; a receiving process supplies an equivalent service provider there. Applications must rebind required services rather than placing service instances or secrets in durable state.

Changing graph topology, node identity, explicit version, agent/signature version, or a generated child fingerprint makes the checkpoint incompatible. External effects remain at-least-once; code and tool contexts therefore receive stable idempotency keys.

## Resource and security boundary

Factories and code nodes are trusted host code, not a sandbox. Run options separately bound active lanes, buffered events, generated nodes, approval rounds, items per resumable page, total resumable pages, and checkpoint bytes. Async sources reserve lane capacity before pulling. Resumable providers return one explicitly bounded page at a time; the scheduler snapshots that page before admitting its items and rejects non-advancing cursors. Checkpoint payloads may contain sensitive input, output, session, and approval state and require host-provided authenticity, encryption, access control, and retention.
