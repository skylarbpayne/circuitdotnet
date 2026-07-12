# 17. Observe runs without recording prompts

## What you will build

You will send the support ticket to OpenAI and export Circuit traces and metrics to the console. The observer records lifecycle structure while its four payload-capture switches stay off.

## The idea

Observability answers questions such as “Which definition failed?”, “How long did runs take?”, and “How many operations were cancelled?” It should not silently turn tickets, prompts, replies, tool arguments, or credentials into telemetry.

```text
MafRuntime events -> OpenTelemetryRunObserver -> CircuitDotNet ActivitySource/Meter
                                                    |
                                                    v
                                             console exporters
```

Trace records include run and operation correlation IDs. Metric dimensions are deliberately bounded to definition ID/version, operation kind, status, and related structural fields; they do not use ticket text or run IDs as metric labels.

## Create or open the project

Use the same reader-owned OpenAI configuration as earlier live chapters. In Bash, prompt without echoing the key into terminal or shell history:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY; echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

In PowerShell 7, use `$env:OPENAI_API_KEY = Read-Host "OpenAI API key" -MaskInput`, then set `$env:OPENAI_MODEL` to a model you can use. Environment variables are still readable by the current process and some diagnostic tools; a production host should inject secrets through its managed secret store.

Provider calls can incur charges. Keep the exercise to one request and never commit credentials.

## Complete source

[!code-fsharp](../../tutorials/fsharp/17-telemetry/Program.fs)

The tracer and meter providers subscribe only to Circuit's `CircuitDotNet` sources. `OpenTelemetryRunObserver` receives MAF lifecycle events through `MafRuntimeOptions.Observers`.

All capture switches are explicitly `false`. The application prints only the resulting category and reply length, rather than the ticket, agent prompt, generated reply, or API key. Disposing the providers at program exit flushes the console exporters.

## Run it

From the repository root:

```bash
set -o pipefail
dotnet run --project tutorials/fsharp/17-telemetry | tee /tmp/circuit-telemetry.txt
```

Representative, abbreviated output:

```text
Run succeeded. Category: Account access; suggested reply length: 142
Activity.TraceId: ...
Activity.DisplayName: circuit.run
    circuit.definition.id: support.reply
    circuit.status: success
Metric Name: circuit.runs
Value: 1
```

Exact model text, IDs, timings, token counts, and exporter formatting are provider-variable. With the checked-in ticket unchanged, this command should print no matches:

```bash
if grep -E 'Password reset|requested a password|sk-' /tmp/circuit-telemetry.txt; then
  echo "unexpected payload-shaped text" >&2
  exit 1
else
  echo "no checked payload marker found"
fi
```

This is a useful regression check, not proof that arbitrary data has been redacted. The protection here is keeping payload capture disabled.

## What changed

Chapter 16 was an offline xUnit test project. Chapter 17 intentionally returns to a live executable and adds one operational concern: a structural OpenTelemetry observer plus trace and metric exporters are attached to the familiar support-agent run.

## Check your understanding

1. Why are definition ID and status suitable metric labels while ticket text and run ID are not?
2. Which four observer options prevent prompt, input, output, and tool-argument capture?
3. Why is grepping one run's output weaker than leaving capture disabled by design?

## Try it yourself

Run one request and find `circuit.definition.id` and `circuit.status` in the console export. Do not enable payload capture. Confirm the checked ticket markers are absent, then delete `/tmp/circuit-telemetry.txt` because diagnostic files can still be sensitive.

## Recap and next step

- Subscribe exporters to Circuit's `ActivitySource` and `Meter` name, `CircuitDotNet`.
- Keep payload capture off unless you have an explicit policy, redaction strategy, and protected sink.
- Use structural traces for correlation and bounded metric labels for aggregation.

Chapter 18 keeps this typed application boundary and selects either OpenAI or Azure OpenAI through the provider-neutral `IChatClient` seam.
