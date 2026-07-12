# 11. Build a `circuit {}` program

## What you will build

You will classify one support ticket and then draft a response with two live OpenAI calls. A `circuit {}` computation expression makes the data dependency visible and returns one structured success or failure.

## The idea

A Circuit program is lightweight composition. `Circuit.call` describes an agent operation; `let!` runs it and makes its typed value available to the next operation.

```text
Ticket -> classify -> Classification -> draft -> DraftOutput
              failure --------X
```

If classification fails, execution short-circuits and drafting does not start. Child operations share the parent run's correlation, usage, cancellation, and failure boundary. This is in-memory orchestration, not a durable workflow: `circuit {}` does not create a checkpoint or survive process termination.

## Create or open the project

Circuit packages are not published yet. From a repository clone, open `tutorials/fsharp/11-circuit-programs`. Set a key and an explicit model that supports structured output:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Provider calls can incur charges; this chapter normally makes two calls. Keep keys out of source and shell history, and use secret-manager injection in production.

## Complete source

[!code-fsharp](../../tutorials/fsharp/11-circuit-programs/Program.fs)

The first `Circuit.call` produces `Classification`. Ordinary F# constructs the typed `DraftInput`, and the second call consumes it. `Circuit.run` executes the whole description with one timeout and returns the root `RunId`, aggregated usage, and one `CircuitResult`. No helper project hides provider or runtime setup.

## Run it

From the repository root:

```bash
dotnet run --project tutorials/fsharp/11-circuit-programs
```

Representative output (**all classification and draft text is provider-variable**, and the run ID varies):

```text
Run: run_...
Category: Account access (urgency 2)
Draft: Check the spam folder, then request one new reset email.
```

Missing configuration produces a fixed corrective message before client construction. Provider, validation, decode, or cancellation failures produce a short error and a nonzero exit code.

## What changed

Chapter 10 gave one agent policy guidance through a skill. This chapter instead composes two typed agent calls with `circuit {}`, passing the first value into the second while keeping one application-level result boundary.

## Check your understanding

1. Why does drafting not run when classification fails?
2. What does the parent run correlate across the two calls?
3. Why is this program not a durable workflow?

## Try it yourself

Add the classified urgency to the drafting agent's instructions and run once. Confirm the program still prints one classification and one draft, or one explicit failure; this remains a two-call exercise.

## Recap and next step

- `Circuit.call` lifts a typed agent request into a composable program.
- `let!` sequences dependent work and failures short-circuit later work.
- Correlation and cancellation do not make an in-memory program durable.

Chapter 12 keeps the lightweight program model but runs three independent analyses with a safe concurrency bound.
