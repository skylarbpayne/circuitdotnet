# Versioning

Circuit uses explicit semantic versions for agents, signatures, tools, skills, and root `Circuit<'Input,'Output>` definitions.

## Why versions exist

Versions name compatible revisions, bind sessions and checkpoints to the intended definition family, and make unsafe durable restores fail before resumed work begins.

## Current rules

- Tool resolver identity is unique by tool ID plus major version.
- Sessions bind to the runtime adapter, agent definition, signature, tenant/user context, and resolved capabilities.
- A `CircuitCheckpoint<'Output>` records the root definition ID, semantic version, exact structural fingerprint, and durable lineage.
- The fingerprint covers graph topology, node identities and versions, declared types, and frozen constant values.
- Delegates such as selectors, prompt builders, code handlers, aggregate handlers, and loop predicates cannot be fingerprinted.
- Generated children are materialized, validated, and fingerprint-checked on resume before their evaluation continues.

## Recommended practice

- Bump the major version for contract-breaking input, output, or capability changes.
- Keep IDs stable only for the same conceptual definition.
- Treat graph topology changes as checkpoint-breaking.
- Bump a root Circuit version whenever behavior changes inside an unhashable delegate, even if topology is unchanged.
- Version skills when instructions, files, or script contracts can change behavior.

Circuit does not migrate checkpoints automatically or infer semantic compatibility beyond explicit versions, validation, and fingerprints. Adapter format changes may also be checkpoint-breaking.
