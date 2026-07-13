# 11. Compose a static Circuit graph

## What you will build

You will classify and format one support ticket through two statically connected, typed code nodes.

## The idea

`Circuit.thenStep` sends only a successful completed response into the next node. A failed classification lane skips formatting. `Circuit.define` gives the graph stable identity and version.

## Create or open the project

Open `tutorials/fsharp/11-circuit-programs`. This chapter is intentionally offline so the graph mechanics are visible without provider variability.

## Complete source

[!code-fsharp](../../tutorials/fsharp/11-circuit-programs/Program.fs)

The source builds one immutable graph, validates its types at compile time, and executes it through the same `ICircuitRuntime` kernel used by agent leaves.

## Run it

```bash
dotnet run --project tutorials/fsharp/11-circuit-programs
```

It prints `ticket-1:identity`.

## What changed

Earlier chapters ran one agent Circuit. This chapter connects two nodes with ordinary failure propagation.

## Check your understanding

1. Which response enters the formatting node?
2. Why do node IDs and versions matter for checkpoints?
3. Why is the runtime still required for code-only graphs?

## Try it yourself

Change the subject so the classifier returns `general` and run again.

## Recap and next step

- Circuit graphs are immutable and inspectable.
- Completed responses, not deltas, trigger continuation.
- Static continuation propagates failures without invoking the next node.

Chapter 12 admits several keyed lanes with bounded concurrency.
