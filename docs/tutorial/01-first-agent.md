# 1. Run your first agent

## What you will build

You will send one support ticket to OpenAI and receive a typed category and suggested reply. The text comes from the configured model, but Circuit makes the shape of the answer explicit.

## The idea

A Circuit takes typed input, asks an agent runtime to produce typed output, and returns either a value or a structured failure.

```text
TicketInput
    |
    v
Signature -> Agent -> Circuit runtime -> OpenAI
    |
    v
TicketOutput or CircuitFailure
```

Your application owns `TicketInput`, `TicketOutput`, configuration, and what it does with the result. Circuit owns typed validation and the success-or-failure boundary. `MafRuntime` connects Circuit to Microsoft Agent Framework, while the OpenAI adapter sends the provider request.

## Create or open the project

Circuit packages are not published yet, so run the checked-in repository project rather than trying to install a package:

First clone the repository:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
```

In Bash, read the key without echoing it or putting it in shell history. A model name is not a credential, so it can use a normal assignment:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

In PowerShell, use a secure prompt and copy the result into the environment only for the process that needs it:

```powershell
$secureKey = Read-Host "OpenAI API key" -AsSecureString
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:OPENAI_MODEL = "a-model-you-have-access-to"
```

These prompts keep the literal key out of shell history. Environment variables can still be visible to processes in your account, so use your platform's secret-manager injection for production and clear the variable when you finish. Choose an OpenAI model available to your account that supports structured output. Provider calls can incur charges, so review OpenAI pricing and your account limits first. Never put a real key in source code or commit it.

## Complete source

[!code-fsharp](../../tutorials/fsharp/01-first-agent/Program.fs)

The two mutable classes are the public data contracts. Their data annotations describe required strings and size limits; Circuit uses those annotations for validation and schema generation. `AgentDefinition` describes the assistant's identity and instructions. `Signature<TicketInput, TicketOutput>` describes the typed operation.

Configuration is checked before `ChatClient` is constructed. The OpenAI client is adapted to `IChatClient`, passed to `MafRuntime`, and disposed after the run. A 30-second cancellation timeout keeps this small console program bounded. Finally, the program handles the returned success or structured failure explicitly instead of assuming a value exists.

A few F# details may be new even if you know the basics:

- `member val Subject = "" with get, set` defines a mutable .NET property. The JSON serializer and data-annotation validator can read and populate these contract properties.
- `task { ... }` builds an asynchronous .NET `Task` while letting the code use `let!` for asynchronous results.
- `:> ICircuitRuntime` explicitly upcasts `MafRuntime` to the runtime interface expected by `Agent.run`.
- `GetAwaiter().GetResult()` is the small console application's bridge from its synchronous entry point into `runAsync`. Application code that is already asynchronous should use `let!` or `do!` instead.

## Run it

From the repository root, run:

```bash
dotnet run --project tutorials/fsharp/01-first-agent
```

Representative output:

```text
Category: Account access
Suggested reply: Check the spam folder, then request one new reset email and verify that the account address is correct.
```

Both lines are **provider-variable**: wording and category can change with the selected model and provider behavior. The stable boundary is that a successful result has a non-empty `category` and `suggestedReply` within the annotated maximum lengths. A configuration, provider, validation, decode, or cancellation problem instead produces a short error and a nonzero exit code.

## What changed

This is the tutorial baseline. It establishes one live provider call, one agent definition, one typed signature, and one explicit result boundary that later chapters will change a little at a time.

## Check your understanding

1. Which types are owned by the support application, and which component talks to OpenAI?
2. Why does the program check both environment variables before constructing `ChatClient`?
3. What stays predictable when the provider-generated category and reply text vary?

## Try it yourself

Change only the ticket subject and message to describe a duplicate charge. Run the chapter once and confirm that the program still prints exactly the two typed fields or one explicit failure; stop after one call to keep the exercise bounded.

## Recap and next step

- An agent definition supplies identity and instructions.
- A signature makes the input and output boundary typed and validated.
- A Circuit run returns a value or a structured failure and honors cancellation.

Chapter 2 keeps the same working support-ticket flow and names the four building blocks—provider client, agent, signature, and runtime—more precisely.
