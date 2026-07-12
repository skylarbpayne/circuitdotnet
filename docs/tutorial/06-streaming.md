# 6. Stream a response

## What you will build

Run the support agent as a live stream and show progress without printing provider deltas. The program checks event ordering and accepts exactly one successful or failed terminal event.

## The idea

`RunStreamingAsync` returns events rather than waiting for one final value:

```text
RunStarted -> OutputDelta ... -> RunCompleted | RunFailed
```

Sequences must increase monotonically, and a well-formed stream has exactly one terminal event. Deltas are provider-variable and can contain customer data, so this chapter counts their characters instead of logging them. Cancellation is passed both to the runtime and the manually managed async enumerator.

## Create or open the project

Clone or open this repository, then work from its root. Install the .NET SDK selected by `global.json` and set reader-owned credentials (runs call OpenAI and incur provider charges):

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY; echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

The program embeds no default model and exits before client construction when either variable is absent.

## Complete source

[!code-fsharp](../../tutorials/fsharp/06-streaming/Program.fs)

The explicit `GetAsyncEnumerator`, `MoveNextAsync`, and `DisposeAsync` calls make stream ownership visible. Only the members appropriate to each `RunEventKind` are read.

## Run it

```bash
dotnet run --project tutorials/fsharp/06-streaming
```

Representative output (`.` count, category, reply, and delta count are provider-variable):

```text
........
Received 284 provider-variable delta characters.
Category: account-access
Suggested reply: Check spam and confirm the address on the account...
```

The 30-second timeout can instead produce the fixed cancellation message. No fake response replaces a provider failure.

## What changed

Chapter 5 waited for a final structured result. This chapter consumes a live event protocol, treats deltas as sensitive, and still trusts only the single typed terminal value.

## Check your understanding

1. Why is an output delta not the final typed result?
2. What two invariants does the consumer check?
3. Why does the sample avoid printing delta text?

## Try it yourself

Replace each progress `.` with a count printed every 100 characters. Confirm that no delta content is disclosed.

## Recap and next step

- Streaming exposes progress before completion.
- Sequence and terminal-event checks defend the consumer boundary.
- Cancellation and async-enumerator disposal remain application responsibilities.

Next, carry adapter-owned conversation state from one request to another.
