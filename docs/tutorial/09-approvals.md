# 9. Require approval

## What you will build

Start an interactive agent run whose escalation tool always pauses. Run the same program with `--approve` or `--reject`, respond on the live handle, and continue until one terminal event.

## The idea

Approval changes the tool flow into a protocol:

```text
model requests tool -> ApprovalRequested -> host decision -> continue -> terminal event
```

`Agent.start` requires `IInteractiveCircuitRuntime` and returns a live `AgentRun`. Its request identifier is opaque and single-use. The host responds to that exact request on the same handle, then keeps enumerating.

Circuit models a pause; it does not identify, authenticate, or authorize the operator. The host owns identity, authorization, policy, audit, and user experience. Tool arguments may contain customer secrets, so this sample never prints `ArgumentsJson`.

## Create or open the project

From the repository root, install the SDK selected by `global.json` and set explicit reader-owned configuration:

```bash
export OPENAI_API_KEY="your key from your secret store"
export OPENAI_MODEL="a model available to your account"
```

Each branch can make multiple provider calls and incur charges. The 45-second timeout owns cancellation; abandoning the live handle disposes its provider state.

## Complete source

[!code-fsharp](../../tutorials/fsharp/09-approvals/Program.fs)

The escalation contract is typed and uses `ApprovalMode.Always`. The program reports only tool name and opaque request ID, sends one `ApprovalResponse`, and disposes both enumerator and run.

## Run it

Approve:

```bash
dotnet run --project tutorials/fsharp/09-approvals -- --approve
```

Reject:

```bash
dotnet run --project tutorials/fsharp/09-approvals -- --reject
```

Representative approved output (request ID and provider-generated response are variable):

```text
Approval requested for tool 'ticket.escalate' (request 7f...).
Host decision: approve
Category: escalation
Suggested reply: Your ticket has been queued for a specialist...
```

A rejected run still continues to a provider-dependent terminal result; the tool itself does not execute. Provider failure produces a fixed, non-sensitive diagnostic rather than a fake success.

## What changed

Chapter 8 allowed a read-only lookup automatically. Chapter 9 places a resumable host decision before a sensitive escalation.

## Check your understanding

1. Why must the response use the pending request ID and live run?
2. Which approval responsibilities remain with the host?
3. Why does the sample avoid printing tool arguments?

## Try it yourself

Run both commands and compare the decision line and terminal reply. Confirm that each invocation reports exactly one approval and one terminal event.

## Recap and next step

- Approval is a pause-and-response protocol on a live run.
- Matching, single-use identifiers prevent responding to the wrong pause.
- Approval does not provide authentication or authorization.

Next, add versioned guidance that does not execute application code.
