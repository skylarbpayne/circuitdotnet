# 12. Run independent work in parallel

## What you will build

You will ask three live agents to analyze one ticket from sentiment, risk, and routing perspectives. At most two calls are active together, while the returned list remains in declaration order.

## The idea

Independent work does not need to wait in a chain. `Circuit.``parallel`` 2` starts children subject to a maximum of two active operations:

```text
             +-> sentiment --+
Ticket ------+-> risk -------+-> [sentiment; risk; routing]
       bound +-> routing ----+
              max active = 2
```

The list is returned in declaration order even when calls finish in another order. Runtime operation IDs reflect actual scheduling, not list position. If one child fails, Circuit cancels its siblings and drains started work before returning. Provider cancellation is cooperative and may arrive after a request was accepted, so cancellation does not guarantee zero charge. Unbounded fan-out can exhaust sockets, quotas, rate limits, and budgets.

## Create or open the project

From a repository clone, open `tutorials/fsharp/12-parallel-programs`. Configure the live provider explicitly:

```bash
read -rsp "OpenAI API key: " OPENAI_API_KEY && echo
export OPENAI_API_KEY
export OPENAI_MODEL="a-model-you-have-access-to"
```

Choose a model with structured-output support. This run can make three provider calls and incur three calls' cost. Never commit credentials; production deployments should inject them from a secret store.

## Complete source

[!code-fsharp](../../tutorials/fsharp/12-parallel-programs/Program.fs)

Each list item is a real `Circuit.call` with a distinct agent definition and the same typed signature. The bound is `2`, not the number of children. `Circuit.run` owns cancellation and combines the children into one result.

## Run it

```bash
dotnet run --project tutorials/fsharp/12-parallel-programs
```

Representative output (**findings are provider-variable**):

```text
Analyses (declaration order):
1. Sentiment: The customer sounds concerned but patient.
2. Risk: Verify the account address before changing credentials.
3. Routing: Route to account access support.
```

Completion timing can vary, but successful output positions are stable. Missing environment variables fail before client construction; provider and timeout failures are reported without exception details.

## What changed

Chapter 11 sequenced classification before drafting because the second call needed the first result. Chapter 12 identifies genuinely independent work and uses bounded parallel composition instead.

## Check your understanding

1. Why can completion order differ from result order?
2. What resources and costs does the bound protect?
3. Why can a cancelled sibling still consume provider time or money?

## Try it yourself

Change the bound from `2` to `1` and run once. Confirm the three output positions remain the same even though the calls are now scheduled serially.

## Recap and next step

- Parallelism is appropriate only when children do not depend on one another.
- The maximum concurrency bound limits active work; it does not change declaration-order results.
- Failure cancels siblings cooperatively, but cancellation timing cannot reverse provider work already accepted.

Chapter 13 moves composition into a named, validated workflow when explicit topology becomes useful.
