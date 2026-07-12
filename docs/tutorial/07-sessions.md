# 7. Continue a session

## What you will build

Send a support ticket, retain the returned `CircuitSession`, and ask a follow-up that refers to the first request. Both requests are live OpenAI calls.

## The idea

A session is opaque conversation state owned by the provider adapter:

```text
first run -> CircuitSession -> RunOptions.withSession -> follow-up run
```

The host may serialize it only through `ICircuitRuntime.SerializeSessionAsync` and restore it through the matching adapter's deserializer. Session state can contain sensitive conversation/provider data and is bound to its compatible agent/runtime. It is not a durable Circuit workflow checkpoint and provides no workflow replay or resume guarantee.

## Create or open the project

From a repository clone, use the .NET SDK selected by `global.json` and set both variables in your shell or secret manager:

```bash
export OPENAI_API_KEY="your key from your secret store"
export OPENAI_MODEL="a model available to your account"
```

This chapter makes two paid provider requests, so account limits and charges apply. There is no built-in model default or offline fallback.

## Complete source

[!code-fsharp](../../tutorials/fsharp/07-sessions/Program.fs)

The second options value is an immutable copy of `RunOptions.Default`. The first result must contain a session before the follow-up can continue.

## Run it

```bash
dotnet run --project tutorials/fsharp/07-sessions
```

Representative output (all categories and replies are provider-variable):

```text
First response category: account-access
First response reply: Check your spam folder and verify the account address...
Follow-up category: account-access
Follow-up reply: Since those checks are complete, contact support to verify delivery...
```

## What changed

Chapter 6 followed one request through its stream. Chapter 7 performs two complete requests and explicitly passes adapter-owned state between them.

## Check your understanding

1. Who defines the opaque contents of `CircuitSession`?
2. Why should session serialization be protected as sensitive data?
3. Why is a session not a durable workflow checkpoint?

## Try it yourself

Change only the follow-up message to ask for a one-sentence recap. Verify that the second reply still refers to the original ticket.

## Recap and next step

- A returned session can continue a compatible conversation.
- `RunOptions.withSession` preserves the other run-option defaults.
- Sessions are sensitive adapter state, not durable workflow state.

Next, let the model request fresh application data through a typed tool.
