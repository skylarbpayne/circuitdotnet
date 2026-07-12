# 15. Checkpoint and resume

## What you will build

You will run two distinct processes. The first drafts a response, pauses for review, and atomically writes an opaque checkpoint envelope; the second reads it, publicly deserializes it, resumes the exact workflow fingerprint, supplies a decision, and deletes the file.

## The idea

A live pause belongs to one process. A workflow checkpoint captures runtime-owned continuation state so another process can restore it:

```text
process 1: start -> ApprovalRequested -> Serialize -> atomic file replace -> exit
process 2: read -> WorkflowCheckpoint<bool>.Deserialize -> Workflow.resume
                                                        -> approve/reject -> terminal
```

The envelope includes workflow identity, version, graph fingerprint, and opaque provider/runtime state. `Deserialize` validates envelope shape and format, while `Workflow.resume` checks compatibility with the supplied definition. Neither operation proves authenticity. Changing the definition version or topology changes compatibility and resume fails; do not weaken the checked-in definition to bypass that protection.

## Create or open the project

From the repository root, open `tutorials/fsharp/15-checkpoints` and configure OpenAI:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Choose a structured-output-capable model. `create` normally incurs one provider call; `resume` continues stored state and provider behavior can add cost. For this tutorial, choose a private temporary/application-data path, for example:

```bash
CHECKPOINT_PATH="${TMPDIR:-/tmp}/circuit-tutorial-$USER/review.checkpoint.json"
```

A temp path is convenient, not production-secure. Checkpoints can contain prompts, responses, sessions, and provider state. Production storage needs strict access control, encryption at rest, integrity/authenticity protection, size limits, retention policy, and audit; never accept an untrusted client envelope directly.

## Complete source

[!code-fsharp](../../tutorials/fsharp/15-checkpoints/Program.fs)

`create` waits for the real approval event before calling `CreateCheckpointAsync`. It writes a same-directory temporary file with user-only Unix permissions where available and atomically replaces the destination. `resume` applies a 1 MiB limit, parses JSON, calls the public `WorkflowCheckpoint<bool>.Deserialize`, and passes that restored value to `Workflow.resume`. There is no reflection or same-process substitute. The resumed enumerator and run are disposed, and the checkpoint file is deleted after the resume attempt.

## Run it

Run this command and let the process exit:

```bash
dotnet run --project tutorials/fsharp/15-checkpoints -- create "$CHECKPOINT_PATH"
```

Representative first-process output:

```text
Checkpoint written atomically to: /tmp/.../review.checkpoint.json
Stop this process, then use the documented resume command.
```

Now start a **new process** and choose one decision:

```bash
dotnet run --project tutorials/fsharp/15-checkpoints -- resume "$CHECKPOINT_PATH" --approve
```

Use `--reject` instead to exercise rejection (create a fresh checkpoint first because envelopes are single-use tutorial state). Representative output:

```text
Restored the pending review; checkpoint contents are not displayed.
Resumed workflow completed: approved
```

The draft is **provider-variable** and intentionally hidden. Paths vary. The final decision is deterministic from the flag. Missing configuration and malformed/incompatible state produce fixed safe errors without dumping envelope or exception content.

## What changed

Chapter 14 continued one in-memory live handle. Chapter 15 serializes a paused continuation and proves public restoration with separate `create` and `resume` process invocations.

## Check your understanding

1. Why are both definition version and graph fingerprint checked on resume?
2. What does `Deserialize` validate, and what security property does it not establish?
3. Why should checkpoint storage be more protected than an ordinary cache file?

## Try it yourself

Create a checkpoint, copy it to another private path, and resume the original with `--reject`. Confirm the original is deleted afterward, then securely delete the unused copy without opening or editing its opaque contents.

## Recap and next step

- A checkpoint is an opaque, sensitive continuation envelope, not a session or general data model.
- Public deserialization plus `Workflow.resume` enables true cross-process continuation only for the exact compatible definition.
- Atomic writes, size limits, cleanup, access control, encryption, and authenticity checks belong at the storage boundary.

Chapter 16 replaces the live provider with deterministic scripted responses so these boundaries can be tested without spending tokens.
