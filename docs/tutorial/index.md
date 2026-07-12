# Progressive F# tutorial

A Circuit takes typed input, asks an agent runtime to produce typed output, and returns either a value or a structured failure. This tutorial evolves one support-ticket agent through 18 small, independent projects.

## Before you begin

You should know basic F# syntax, functions, classes or records, and asynchronous code. Install the .NET SDK selected by this repository (`10.0.301`), clone the repository, and restore it:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
dotnet restore CircuitDotNet.slnx --locked-mode
```

Live chapters require your own `OPENAI_API_KEY` and `OPENAI_MODEL`. Provider calls can incur charges, and cost varies by model and usage. Chapter 16 is offline and spends no tokens. Never commit credentials; use environment variables or an appropriate secret store.

Circuit packages are not published yet. These lessons therefore run in **repository mode** with project references. Package-install commands are not currently available.

## Roadmap

1. **One typed agent (chapters 1–5):** run a useful agent, then understand its contracts and failures.
2. **Give the agent capabilities (chapters 6–10):** add streaming, sessions, tools, approvals, and skills.
3. **Compose work (chapters 11–15):** build programs and durable workflows progressively.
4. **Make it production-ready (chapters 16–18):** test offline, observe safely, and change providers.

| Chapter | Observable progress |
|---|---|
| [1. Run your first agent](01-first-agent.md) | Configure OpenAI and receive a typed support reply. |
| [2. Understand the four building blocks](02-building-blocks.md) | Name the provider, agent, signature, and runtime roles. |
| [3. Validate input and output](03-validation.md) | Reject invalid data at typed boundaries. |
| [4. Handle failures explicitly](04-failures.md) | Respond to structured failures without exposing exception details. |
| [5. Control structured output](05-structured-output.md) | Choose native structured output and understand repair boundaries. |
| [6. Stream a response](06-streaming.md) | Observe progress and one terminal result. |
| [7. Continue a session](07-sessions.md) | Carry provider-owned conversation state into another request. |
| [8. Add a typed tool](08-tools.md) | Let the agent call validated application code. |
| [9. Require approval](09-approvals.md) | Pause a sensitive tool call for a host decision. |
| [10. Add a skill](10-skills.md) | Add versioned guidance without confusing it with executable code. |
| [11. Build a circuit program](11-circuit-programs.md) | Compose agent calls with the F# computation expression. |
| [12. Run independent work in parallel](12-parallel-programs.md) | Use bounded concurrency and deterministic result ordering. |
| [13. Build an explicit workflow](13-workflows.md) | Give a support process named, validated topology. |
| [14. Pause for human review](14-human-review.md) | Approve or reject a workflow on its live handle. |
| [15. Checkpoint and resume](15-checkpoints.md) | Persist and restore a paused workflow safely. |
| [16. Test without spending tokens](16-testing.md) | Verify agent behavior with ScriptedRuntime and xUnit. |
| [17. Add telemetry safely](17-telemetry.md) | Observe structural signals while content remains redacted. |
| [18. Switch providers](18-switch-providers.md) | Change IChatClient configuration without changing Circuit contracts. |

## Cost, security, and help

- Live provider output is variable. Review provider pricing and set your own usage limits before running a live chapter.
- Tools and skill scripts run in the host process; Circuit is not a sandbox. Approvals model a pause but do not authenticate or authorize a person.
- Sessions and checkpoints can contain sensitive data. Telemetry content capture requires explicit opt-in.
- For failure codes and common corrective actions, see [Errors](../reference/errors.md) and [Getting started with F#](../getting-started/fsharp.md).
- Read the [security model](../reference/security-model.md), [provider compatibility evidence](../reference/provider-compatibility.md), and [API reference](../../api/index.md) before production use.
