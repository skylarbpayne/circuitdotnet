module Tutorial.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing
open Xunit

type Ticket = { Id: string; Kind: string }

let signature =
    Signature.create<Ticket, string> "ticket-output" "1.0.0" "Ticket output" "Return a JSON string."

let agent id =
    AgentDefinition.create id "1.0.0" id "Return the scripted ticket result."

let tickets: IReadOnlyList<Ticket> =
    [| { Id = "ticket-1"; Kind = "billing" }
       { Id = "ticket-2"; Kind = "security" }
       { Id = "ticket-3"; Kind = "provider" } |]

let source =
    Circuit.keyedItems "test-tickets" "1.0.0" _.Id (fun (_: unit) -> tickets)

let child ticket =
    match ticket.Kind with
    | "security" ->
        Circuit.agent (agent "security-agent") signature
        |> Circuit.thenStep (
            Circuit.code "security-audit" "1.0.0" (fun context value ->
                Task.FromResult(Response.succeed context (value + ":audited")))
        )
    | "provider" ->
        Circuit.agent (agent "provider-agent") signature
        |> Circuit.recover "provider-recovery" "1.0.0" (fun failure -> $"recovered:{failure.Code}")
    | _ -> Circuit.agent (agent "billing-agent") signature

let pipeline =
    source
    |> Circuit.thenDynamic "test-route" "1.0.0" _.Id 3 child
    |> Circuit.define "scripted-pipeline" "1.0.0"

[<Fact>]
let ``scripted runtime deterministically tests dynamic concurrent pipeline`` () =
    task {
        let providerFailure =
            TestFailures.Create(CircuitFailureCode.Provider, "controlled provider failure")

        let runtime =
            ScriptedRuntime(
                [| ScriptedResponses.ForNode("billing-agent.ticket-output", ScriptedResponses.OutputValue("billing"))
                   ScriptedResponses.ForNode(
                       "security-agent.ticket-output",
                       ScriptedResponses.Stream([ "\"sec"; "urity\"" ])
                   )
                   ScriptedResponses.ForNode("provider-agent.ticket-output", ScriptedResponses.Failure(providerFailure)) |]
            )

        let options = RunOptions.Default.WithMaxConcurrency(3).WithEventBufferCapacity(2)
        let! result = Circuit.collect (runtime :> ICircuitRuntime) pipeline () options CancellationToken.None
        Assert.True(result.IsSuccess, if result.IsSuccess then "" else result.Failure.Message)
        let values = result.Value |> Seq.map _.Value |> Seq.sort |> Seq.toArray
        Assert.Equal<string[]>([| "billing"; "recovered:Provider"; "security:audited" |], values)
        Assert.Equal(0, runtime.RemainingResponses)
        Assert.Equal(3, runtime.Calls.Count)
        Assert.Single(runtime.Calls |> Seq.map _.RunId |> Seq.distinct) |> ignore
        Assert.Equal(3, runtime.Calls |> Seq.map _.NodePath |> Seq.distinct |> Seq.length)
    }
