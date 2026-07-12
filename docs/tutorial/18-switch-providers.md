# 18. Switch providers

## What you will build

Change IChatClient configuration without changing Circuit contracts. This project is a compiling chapter skeleton; its complete lesson will replace the placeholder in the next tutorial-writing pass.

## The idea

Each chapter changes one main idea while remaining an independent project.

## Create or open the project

From the repository root, open `tutorials/fsharp/18-switch-providers`. Live chapters will require explicit provider environment variables; chapter 16 remains offline.

## Complete source

[!code-fsharp](../../tutorials/fsharp/18-switch-providers/Program.fs)

## Run it

```bash
dotnet run --project tutorials/fsharp/18-switch-providers
```

Representative placeholder output (the completed live chapter's provider-generated values will be variable):

```text
Chapter 18 will build on the support-ticket agent.
```

## What changed

This skeleton reserves chapter 18's approved project and documentation boundary. The completed lesson will explain its single delta from chapter 17.

## Check your understanding

1. Why should this chapter remain independently runnable?
2. Which one boundary will this chapter introduce?
3. Which values will be deterministic, and which will be provider-variable?

## Try it yourself

Build this project from the repository root and confirm that it does not depend on another tutorial project.

## Recap and next step

The project, page, and complete-source include now have stable names. The next writing pass will replace this placeholder with the approved support-ticket lesson without changing that structure.
