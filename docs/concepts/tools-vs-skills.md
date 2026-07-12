# Tools vs. skills

Tools and skills solve different problems.

## Tools

Tools are typed callable capabilities.

Use a tool when the model needs fresh data or an effectful action:

- read data;
- call an API;
- write state behind an approval boundary.

A tool has input and output schemas, validation, tags, and an approval mode.

## Skills

Skills are versioned guidance bundles.

Use a skill when the model needs:

- instructions;
- reference files or resources;
- optional script descriptors for a runtime-specific script runner.

Skills shape prompt context. Tools shape executable capability.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/ToolsVsSkills.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/ToolsVsSkills.cs)]

## Practical rule

- If the model should *read or change the world*, use a tool.
- If the model should *read guidance before deciding*, use a skill.

## What Circuit does not guarantee

- that every runtime executes skills the same way;
- that skill scripts are sandboxed;
- that tool side effects are idempotent unless your handler makes them idempotent.
