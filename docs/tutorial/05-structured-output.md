# 5. Control structured output

## What you will build

You will run the typed support reply with Circuit's default provider-native structured-output policy. An optional `--repair` mode supplies a separate secondary OpenAI `IChatClient` and publicly opts into one secondary repair pass if native output cannot be decoded.

## The idea

A typed F# output does not by itself force a model to return matching JSON. With `NativeOnly`, Circuit asks the provider to honor the generated schema and fails if that guarantee is unavailable or the answer cannot be decoded.

```text
NativeOnly: input -> primary model with schema -> decode -> TicketOutput or failure

Repair allowed: input -> primary model -> decode failure
                                      `-> secondary model -> decode -> output or failure
```

Repair is not a free local parser. When needed, it sends provider-produced output across a second model boundary, performs at most one additional model pass, shares that data with the secondary client, and can add tokens, latency, and provider cost. It may improve formatting, not factual correctness. Therefore it is explicit and the default remains native-only.

## Create or open the project

Use the checked-in repository project because Circuit packages are not published:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

No model is selected by the sample. Choose one available to your account that supports native structured output. Both modes can incur provider charges, and repair mode can make one extra charged request when repair is necessary. Keep keys out of source and transcripts, use secret-manager injection in production, and review whether provider output may cross the secondary-client data boundary.

## Complete source

[!code-fsharp](../../tutorials/fsharp/05-structured-output/Program.fs)

The default passes `RunOptions.Default`, whose policy is `NativeOnly`. The opt-in path creates a second `ChatClient` adapter, assigns it to `MafRuntimeOptions.SecondaryStructuredOutputClient`, and constructs immutable run options through the public pipeline helper `RunOptions.withStructuredOutputPolicy`. Merely supplying the client does not enable repair; the per-run policy must also allow it. Both adapters and the timeout are disposed.

## Run it

Run native-only mode from the repository root:

```bash
dotnet run --project tutorials/fsharp/05-structured-output
```

Representative output:

```text
Structured output policy: native only
Category: Account access
Suggested reply: Check spam, verify the account address, and request one fresh reset link.
```

Opt in to possible secondary repair:

```bash
dotnet run --project tutorials/fsharp/05-structured-output -- --repair
```

Representative output:

```text
Structured output policy: secondary repair allowed
Category: Account access
Suggested reply: Confirm the email address and try one new reset request after checking spam.
```

Category and reply text are **provider-variable** in both examples. The policy line is deterministic. `--repair` allows an additional pass only if native decoding fails, so a successful native response may still use just the primary call.

## What changed

Chapter 4 handled structured-output failures as categories. This chapter makes the policy choice explicit: retain the native-only default, or configure a separate client and opt into bounded secondary repair for one run.

## Check your understanding

1. Why is `NativeOnly` the default even though repair may recover malformed output?
2. What two configuration steps are required before secondary repair is allowed?
3. Which data, security, and cost boundary changes when repair actually runs?

## Try it yourself

Change only the deterministic policy label in the repair branch to `Secondary repair permitted (one extra pass maximum)`. Run default mode once and verify its label remains `native only`; avoid a second provider call unless you intend to pay for it.

## Recap and next step

- Native structured output asks the primary provider to honor the signature's schema.
- Secondary repair requires both a separately configured client and explicit per-run policy.
- An actual repair adds one model pass and a new data, latency, and cost boundary.

Chapter 6 keeps the typed terminal result but exposes streaming events while the provider is working.
