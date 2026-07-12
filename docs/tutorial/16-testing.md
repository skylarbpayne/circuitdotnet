# 16. Test without spending tokens

## What you will build

You will test the support-ticket agent without a provider, network connection, API key, or token charge. One xUnit test drives a normal run, a stream, and a provider failure through `ScriptedRuntime`.

## The idea

Most application tests should verify what your code sends to Circuit and how it handles Circuit's result boundary—not whether a remote model happens to use the same words today.

`ScriptedRuntime` consumes responses in queue order and records each call:

```text
scripted output -> normal call -> typed TicketOutput
scripted chunks -> stream      -> ordered RunEvent values
scripted failure -> normal call -> CircuitFailure
```

The runtime still serializes inputs, validates output contracts, and emits the normal Circuit result and streaming types. It does not emulate a provider, tool execution, approval pump, or the full MAF observer pipeline.

## Create or open the project

This chapter is deliberately offline. From an existing repository checkout, unset any provider credentials if you want proof that the test cannot use them:

```bash
unset OPENAI_API_KEY OPENAI_MODEL
dotnet restore tutorials/fsharp/16-testing --locked-mode
```

PowerShell uses `Remove-Item Env:OPENAI_API_KEY, Env:OPENAI_MODEL -ErrorAction SilentlyContinue`. The test project references the checked-in Circuit projects because the packages are not published yet.

## Complete source

[!code-fsharp](../../tutorials/fsharp/16-testing/Tests.fs)

The queue contains exactly three responses. `OutputJson` exercises normal typed decoding, `Stream` produces deltas followed by a decoded terminal value, and `Failure` returns the chosen failure unchanged except for Circuit's generated run ID. `RecordedCall` lets the test inspect identifiers, versions, serialized input, and both schemas without reaching into runtime internals.

`RunAssertions` checks two protocol invariants: stream sequence numbers increase and exactly one terminal event occurs. `RemainingResponses = 0` proves that the expected calls consumed the whole script.

## Run it

From the repository root:

```bash
dotnet test tutorials/fsharp/16-testing
```

Representative output:

```text
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

This output is deterministic and makes no provider request.

## What changed

Chapter 15 exercised a durable live workflow across processes. This chapter changes only the runtime: `ScriptedRuntime` replaces `MafRuntime`, while the same support agent, signature, typed contracts, and Circuit calls remain testable.

## Check your understanding

1. Why does the test assert the recorded input and schemas as well as the decoded value?
2. What do monotonic sequence and one terminal event prove about a scripted stream?
3. Why is `ScriptedRuntime` useful without pretending to reproduce model behavior?

## Try it yourself

Add one assertion that the second recorded call has `ScriptedCallKind.Streaming`. Run this single test once and confirm the response count still reaches zero.

## Recap and next step

- Queue scripted success, stream, and failure responses in the order your code should call them.
- Assert typed results and the request contract recorded at the runtime seam.
- Keep ordinary tests deterministic, offline, fast, and free of provider charges.

Chapter 17 switches back to the live adapter and observes structural run telemetry without capturing ticket or prompt payloads.
