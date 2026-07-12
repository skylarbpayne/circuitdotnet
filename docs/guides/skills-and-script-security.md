# Skills and script security

Skills are versioned guidance bundles. They can be inline, file-backed, or runtime-custom.

File skills require safe roots that contain `SKILL.md`. Circuit rejects rooted escapes, missing files, and unsafe link traversal when it resolves file-backed skill roots.

Script execution is opt-in and runtime-specific. Circuit carries script descriptors and arguments, but it does not provide a sandbox.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/SkillsSecurity.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/SkillsSecurity.cs)]

## Failure behavior

- Invalid file roots fail during skill construction.
- Duplicate skill identities from resolvers fail resolution.
- Skill resolution or script-runner failures surface as `Skill`.

## Cancellation behavior

Cancellation flows through skill resolution and script execution requests when the runtime supports script execution.

## Security notes

- Treat skill roots as trusted content.
- Treat skill scripts as arbitrary code.
- Prefer immutable, versioned skill directories for reproducibility.
- Dynamic skill properties are process data, not a secret store.

## What Circuit does not guarantee

- sandboxing, resource limits, or network isolation for scripts;
- that two runtimes interpret the same skill bundle identically;
- automatic integrity verification for skill files beyond safe path resolution.
