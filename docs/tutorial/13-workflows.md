# 13. Build an explicit workflow

## What you will build

You will express classification, typed adaptation, and response drafting as named workflow steps. The program validates the graph before making live OpenAI calls, then executes the definition to completion.

## The idea

A `circuit {}` program is convenient local composition. A workflow adds a stable identity, version, named nodes, explicit edges, validation, event topology, and a foundation for pause and resume.

```text
classify.ticket
      |
prepare.draft-input     (Classification -> DraftInput)
      |
draft.response
```

Agent steps must be type-compatible. The explicit code step is not filler: it adapts the classifier's output into exactly the drafter's input type. `Workflow.validate` checks graph shape and contracts; it does not contact the model or prove that a later provider response will be valid.

## Create or open the project

From the repository root, open `tutorials/fsharp/13-workflows`. Configure an explicit structured-output-capable model:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

The successful path makes two provider calls and can incur charges. Store credentials outside source and use access-controlled secret injection in production.

## Complete source

[!code-fsharp](../../tutorials/fsharp/13-workflows/Program.fs)

`Workflow.agent`, `Workflow.code`, and `Workflow.thenStep` create a typed graph. `Workflow.define` gives the complete graph an ID and semantic version. The program refuses to run if validation returns any issue, then uses `Workflow.run` for the live execution.

## Run it

```bash
dotnet run --project tutorials/fsharp/13-workflows
```

Representative output (the draft is **provider-variable**):

```text
Workflow validated: classify.ticket -> prepare.draft-input -> draft.response
Draft: Check the spam folder and verify the email address on the account.
```

The validation line is deterministic for the checked-in definition. Missing provider configuration fails before client construction; runtime failures remain fixed, short, and nonzero.

## What changed

Chapter 12 used lightweight parallel composition. Chapter 13 chooses additional structure—names, version, topology, and validation—because later chapters need to observe and pause a stable process.

## Check your understanding

1. Why is the adapter step required between the two agents?
2. What does graph validation prove, and what does it not prove?
3. When is explicit workflow topology worth more structure than `circuit {}`?

## Try it yourself

Rename only `prepare.draft-input` to `prepare.classified-draft`, run once, and confirm the validated topology line and final typed result behavior remain understandable.

## Recap and next step

- A workflow gives composition a stable ID, version, and named topology.
- Adjacent workflow steps must have compatible input and output types.
- Validate before live execution, while still handling provider and typed-output failures at runtime.

Chapter 14 starts the workflow as a live run so a human-review request can pause and continue it.
