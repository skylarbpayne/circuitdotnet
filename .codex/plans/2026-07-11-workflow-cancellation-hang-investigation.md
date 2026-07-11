# Workflow cancellation hang investigation

## Symptom Summary

`Circuit.MicrosoftAgentFramework.Tests.WorkflowRuntimeTests.run async drains cancelled streaming workflow to a cancelled result` hung the suite instead of finishing. Expected behavior: canceling the workflow should yield one cancelled terminal event/result and the background workflow tasks should shut down. Actual behavior: the test process stayed alive until killed.

## Context Provided

- Repo: `/home/skylarbpayne/projects/circuitdotnet`
- Branch: `feat/circuit-workflows`
- Affected area: MAF workflow streaming cancellation lifecycle, event/channel draining, run disposal, and the test itself
- SDK: `.NET 10.0.301` via `mise exec dotnet@10.0.301`
- Repro requirement: run the exact test with a shell timeout, then full `WorkflowRuntimeTests`, then full MAF tests

## Evidence Quality

- Primary evidence type: `direct code/history evidence`
- Telemetry status: `not needed`
- Operational query intent: n/a
- Evidence limits: local repro was enough; no external telemetry was needed

## Investigation Lanes Used

- Code/history: inspected `MafWorkflows.fs`, `MafRuntime.fs`, and `WorkflowRuntimeTests.fs` to trace the cancellation path and teardown logic.
- Local: reproduced the hang with a shell timeout, then re-ran the exact test and the full workflow/MAF suites after the fix.

## Evidence Gathered

- The exact test initially hung under `timeout 90s mise exec dotnet@10.0.301 -- dotnet test ... --filter "FullyQualifiedName~run async drains cancelled streaming workflow to a cancelled result"` until the shell timeout expired; the VSTest `testhost` process stayed alive and had to be killed.
- `src/Circuit.MicrosoftAgentFramework/MafWorkflows.fs` now links the outer workflow cancellation token into the watcher CTS, emits exactly one cancelled terminal `RunFailed` event when the run token is canceled, and bounds cleanup with `WaitAsync(TimeSpan.FromSeconds 5.0)` for both the event enumerator and wrapped workflow run disposal.
- `tests/Circuit.MicrosoftAgentFramework.Tests/WorkflowRuntimeTests.fs` now uses bounded waits (`WaitAsync`) for the start signal and final result in the cancellation tests.
- After the fix, the exact test passed twice in a row.
- `WorkflowRuntimeTests` passed in full (12 tests).
- The full `Circuit.MicrosoftAgentFramework.Tests` project passed in full (69 tests).

## Hypotheses Considered

1. The workflow stream watcher was not receiving cancellation, so the wrapped run never produced a terminal cancelled event.
2. Cleanup/disposal of the workflow wrapper or underlying `StreamingRun` was blocking indefinitely after cancellation.
3. The test itself was unbounded and could hide a real teardown hang instead of failing fast.

## Hypotheses Ruled Out

- A purely test-side issue: the test was updated to use bounded waits, but the production cancellation path also needed fixing; otherwise the exact test still hung.
- A broad MAF runtime regression outside workflows: other MAF cancellation coverage already passed, and the reproduced hang was isolated to the workflow wrapper path.

## Most Likely Root Cause

The workflow wrapper did not fully propagate outer run cancellation into the watcher lifecycle and teardown path, so a canceled workflow could strand background work and never deliver a terminal cancelled result to the consumer. The follow-on unbounded disposal made the hang persistent instead of failing fast.

## Confidence And Why

High confidence: the hang reproduced locally, the workflow cancellation path was the only failing area, the code now explicitly closes that gap, and the exact test plus the broader workflow and MAF suites pass after the change.

## Remaining Unknowns

None material. The failure mode is understood well enough to keep the fix bounded and the suite now fails fast if teardown regresses.

## Recommended Next Skill

`/wonderly-work` — the root cause is pinned down and the next step is implementation/cleanup verification, not more diagnosis.
