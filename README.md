# Circuit

Circuit is typed composition for agentic .NET.

Circuit gives you:

- typed input and output contracts for agent runs;
- native structured output with optional single-pass repair;
- dynamic tools with explicit approval boundaries;
- skills as versioned instruction bundles;
- immutable graph-backed Circuits with pipelining, branching, approvals, and checkpoints;
- one Core-owned scheduler with Microsoft Agent Framework as an agent-leaf adapter;
- testing helpers for deterministic examples and assertions;
- OpenTelemetry observer hooks with explicit redaction controls.

## Packages

- `CircuitDotNet` — primary C# entry point.
- `CircuitDotNet.FSharp` — F# graph composition and execution projections.
- `CircuitDotNet.Core` — runtime-neutral graph, response, event, scheduler, and checkpoint contracts.
- `CircuitDotNet.MicrosoftAgentFramework` — MAF adapter, DI helpers, and OpenTelemetry observer.
- `CircuitDotNet.Testing` — scripted runtimes and assertion helpers.

## Choose an entry point

- Use `CircuitDotNet` for application code in C#.
- Use `CircuitDotNet.FSharp` for F#-first composition.
- Use `CircuitDotNet.Core` only when you are building a custom runtime or lower-level adapter.
- Use `CircuitDotNet.MicrosoftAgentFramework` when you want the built-in MAF adapter directly.
- Use `CircuitDotNet.Testing` for deterministic tests and sample hosts.

## Start here

- **[Run your first agent](docs/tutorial/01-first-agent.md)** — begin the progressive F# tutorial with a live typed support-ticket agent.
- [Concepts: circuits](docs/concepts/circuits.md)
- [Concepts: signatures and validation](docs/concepts/signatures-and-validation.md)
- [Concepts: tools vs. skills](docs/concepts/tools-vs-skills.md)
- [Getting started in F#](docs/getting-started/fsharp.md)
- [Getting started in C#](docs/getting-started/csharp.md)
- [Guides: structured output](docs/guides/structured-output.md)
- [Guides: dynamic tools and approval](docs/guides/dynamic-tools-and-approval.md)
- [Guides: skills and script security](docs/guides/skills-and-script-security.md)
- [Guides: lightweight programs](docs/guides/lightweight-programs.md)
- [Guides: workflows, checkpoints, and HITL](docs/guides/workflows-checkpoints-and-hitl.md)
- [Guides: sessions](docs/guides/sessions.md)
- [Guides: observability and redaction](docs/guides/observability-and-redaction.md)
- [Guides: testing](docs/guides/testing.md)
- [Reference: CI and release gates](docs/reference/ci-and-release.md)
- [Reference: provider compatibility](docs/reference/provider-compatibility.md)
- [Reference: errors](docs/reference/errors.md)
- [Reference: versioning](docs/reference/versioning.md)
- [Reference: security model](docs/reference/security-model.md)
- [Reference: Microsoft Agent Framework adapter](docs/reference/maf-adapter.md)

## Offline samples

The repository includes offline ticket-triage samples in both languages.

- `mise exec dotnet@10.0.301 -- dotnet run --project samples/TicketTriage.CSharp`
- `mise exec dotnet@10.0.301 -- dotnet run --project samples/TicketTriage.FSharp`

Both commands are deterministic and ignore provider credentials. Live provider, tool, skill, session,
approval, telemetry, and provider-switching setup is taught progressively in the 18 runnable F# tutorial
chapters instead of being hidden behind sample-only command-line switches.

## Current scope

Circuit currently ships one production adapter: the Microsoft Agent Framework adapter in `CircuitDotNet.MicrosoftAgentFramework`.

## Non-goals

Circuit does not currently promise:

- provider portability across untested live providers;
- sandboxing for skill scripts or tool handlers;
- automatic retries, compensation, or durable orchestration outside explicit Circuit checkpoints;
- backwards-compatible session or checkpoint restoration across incompatible definition changes.
