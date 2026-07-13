# 13. Build a dynamic Circuit pipeline

## What you will build

You will route billing, security, and controlled-provider-failure tickets into materially different child graphs and compare every execution projection.

## The idea

`Circuit.thenDynamic` rebuilds, validates, and fingerprints a child Circuit for each keyed item. Security adds an audit node; billing uses one processing node; the provider lane recovers a typed failure and continues. The bound is two.

## Create or open the project

Open `tutorials/fsharp/13-workflows`. The example is offline and deterministic.

## Complete source

[!code-fsharp](../../tutorials/fsharp/13-workflows/Program.fs)

The source exercises `collect`, `collectSourceOrder`, `stream`, `run`, and `start`. `run` intentionally reports cardinality because the pipeline has three root outputs.

## Run it

```bash
dotnet run --project tutorials/fsharp/13-workflows
```

The fast controlled-failure lane recovers before the slow billing lane; the security output includes `audited`.

## What changed

Chapter 12 reused one topology. Chapter 13 creates different versioned graphs from runtime ticket values and keeps recovery inside the graph.

## Check your understanding

1. Why must the dynamic factory be deterministic?
2. Which projection resequences by source ordinal?
3. Why does `run` reject this pipeline?

## Try it yourself

Add a fourth kind with a two-node child and observe its distinct node path.

## Recap and next step

- Dynamic children are validated and fingerprinted.
- Controlled failures remain lane-local and recoverable.
- Projections change consumption, not scheduler semantics.

Chapter 14 pauses only one generated lane for review.
