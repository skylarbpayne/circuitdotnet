# 8. Add a typed tool

## What you will build

Register a typed, read-only account lookup and ask the model to use it while answering a support ticket. The tool returns deterministic host data; the model's final wording remains variable.

## The idea

A tool lets the model request application code:

```text
model tool call -> typed input validation -> host function -> typed output validation -> model
```

`StaticToolResolver` supplies the catalog and the agent's tag selects it. `ApprovalMode.Never` is appropriate here only because this tutorial lookup is read-only. Circuit validates both typed contracts, while `ToolContext.CancellationToken` lets the host operation stop with the run.

Tools execute trusted code in the host process. Circuit does not sandbox that code and makes no idempotency guarantee; production hosts must implement access control, timeouts, retries, and side-effect safety appropriate to each operation.

## Create or open the project

From the repository root, install the SDK selected by `global.json` and configure reader-owned OpenAI values:

```bash
export OPENAI_API_KEY="your key from your secret store"
export OPENAI_MODEL="a model available to your account"
```

The live request may invoke the tool and incurs provider cost. The program has no embedded credential, model default, or fake provider fallback.

## Complete source

[!code-fsharp](../../tutorials/fsharp/08-tools/Program.fs)

Data annotations constrain account IDs and both output fields. The model—not application code—chooses when to issue the call, subject to the tools the host exposes.

## Run it

```bash
dotnet run --project tutorials/fsharp/08-tools
```

Representative output (provider-generated values are variable):

```text
Category: account-plan
Suggested reply: Your active Business plan includes priority support...
```

## What changed

Chapter 7 supplied prior conversation state. Chapter 8 supplies fresh host-owned facts through a typed execution boundary.

## Check your understanding

1. Which component decides whether to request the lookup?
2. Where are tool input and output checked?
3. What safety properties are not provided by tool registration?

## Try it yourself

Change the account ID in the ticket from `ACM-2048` to `XYZ-1000`. Observe how the deterministic tool plan changes while the final prose remains provider-variable.

## Recap and next step

- Resolvers expose only the tool catalog selected by the host.
- Typed contracts validate data entering and leaving host code.
- A tool is host-process execution, not a sandbox.

Next, place an explicit operator pause in front of a sensitive tool.
