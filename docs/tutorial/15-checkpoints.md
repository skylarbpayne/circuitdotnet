# 15. Checkpoint active lanes and resume

## What you will build

You will use two processes. `create` checkpoints while one lane waits for review and another delayed lane is active; `resume` publicly deserializes the envelope, restores the request, and completes matching materialized child graphs.

## The idea

A checkpoint binds root ID, version, fingerprint, source snapshots, generated child fingerprints, completed responses, correlation, and pending work. Resume rejects malformed or changed state before new work. Storage still needs authenticity, encryption, access control, size limits, retention, and audit.

## Create or open the project

Open `tutorials/fsharp/15-checkpoints` and choose a private path:

```bash
CHECKPOINT_PATH="${TMPDIR:-/tmp}/circuit-tutorial-$USER/review.json"
```

## Complete source

[!code-fsharp](../../tutorials/fsharp/15-checkpoints/Program.fs)

The first process waits for `ApprovalRequested`, calls `CreateCheckpointAsync`, and atomically moves a same-directory temporary file. The second uses `CircuitCheckpoint<string>.Deserialize` and `Circuit.resume`. It deletes the file after the attempt.

## Run it

```bash
dotnet run --project tutorials/fsharp/15-checkpoints -- create "$CHECKPOINT_PATH"
dotnet run --project tutorials/fsharp/15-checkpoints -- resume "$CHECKPOINT_PATH" --approve
```

Use `--reject` with a fresh checkpoint for the rejection path.

## What changed

Chapter 14 owned one live in-memory handle. Chapter 15 crosses a real process boundary with an opaque serialized checkpoint.

## Check your understanding

1. Why rebuild every saved dynamic child before work starts?
2. Why are stable item keys and idempotency keys preserved?
3. Which security property is not supplied by deserialization?

## Try it yourself

Change one child version after `create` and confirm resume rejects the fingerprint mismatch.

## Recap and next step

- Checkpoints are sensitive execution state, not ordinary caches.
- Resume requires the exact compatible graph.
- Root delivery and external effects remain at-least-once boundaries.

Chapter 16 tests the same dynamic pipeline offline with deterministic node matching.
