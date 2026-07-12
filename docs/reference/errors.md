# Errors

Public C# runs surface failures through `AgentFailure` and `AgentFailureCode`. Core F# APIs surface the same categories through `CircuitFailure` and `CircuitFailureCode`.

## Error codes

| Code | Meaning | Typical trigger |
| --- | --- | --- |
| `Validation` | Contract validation failed. | Missing required input, custom validator rejection, tool output validation failure. |
| `StructuredOutputUnsupported` | The selected runtime or policy cannot provide the requested structured-output behavior. | Repair requested without a repair client, provider/runtime limitation. |
| `Decode` | Circuit could not decode the provider or tool payload into the declared output type. | Malformed JSON, null where not allowed, wrapper-shape mismatch. |
| `Provider` | The provider call failed. | Client exception, transport failure, provider-side rejection. |
| `Tool` | Tool resolution or execution failed. | Handler exception, invalid tool configuration, tool contract mismatch. |
| `ApprovalRequired` | Execution paused for approval or approval handling failed. | HITL step or tool approval gate. |
| `Skill` | Skill resolution or script execution failed. | Missing skill content, resolver failure, script-runner error. |
| `Workflow` | Workflow execution failed. | Invalid branch selection, step exception, aggregate failure. |
| `CheckpointMismatch` | A checkpoint does not match the current workflow definition. | Version or topology drift on resume. |
| `Cancelled` | The caller cancelled the operation. | Cancellation token requested before or during execution. |

## Message design

Circuit intentionally sanitizes public error messages at adapter boundaries. The goal is to preserve the failure category without leaking raw provider payloads, secret file paths, or internal exception text.

## Non-guarantees

Circuit does not guarantee:

- provider-specific subcodes on the public failure object;
- stable internal exception text for programmatic parsing;
- recoverability from a given error without application-specific retry logic.
