# Security model

Circuit is a typed composition library, not a sandbox.

## What Circuit does enforce

- contract validation for inputs and outputs;
- explicit tool approval modes;
- safe file-root checks for file-backed skills;
- session binding checks in the MAF adapter;
- optional public-failure redaction and observer payload redaction hooks.

## What stays in your control plane

You still own:

- provider credentials and network policy;
- tool-side authorization and idempotency;
- skill-script execution environment;
- telemetry retention and downstream redaction;
- approval identity, audit, and policy decisions.

## Threat-model summary

- **Tools** can read or write real systems. Treat them like application code.
- **Skills** can influence model behavior. Treat file skills as trusted prompt supply.
- **Skill scripts** are arbitrary code if you enable them.
- **Sessions and checkpoints** can carry sensitive model/runtime state.
- **Observers** can leak payloads if you opt in to capture.

## Non-guarantees

Circuit does not guarantee:

- sandboxing;
- secret scrubbing outside its public adapter boundaries;
- provider isolation;
- complete policy enforcement without your host application.
