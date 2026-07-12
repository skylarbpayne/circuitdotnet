# Versioning

Circuit uses explicit semantic versions on the major public definition types:

- agent definitions;
- signatures;
- tools;
- skills;
- workflows.

## Why version values exist

They let you:

- name compatible revisions explicitly;
- bind sessions and checkpoints to the right definition family;
- reject unsafe restores when topology or capability snapshots change.

## Current rules

- Tool resolver identity is unique by tool ID plus **major** version.
- Sessions are bound to runtime/definition/signature/capability snapshots, not only an ID string.
- Workflow checkpoints include definition ID, version, and a topology fingerprint.
- Workflow fingerprints intentionally hash graph topology and declared definition metadata only. They do **not** hash delegates, selector functions, prompt builders, aggregate functions, loop predicates, or other runtime objects.

## Recommended practice

- Bump the major version when you break contract compatibility.
- Keep IDs stable for the same conceptual capability.
- Treat workflow topology changes as checkpoint-breaking unless proven otherwise.
- Version skills whenever their instructions, files, or script contract change in a way that could affect behavior.
- For resumable workflows, **bump the workflow definition semantic version whenever behavior changes inside code delegates, branch selectors, approval prompts, aggregates, or loop predicates**. Those behaviors are outside the fingerprint by design, so the version is the semantic checkpoint boundary.

## Non-guarantees

Circuit does not guarantee:

- automatic migration between versions;
- semantic compatibility checks beyond the explicit validation and fingerprint rules it enforces;
- long-term restore compatibility across breaking adapter changes.
