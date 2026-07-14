# 10. Add a skill

## What you will build

Attach versioned support-policy guidance to the agent and use it in a live ticket response. The skill is inline, contains no scripts, and is resolved from an explicit fixed catalog.

## The idea

A skill contributes reusable guidance; a tool executes host code:

```text
skill -> instructions available to agent
tool  -> model-requested host operation
```

The agent references `skill.support-policy@1.0.0`, and `StaticSkillResolver` supplies that exact version. Versioning makes the guidance dependency visible. This source is script-free, and `SkillScriptRunner` remains `ValueNone`.

Skill instructions can influence model behavior but do not enforce business policy. Scripts require an explicitly configured, trusted runner; Circuit does not sandbox them. Treat externally sourced skill text as untrusted content and review it before registration.

## Create or open the project

From the repository root, install the SDK selected by `global.json`, then set reader-owned provider configuration:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY; echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

The chapter makes a paid OpenAI request. It has no credential or model default and no fake fallback when provider execution fails.

## Complete source

[!code-fsharp](../../tutorials/fsharp/10-skills/Program.fs)

`SkillSource.CreateInline` contains only policy prose. `AgentDefinition.withSkills` records the dependency, while the runtime resolver materializes it for this run.

## Run it

```bash
dotnet run --project tutorials/fsharp/10-skills
```

Representative output (category and reply are provider-variable):

```text
Policy skill: skill.support-policy@1.0.0
Category: account-access
Suggested reply: Check spam and verify the email address on the account...
```

## What changed

Chapter 9 introduced controlled host execution. Chapter 10 adds reusable versioned instructions without exposing an executable capability.

## Check your understanding

1. What does a skill contribute that a tool does not?
2. Why is the skill version part of the agent dependency?
3. What changes before a skill script could execute?

## Try it yourself

Change the inline guidance to require replies under two sentences, increment the skill version to `1.0.1`, and update the output. Observe the provider-variable response length.

## Recap and next step

- Skills provide versioned guidance rather than host operations.
- Inline, script-free sources keep this chapter's capability narrow.
- Script execution requires an explicit trusted runner and is not sandboxed.

Next, compose multiple typed calls into a lightweight graph-backed Circuit composition program.
