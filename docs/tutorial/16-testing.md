# 16. Test the pipeline offline

## What you will build

You will test a three-lane dynamic pipeline with `ScriptedRuntime`, including a streamed success, a controlled provider failure recovered in the graph, and distinct child node paths.

## The idea

`ScriptedResponses.ForNode` matches responses to scheduler node-path suffixes, so concurrent execution does not depend on dequeue races. Calls retain one scheduler run ID and distinct node paths.

## Create or open the project

This xUnit project is offline:

```bash
unset OPENAI_API_KEY OPENAI_MODEL
dotnet restore tutorials/fsharp/16-testing --locked-mode
```

## Complete source

[!code-fsharp](../../tutorials/fsharp/16-testing/Tests.fs)

The test uses the production Core scheduler, `Circuit.thenDynamic`, three matched agent responses, recovery, bounded event buffering, recorded calls, and stable correlation assertions.

## Run it

```bash
dotnet test tutorials/fsharp/16-testing
```

One deterministic test passes without a provider or credentials.

## What changed

Chapter 15 demonstrated process durability. Chapter 16 keeps the dynamic topology but replaces provider execution with a deterministic scripted leaf adapter.

## Check your understanding

1. Why is node matching safer than a shared concurrent queue?
2. Which scheduler behavior remains production behavior?
3. What does one run ID plus distinct node paths prove?

## Try it yourself

Add a fourth matched branch and assert its path and recovered output.

## Recap and next step

- Scripted leaves do not bypass the scheduler.
- Node-path matching is deterministic under concurrency.
- Calls, outputs, failures, and correlation can be asserted without token cost.

Chapter 17 returns to the live adapter and observes structural telemetry.
