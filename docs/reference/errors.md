# Errors

Unified F# and C# executions report expected failures through `Response<'T>` / `CircuitResponse<T>` and classify them with `CircuitFailureCode`. The full event protocol carries the same failure object in node and terminal responses. Exceptions remain available only as trusted diagnostics; applications should branch on the code rather than parse messages.

## Error codes

| Code | Meaning | Typical trigger |
| --- | --- | --- |
| `Validation` | A declared input or output contract was rejected. | Missing required input or custom validator rejection. |
| `StructuredOutputUnsupported` | The requested structured-output policy is unavailable. | Repair requested without a repair client or provider limitation. |
| `Decode` | A provider or tool payload could not be decoded into its declared type. | Malformed JSON, null output, or wrapper-shape mismatch. |
| `Provider` | The provider request failed. | Transport failure, client exception, or provider rejection. |
| `Tool` | Tool resolution, validation, or execution failed. | Resolver failure, handler exception, or invalid tool output. |
| `ApprovalRequired` | A projection encountered work that requires interactive approval. | Calling `run` or `collect` for an approval-bearing Circuit. |
| `Skill` | Skill resolution or execution failed. | Missing skill content, resolver failure, or script error. |
| `Engine` | Trusted Circuit scheduling or code execution failed. | Code-node exception or invalid branch selection. |
| `CheckpointMismatch` | Durable state does not match the exact Circuit definition. | Definition, version, fingerprint, session, or generated-child drift. |
| `Cancelled` | The run was cancelled. | Caller cancellation or run disposal. |
| `NotCheckpointable` | The active graph cannot produce a durable checkpoint. | A plain asynchronous source is present. |
| `Cardinality` | A projection received the wrong number of root outputs. | `Circuit.run` observed zero or multiple outputs. |
| `DuplicateItemKey` | One source produced a duplicate stable lane key. | A keyed source returned the same key twice. |
| `ResourceLimit` | A configured hard bound was exceeded. | Too many pages, generated nodes, approvals, or loop iterations. |
| `GeneratedGraphIntegrity` | A dynamic child could not be safely built or validated. | Invalid generated graph or factory exception. |
| `InvalidApprovalResponse` | An approval response was unknown, mismatched, or already consumed. | Cross-run response, wrong request ID, or replay. |

## Message design

Circuit sanitizes public adapter-boundary messages to preserve classification without leaking provider payloads, secret paths, or internal exception text. Codes do not imply retry safety; retries and recovery remain application decisions.

## Non-guarantees

Circuit does not guarantee provider-specific subcodes, stable exception text, automatic retry safety, or compatibility with unknown future numeric enum values.
