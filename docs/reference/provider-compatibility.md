# Provider compatibility

Circuit's adapter logic is covered by automated tests, but **this repository does not currently publish live-provider compatibility claims beyond what is explicitly recorded here**.

Compilation, fake clients, and adapter unit tests do **not** prove live support.

## Matrix

| Provider/client | Model | Native object output | Native primitive output | Native array output | Tools | Approvals | Streaming | Sessions | Test date | Known limitations |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Circuit MAF adapter (`CircuitDotNet.MicrosoftAgentFramework`) | adapter-only | Fake-client tested only | Fake-client tested only | Fake-client tested only | Fake-client tested only | Fake-client tested only | Fake-client tested only | Fake-client tested only | 2026-07-11 | This is adapter behavior coverage, not a live-provider claim. |
| OpenAI via `Microsoft.Extensions.AI` | — | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | — | No live verification in this repo yet. |
| Azure OpenAI via `Microsoft.Extensions.AI` | — | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | — | No live verification in this repo yet. |
| Anthropic via `Microsoft.Extensions.AI` | — | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | Not yet recorded | — | No live verification in this repo yet. |

Manual live checks are recorded by the checked-in `tests/Circuit.ProviderContract` harness. Each run writes redacted artifacts under `artifacts/provider-contract/<provider>/<UTC-date>/`, including capability pass/fail state, request IDs where available, token totals, estimated cost totals, package/model metadata, and trace summaries with sensitive payload capture disabled.

## How to read this table

- `Recorded` means this repository has explicit evidence for that provider/model pair.
- `Not yet recorded` means Circuit makes no compatibility claim yet.
- The adapter row exists to show what Circuit's own tests cover without overstating provider support.
