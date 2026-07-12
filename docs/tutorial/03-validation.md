# 3. Validate input and output

## What you will build

You will add data-annotation rules to both sides of the support-ticket signature. The normal path makes one live request; `--invalid` supplies a deliberately invalid ticket and returns a validation failure before provider execution.

## The idea

Validation protects both edges of a typed run:

```text
input annotations -> validate -> provider -> decode -> output annotations
                         X                         X
```

Circuit validates `TicketInput` before asking the runtime to execute provider work. After provider output is decoded into `TicketOutput`, Circuit validates that object too. Input validation prevents known-bad application data from crossing the provider boundary; output validation prevents a decoded but contract-invalid answer from being accepted. It does not make model text factually correct.

## Create or open the project

Run the checked-in repository project because Circuit packages are not published:

```bash
git clone https://github.com/skylarbpayne/circuitdotnet.git
cd circuitdotnet
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Both variables are preflight requirements for every mode, and there is no model default. Use a model available to your account that supports structured output. The default live call spends tokens and can incur a charge. Keep keys out of source and shell history, prefer secret-manager injection outside this tutorial, and clear the key afterward.

## Complete source

[!code-fsharp](../../tutorials/fsharp/03-validation/Program.fs)

`Required` rejects missing or empty strings. `StringLength` now supplies both minimum and maximum lengths. The same annotation mechanism contributes constraints to the provider schema and validates the decoded output locally. The invalid branch changes only the input values; it still calls the public `Agent.run` path, whose ordering guarantees input validation before provider execution.

## Run it

First run the default live path:

```bash
dotnet run --project tutorials/fsharp/03-validation
```

Representative live output:

```text
Category: Account access
Suggested reply: Verify the account email, check spam, and request one fresh reset link.
```

Those two values are **provider-variable**.

Then run the controlled invalid path:

```bash
dotnet run --project tutorials/fsharp/03-validation -- --invalid
```

Representative boundary (individual validation wording can evolve):

```text
Validation rejected the typed contract before the run could continue: <validation summary>
```

The invalid path is local and does not send the ticket to OpenAI. This follows Circuit's validated execution order; paid live-provider validation is not part of CI.

## What changed

Compared with chapter 2, the four building blocks are unchanged. Only minimum-length rules and an invalid input branch were added, making the existing typed boundary observable on both input and output.

## Check your understanding

1. Why must input validation happen before provider execution?
2. When is `TicketOutput` validated?
3. What important property of a suggested reply is not guaranteed by data annotations?

## Try it yourself

Raise `TicketInput.Message`'s minimum length from 10 to 20, then change the `--invalid` branch's message to `Still too short`. Run only `--invalid` and confirm the result remains a validation failure rather than typed output.

## Recap and next step

- Input annotations reject invalid application data before provider execution.
- Output annotations reject decoded values that violate the typed contract.
- Validation constrains shape and declared rules, not factual quality.

Chapter 4 treats validation as one member of the complete structured-failure set and gives each failure category a safe user-facing response.
