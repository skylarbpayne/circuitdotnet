# 9. Require approval

## What you will build

Start an interactive agent run whose escalation tool always pauses. Run the same program with `--approve` or `--reject`, respond on the live handle, and continue until one terminal event.

## The idea

Approval changes the tool flow into a protocol:

```text
model requests tool -> ApprovalRequested -> host decision -> continue -> terminal event
```

`Circuit.start` requires `ICircuitRuntime` and returns a live `CircuitRun`. Its request identifier is opaque and single-use. The host responds to that exact request on the same handle, then keeps enumerating.

Circuit models a pause; it does not identify, authenticate, or authorize the operator. The host owns identity, authorization, policy, audit, and user experience. Tool arguments may contain customer secrets, so this sample never prints `ArgumentsJson`.

## Create or open the project

From the repository root, install the SDK selected by `global.json` and set explicit reader-owned configuration:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY; echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Each branch can make multiple provider calls and incur charges. The 45-second timeout owns cancellation; abandoning the live handle disposes its provider state.

## Complete source

[!code-fsharp](../../tutorials/fsharp/09-approvals/Program.fs)

The escalation contract is typed and uses `ApprovalMode.Always`. The program reports only tool name and opaque request ID, responds to every request, and disposes both enumerator and run. The model controls whether it asks for the tool and can ask again; this host permits only the first request when `--approve` is selected and rejects any additional request. Circuit also bounds interactive rounds and approval requests.

## Run it

Approve:

```bash
dotnet run --project tutorials/fsharp/09-approvals -- --approve
```

Reject:

```bash
dotnet run --project tutorials/fsharp/09-approvals -- --reject
```

Representative approved output (the request count, request ID, and provider-generated response are variable):

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

Run one command and confirm that it reports at least one approval and exactly one terminal event. If a model asks more than once, confirm later requests are rejected by the host policy.

## Recap and next step

- Approval is a pause-and-response protocol on a live run.
- Matching, single-use identifiers prevent responding to the wrong pause.
- Approval does not provide authentication or authorization.

Next, add versioned guidance that does not execute application code.
