# 14. Pause for human review

## What you will build

You will start a live drafting workflow, wait for `ApprovalRequested`, and continue it with either `--approve` or `--reject`. The sample also proves that the runtime rejects a mismatched token and a second use of the matching token.

## The idea

`Workflow.start` returns a live `WorkflowRun`, not a completed result. Its event stream pauses at the request step:

```text
draft.response -> review.response -- ApprovalRequested --> host decision
                                             |
                                   RespondAsync(token, decision)
                                             |
                                      terminal result
```

The request token binds the response to one pending pause and is single-use. Circuit models this continuation protocol; it does **not** identify, authenticate, or authorize the operator. The host must apply identity, role, policy, audit, and separation-of-duty checks before calling `RespondAsync`. Approval prompts and draft content can be sensitive.

## Create or open the project

From a repository clone, open `tutorials/fsharp/14-human-review`, then configure OpenAI:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Each invocation normally makes one drafting call and can incur a charge. Use a structured-output-capable model and keep credentials in an appropriate secret store.

## Complete source

[!code-fsharp](../../tutorials/fsharp/14-human-review/Program.fs)

The program validates the workflow, starts it, and manually enumerates events until one terminal event. At the pause it deliberately demonstrates wrong-token rejection, sends the selected matching response, and demonstrates replay rejection. It does not print the sensitive prompt message. Both the enumerator and live run are disposed, including failure and cancellation paths.

## Run it

Run the approval path:

```bash
dotnet run --project tutorials/fsharp/14-human-review -- --approve
```

Then run the separate rejection path if you want to spend one additional provider call:

```bash
dotnet run --project tutorials/fsharp/14-human-review -- --reject
```

Representative approval output (the hidden draft is **provider-variable**):

```text
Approval requested by step: review.response
Draft content is sensitive; inspect it only in an authorized review UI.
Mismatched token rejected.
Reused token rejected.
Terminal decision: approved
```

The rejection command ends with `Terminal decision: rejected`. Token values and prompt content are intentionally not printed.

## What changed

Chapter 13 ran the graph directly to completion. Chapter 14 uses a live workflow handle and events because the host must make an external decision between two steps.

## Check your understanding

1. Why must the response carry the exact pending request token?
2. Which authorization responsibilities remain with the host application?
3. Why should approval prompt content and arguments be treated as sensitive?

## Try it yourself

Run only the `--reject` command and confirm that rejection is an explicit terminal decision, not a timeout or silently dropped response.

## Recap and next step

- `Workflow.start` exposes events and a controlled continuation handle.
- Approval tokens are matching and single-use, but are not operator identities.
- The host owns authorization and must dispose abandoned paused runs.

Chapter 15 checkpoints the paused run so a different process can restore and continue the exact workflow definition.
