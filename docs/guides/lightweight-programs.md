# Composing Circuit graphs

Circuit uses one immutable graph model rather than a separate function-backed program representation.

Use `Circuit.agent` for a typed agent leaf, `Circuit.code` for trusted versioned host code, and `Circuit.thenStep` to connect successful responses. A failed lane bypasses ordinary continuation. Use `Circuit.attempt` for explicit response routing or `Circuit.recover` for a direct replacement value.

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/LightweightPrograms.fs)]

Finite and resumable sources turn the same graph into a pipeline. Each completed item enters downstream work immediately, subject to bounded lane and stage concurrency. `Circuit.collect` preserves completion order; `Circuit.collectSourceOrder` only resequences the final projection.

Closure-bearing durable combinators require explicit IDs and versions. Change the version whenever handler behavior changes in a way that should invalidate saved checkpoints.
