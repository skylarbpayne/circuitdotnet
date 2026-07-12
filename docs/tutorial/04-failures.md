# 4. Handle failures explicitly

## What you will build

You will convert every `CircuitFailureCode` into a short, safe application message. The default path remains live, while `--validation` and `--cancel` make two controlled failures observable without printing exceptions, provider payloads, or request contents.

## The idea

A structured failure separates application control flow from sensitive diagnostics. Code can decide whether a failure is validation, provider, decode, cancellation, or another Circuit boundary without parsing exception text.

```text
RunResult
  |-- success -> TicketOutput
  `-- failure -> CircuitFailureCode -> safe application message
```

`CircuitFailure` can carry diagnostic details for trusted handling, but a normal user-facing console should not dump its exception, identifiers, or raw provider content. A failure code is a classification, not proof that retrying is safe.

## Create or open the project

Use repository mode; Circuit packages are not published:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

There is no embedded key or model default. Select a structured-output-capable model available to your account. The default command makes a paid provider call; the two controlled modes are handled before provider execution. Keep credentials in an appropriate secret store in real applications and clear the environment variable when finished.

## Complete source

[!code-fsharp](../../tutorials/fsharp/04-failures/Program.fs)

`printFailure` covers validation, unsupported structured output, decode, provider, tool, approval, skill, workflow, checkpoint mismatch, and cancellation codes. Its final branch keeps the application safe if a newer runtime supplies an unfamiliar numeric enum value. The function deliberately does not print `failure.Exception`, request IDs, operation IDs, or provider payloads.

The `--cancel` branch passes an already-cancelled token. Circuit reports cancellation through `RunResult` for expected runtime cancellation; the outer exception handler remains a fixed safety net for startup or adapter exceptions.

## Run it

Default live run:

```bash
dotnet run --project tutorials/fsharp/04-failures
```

Representative output (**provider-variable**):

```text
Category: Account access
Suggested reply: Confirm the address and request one new reset email after checking spam.
```

Controlled validation failure:

```bash
dotnet run --project tutorials/fsharp/04-failures -- --validation
```

```text
The ticket or typed response did not satisfy its validation rules.
```

Controlled cancellation:

```bash
dotnet run --project tutorials/fsharp/04-failures -- --cancel
```

```text
The request was cancelled before completion.
```

All modes require the environment preflight. The controlled modes do not make provider requests; the default does and may incur charges.

## What changed

Chapter 3 printed a validation-specific message and otherwise exposed Circuit's supplied message. This chapter centralizes all failure classifications into application-owned safe text and adds a cancellation demonstration.

## Check your understanding

1. Why is matching a failure code safer than parsing exception text?
2. Does a `Provider` failure automatically mean a retry is safe?
3. Which failure details does this console intentionally avoid showing to users?

## Try it yourself

Change only the safe `CircuitFailureCode.Provider` message to recommend contacting support. Run `--validation` and verify its message is unchanged; do not force a paid provider failure.

## Recap and next step

- `RunResult` makes success and structured failure explicit.
- Every current failure code maps to deliberate application behavior.
- Raw exceptions, identifiers, and provider content do not belong in ordinary user output.

Chapter 5 focuses on two failure-adjacent structured-output policies: native provider enforcement and explicitly allowed secondary repair.
