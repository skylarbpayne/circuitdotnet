# Workflows, checkpoints, and HITL

Workflows add explicit step graphs, approval pauses, resume, and checkpoints.

Use a workflow when you need:

- ordered or parallel step execution;
- explicit human-in-the-loop approval;
- pause/resume with a checkpoint envelope;
- versioned workflow topology validation.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/Workflows.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Workflows.cs)]

## Failure behavior

- Invalid graphs report `WorkflowValidationIssue` entries.
- Approval responses must match the pending request token.
- Resuming with a changed definition version or fingerprint fails with checkpoint mismatch behavior.
- Parallel workflow branches cancel siblings on failure.

## Checkpoint versioning rule

Workflow checkpoints are only safe when **both** of these remain compatible:

- the workflow definition semantic version; and
- the workflow graph fingerprint.

The fingerprint covers topology and declared metadata only. It intentionally does **not** hash runtime delegates or objects such as code-step handlers, branch selectors, approval prompt builders, parallel aggregates, or loop predicates.

That means you must bump the workflow definition semantic version whenever any of those behaviors change, even if the graph shape stays the same.

## Cancellation behavior

Workflow cancellation stops active steps, drains the event stream, and emits one cancelled terminal event/result.

## Security notes

- Approval tokens are single-use and run-scoped.
- Checkpoints should be treated as sensitive state snapshots.
- Human approval is an application boundary; Circuit does not supply identity or authorization policy.

## What Circuit does not guarantee

- migration of checkpoints across incompatible topology changes;
- durable storage, replication, or retention of checkpoints;
- compensation for already-executed side effects.
