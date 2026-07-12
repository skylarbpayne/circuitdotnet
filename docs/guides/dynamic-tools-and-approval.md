# Dynamic tools and approval

Dynamic tool resolution lets the runtime choose tools at run time from tenant, user, or service context.

Approval mode is part of the tool contract:

- `Never` for read-only or otherwise safe tools;
- `Always` for explicit human review;
- `ByPolicy` when the runtime host supplies a named approval policy.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/DynamicToolsAndApproval.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/DynamicToolsAndApproval.cs)]

## Failure behavior

- Duplicate resolved tool identities fail resolution.
- Invalid tool schemas or approval-policy configuration fail before provider execution.
- Tool handler exceptions surface as `Tool` with sanitized public messages.
- Approval requests that do not match the pending token are rejected.

## Cancellation behavior

Cancellation flows into resolver calls, approval handling, and tool handlers through the active token.

## Security notes

- Prefer `Never` only for truly safe read paths.
- Use `Always` or `ByPolicy` for writes and externally visible side effects.
- Approval protects execution; it does not sandbox the tool code itself.

## What Circuit does not guarantee

- idempotency of tool side effects;
- global uniqueness of tool IDs across unrelated runtimes;
- a built-in approval UI or approval storage system.
