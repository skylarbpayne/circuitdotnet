# 18. Switch providers at one seam

## What you will build

You will run the same typed support-ticket operation through either an OpenAI or Azure OpenAI `IChatClient`. One environment variable selects the registration; the agent, signature, input, Circuit runtime, and result handling stay shared.

## The idea

Circuit depends on the provider-neutral `Microsoft.Extensions.AI.IChatClient` interface:

```text
OpenAI ChatClient -------\
                          -> IChatClient -> MafRuntime -> same typed Circuit
Azure OpenAI ChatClient -/
```

Provider neutrality means application code can keep one seam. It does **not** mean every client, model, or capability behaves identically. Compilation proves that a registration is well formed; only an explicit live check can establish compatibility for a provider, model/deployment, and feature.

## Create or open the project

Choose exactly one provider and supply every required value. No model or deployment has a checked-in default.

For OpenAI in Bash:

```bash
export CIRCUIT_PROVIDER="openai"
read -rsp "OpenAI API key: " OPENAI_API_KEY; echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

For Azure OpenAI:

```bash
export CIRCUIT_PROVIDER="azure-openai"
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT="your-deployment-name"
read -rsp "Azure OpenAI API key: " AZURE_OPENAI_API_KEY; echo
export AZURE_OPENAI_API_KEY
```

PowerShell 7 can read each key with `Read-Host -MaskInput` and assign it to the matching `$env:` variable. Prompting avoids placing the key literal in shell history, but environment variables remain visible to the current process and some diagnostics. Production hosts should use managed identity or their protected secret-injection mechanism where supported.

Both providers may charge for requests. Run only against accounts and deployments you are authorized to use.

## Complete source

[!code-fsharp](../../tutorials/fsharp/18-switch-providers/Program.fs)

`parseProvider` accepts only the two documented names. `requiredEnvironment` checks configuration before constructing a client. Each branch adapts its provider SDK client to `IChatClient`; after that branch, `runAsync` is literally shared.

Azure OpenAI uses the required deployment name as its model route. OpenAI requires an explicit model ID. The application never guesses either value and disposes the selected `IChatClient` after the bounded run. The structural observer and console exporters from Chapter 17 remain unchanged, with all four payload-capture switches still `false`.

## Run it

After configuring one provider, run the same command:

```bash
dotnet run --project tutorials/fsharp/18-switch-providers
```

Representative output:

```text
Run succeeded. Category: Account access; suggested reply length: 142
Activity.DisplayName: circuit.run
    circuit.definition.id: support.reply
    circuit.status: success
```

The category, reply length, IDs, timings, and token counts are provider-variable. Ticket, prompt, reply, and key payloads remain excluded from Circuit telemetry. A missing selection exits with code 2; missing provider-specific configuration names the required variables without printing their values. Provider, decode, validation, and cancellation problems follow the same Circuit failure boundary used in earlier chapters.

Neither registration has live compatibility evidence checked into this tutorial. Consult the [provider compatibility matrix](../reference/provider-compatibility.md) before making a support claim.

## What changed

Chapter 17 attached telemetry to one OpenAI runtime. Chapter 18 changes only client construction: two provider-specific registrations converge on one `IChatClient` and the entire typed support-ticket application remains unchanged.

## Check your understanding

1. Which code is provider-specific, and where does both registration paths converge?
2. Why is Azure's deployment required instead of falling back to a name in source?
3. Why does successful compilation not prove structured output, tools, sessions, or streaming work for a particular live model?

## Try it yourself

Run the program once with `CIRCUIT_PROVIDER` set to an unsupported value and confirm it exits before requesting credentials or creating a client. If you own both configured accounts, compare one bounded call per provider; otherwise stop without making a live request.

## Recap and next step

- Keep provider construction at the `IChatClient` composition seam.
- Require explicit provider, model/deployment, endpoint, and secret configuration.
- Treat capability compatibility as evidence to record, not an inference from an interface or successful build.

Anthropic is intentionally not registered here: this repository has no verified compatibility evidence for it. The complete tutorial now leaves you with one typed application that can grow by changing explicit capabilities and host policy rather than abandoning its contracts.
