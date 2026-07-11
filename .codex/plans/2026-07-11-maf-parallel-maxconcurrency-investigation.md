# MAF parallel maxConcurrency investigation

## Symptom

The new `maxConcurrency` work in `src/Circuit.MicrosoftAgentFramework/MafWorkflows.fs` compiles, but five `WorkflowRuntimeTests` fail:

- `parallel enforces maxConcurrency two across five branches`
- `parallel maxConcurrency one serializes branch starts`
- `parallel gates are isolated across simultaneous runs`
- `parallel failure cancels queued siblings without hanging`
- `parallel resume reconstructs held permits before starting queued branches`

Observed behavior:

- the original blocking acquire design hangs before queued branches can even start
- after wiring the non-blocking acquire/release dispatch path, the first two branches start, but the next queued branch still never starts when one held branch finishes

## Evidence lane

`code/history` + `local`

## Root cause

**Most likely root cause:** the design fights MAF 1.13 superstep semantics, not just semaphore semantics.

`InProcessRunner.RunSuperstepAsync` in exact MAF 1.13 waits for **every activated executor in the current superstep** to finish before the next superstep can deliver any queued messages.

Relevant 1.13 source:

- `dotnet/src/Microsoft.Agents.AI.Workflows/InProc/InProcessRunner.cs`
  - `RunSuperstepAsync` builds `receiverTasks`
  - then `await Task.WhenAll(receiverTasks)`
  - only after that does it checkpoint and advance
- `dotnet/src/Microsoft.Agents.AI.Workflows/Execution/FanOutEdgeRunner.cs`
  - fan-out activates all selected targets for the same step

That means:

1. **Blocking acquire is dead on arrival.**
   Fan-out activates all acquire executors in one superstep. If any acquire waits on a semaphore, the entire superstep stalls before branch work can proceed.

2. **Non-blocking acquire alone is still not enough.**
   Even after queued branches are marked `Pending`, the held branches themselves run in the same superstep. If branch 0 finishes while branch 1 is still waiting, branch 0's output cannot reach the release executor until branch 1 also finishes, because MAF does not advance to the next superstep yet.

So the current `ParallelGateRuntime` wiring is structurally incompatible with the required behavior:

- release-on-first-finished-branch
- start-next-queued-branch immediately after release
- resume with preserved held permits
- no hang with long-running sibling branches

## Concrete local proof

I ran the failing test individually under the required SDK:

- `mise x dotnet@10.0.301 -- dotnet test ... --filter "DisplayName~parallel enforces maxConcurrency two across five branches"`

Result after wiring the non-blocking path:

- first two branches started
- the third branch never started after releasing one of them

A temporary trace showed:

- setup ran
- branches 0 and 1 acquired permits
- branches 2/3/4 became pending
- branch 0 step completed
- **release executor never ran**

That matches the MAF superstep barrier: branch 0 completed, but branch 1 was still active in the same superstep, so branch 0's completion could not drive the queued release/resume message.

## Why the resume failure also happens

The resume test expects two held branches to be restored and the third branch to remain queued until one held branch completes.

Under the current design, resume state can restore branch statuses, but the runtime still depends on a branch completion message crossing a superstep boundary before the queued branch can start. Because active siblings still pin the superstep, the held-permit reconstruction is not enough to make progress.

## Confidence

**High**

Why:

- direct reading of exact MAF 1.13 source explains both failure modes
- local test behavior matched the source-level explanation exactly
- the failure persists even after removing the semaphore wait from acquire, which rules out a simple semaphore bug

## Smallest viable design direction

A working fix has to stop modeling branch release as ordinary downstream MAF message routing inside the same workflow superstep.

The smallest viable direction appears to be:

- keep MAF for non-parallel workflows
- treat top-level parallel execution as a custom orchestrator outside MAF superstep routing
- run branches as independent child workflow runs
- forward child approval events/results into the parent run
- store per-branch checkpoints/status so resume can rebuild held permits and queued branches

That avoids the superstep barrier entirely.

## Limits

I confirmed the root cause and proved the current gate architecture cannot satisfy the tests under MAF 1.13 semantics, but I did **not** finish the replacement orchestration in this pass.
