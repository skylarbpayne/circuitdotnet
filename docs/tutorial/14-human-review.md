# 14. Pause one lane for human review

## What you will build

You will run three ticket lanes while only the security lane pauses at `Circuit.approval`.

## The idea

`Circuit.start` exposes the structural protocol. Independent lanes keep completing while one request waits. The host answers the exact opaque request ID once; replay is rejected. Circuit does not authenticate or authorize the operator.

## Create or open the project

Open `tutorials/fsharp/14-human-review`. The example is offline. Use `--reject` to exercise rejection; approval is the default.

## Complete source

[!code-fsharp](../../tutorials/fsharp/14-human-review/Program.fs)

The host enumerates `CircuitEvent`, records completed outputs, responds through the same `CircuitRun`, and demonstrates a rejected second response.

## Run it

```bash
dotnet run --project tutorials/fsharp/14-human-review
dotnet run --project tutorials/fsharp/14-human-review -- --reject
```

The output reports how many automatic lanes completed before the review decision.

## What changed

Chapter 13 recovered a provider failure automatically. Chapter 14 introduces an explicit lane-local external decision.

## Check your understanding

1. Why is the request ID single-use?
2. Which authorization duties remain with the host?
3. Why can unrelated lanes continue?

## Try it yourself

Route one more kind through an independent approval and compare the pending requests.

## Recap and next step

- Approval pauses the owning lane.
- Structural request events are lossless under bounded backpressure.
- Hosts own authorization, auditing, timeout, and disposal.

Chapter 15 serializes an active run and resumes it in a second process.
