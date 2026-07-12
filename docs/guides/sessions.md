# Sessions

Sessions let an adapter carry provider-owned conversational state across runs.

In Circuit today, the Microsoft Agent Framework adapter binds a session to:

- the adapter identity;
- the agent definition;
- the signature binding;
- tenant and user context;
- resolved tool and skill capability snapshots.

## F# example

[!code-fsharp[F#](../../tests/Circuit.FSharp.Tests/DocumentationExamples/Sessions.fs)]

## C# example

[!code-csharp[C#](../../tests/Circuit.Interop.Tests/DocumentationExamples/Sessions.cs)]

## Failure behavior

- Deserializing malformed envelopes fails immediately.
- Restoring a session against the wrong runtime, agent, tenant/user context, or capability snapshot fails.
- Session support depends on the adapter. Unsupported adapters cannot promise round-trips.

## Cancellation behavior

Serialization and deserialization accept cancellation and should be treated like any other adapter call.

## Security notes

- Session envelopes can contain provider state and metadata. Store them like sensitive application data.
- Do not treat session IDs as authorization tokens.

## What Circuit does not guarantee

- compatibility across adapter changes;
- restoration across incompatible definition or capability changes;
- portability of one adapter's session payload into another adapter.
