# 2. Understand the four building blocks

## What you will build

You will run the same live support-ticket agent while printing the names of its four boundaries. The additional lines make it clear which object talks to the provider and which objects describe and execute the typed operation.

## The idea

The working program has four building blocks:

```text
OpenAI -> IChatClient -> ICircuitRuntime
                         |      |
                 AgentDefinition  Signature<TicketInput, TicketOutput>
```

`IChatClient` is Microsoft Extensions AI's provider-facing abstraction; OpenAI's client is adapted to it. `AgentDefinition` gives the assistant a stable ID, version, description, and instructions. The typed `Signature` describes one input-to-output operation. `ICircuitRuntime` executes those definitions against a provider adapter and returns Circuit's result boundary. The definitions describe work; the runtime performs it.

## Create or open the project

Circuit packages are not published yet. Clone the repository and use its checked-in project:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
```

Set both required variables without placing a real key in source or shell history:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Choose a model that supports structured output. Calls can incur provider charges. Environment variables may be visible to other processes in your account; use secret-manager injection in production and clear the key after the tutorial.

## Complete source

[!code-fsharp](../../tutorials/fsharp/02-building-blocks/Program.fs)

The `ChatClient` adapter is held through the `IChatClient` interface. `MafRuntime` is similarly exposed to the application as `ICircuitRuntime`. Neither abstraction contains the support-ticket contract: the agent and signature carry that description, while `TicketInput` and `TicketOutput` remain application-owned types. The provider adapter and timeout are disposed when the program exits.

## Run it

From the repository root:

```bash
dotnet run --project tutorials/fsharp/02-building-blocks
```

Representative output:

```text
Provider boundary: IChatClient
Agent definition: support.agent
Signature: support.reply
Runtime boundary: ICircuitRuntime
Category: Account access
Suggested reply: Check the spam folder and confirm the address before requesting one new reset link.
```

The first four lines are fixed by the program. `Category` and `Suggested reply` are **provider-variable**. A run spends provider tokens and may incur a charge.

## What changed

Chapter 1 already used all four objects. This chapter gives each one a visible name and separates descriptive definitions from the provider-facing client and executing runtime; it does not add another capability.

## Check your understanding

1. Which building block can be replaced to connect a different provider?
2. What is the difference between an agent definition and a typed signature?
3. Why does `Circuit.run over Circuit.agent` accept `ICircuitRuntime` rather than constructing OpenAI itself?

## Try it yourself

Change only the agent's display description from `Support assistant` to `Account support assistant`. Run once and confirm that the printed stable agent ID remains `support.agent` and the typed output still has two fields.

## Recap and next step

- `IChatClient` is the provider-facing boundary.
- Agent and signature definitions describe who performs the work and its typed contract.
- `ICircuitRuntime` executes that contract and returns a structured result.

Chapter 3 adds validation rules to the application-owned input and output types so bad data is stopped at the typed boundary.
