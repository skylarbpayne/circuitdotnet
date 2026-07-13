# 12. Pipeline keyed work with a bound

## What you will build

You will admit three ticket lanes, process at most two concurrently, and print when each completed lane reaches a downstream node.

## The idea

`Circuit.keyedItems` assigns stable item identity. `WithMaxConcurrency(2)` bounds active lanes and backpressures source admission. Downstream handoff follows completion order, so the fast second ticket does not wait for the slow first ticket.

## Create or open the project

Open `tutorials/fsharp/12-parallel-programs`. No provider credentials are required.

## Complete source

[!code-fsharp](../../tutorials/fsharp/12-parallel-programs/Program.fs)

The delayed processing node and `completion-handoff` node make scheduling order visible.

## Run it

```bash
dotnet run --project tutorials/fsharp/12-parallel-programs
```

Representative deterministic order begins with `ticket-2` even though `ticket-1` was admitted first.

## What changed

Chapter 11 had one lane. Chapter 12 uses a finite keyed source, bounded concurrency, and immediate completion-order handoff.

## Check your understanding

1. What does the concurrency bound protect?
2. Why retain source ordinals?
3. Why must keys be unique?

## Try it yourself

Set the bound to one and compare the downstream order.

## Recap and next step

- Sources create stable independent lanes.
- Backpressure is bounded.
- Completion order drives execution; source order remains an explicit projection.

Chapter 13 materializes a different child graph for each ticket kind.
