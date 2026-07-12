# Lightweight programs

Lightweight programs are the F# `circuit` computation expression. They help you compose several local steps and agent calls without moving into the heavier workflow runtime.

Use a lightweight program when you want:

- F#-native composition;
- bounded parallel local work;
- short-lived orchestration that does not need checkpoints or pause/resume.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/LightweightPrograms.fs)]

## C# availability

Circuit does not expose an equivalent lightweight-program API for C# today. Use workflows or ordinary application code instead.

## Failure behavior

- `Circuit.fail` short-circuits the remaining computation.
- Parallel branches cancel siblings after the first failure.
- Child-run failures are rewritten to the root program run for consistent correlation.

## Cancellation behavior

Program cancellation stops queued work before start and cancels active parallel branches through the shared token.

## Security notes

- A lightweight program does not add isolation around tools or agent calls.
- Use explicit workflow approvals when a step requires human release.

## What Circuit does not guarantee

- durability;
- checkpoints;
- replay after process restart;
- a C# façade for this API surface.
