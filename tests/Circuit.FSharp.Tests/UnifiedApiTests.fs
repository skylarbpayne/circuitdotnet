module Circuit.FSharp.Tests.UnifiedApiTests

open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing
open Xunit

type Input = { Text: string }
type Output = { Text: string }
type Summary = { Total: int; Count: int }

let private agent = AgentDefinition.create "echo-agent" "1.0.0" "Echo" "Echo input"

let private signature =
    Signature.create<Input, Output> "echo" "1.0.0" "Echo" "Return output"

[<Fact>]
let ``FSharp agent Circuit runs through scripted scheduler`` () =
    task {
        let runtime = ScriptedRuntime([ ScriptedResponses.OutputValue({ Text = "done" }) ])
        let definition = Circuit.agent agent signature
        let! response = Circuit.run runtime definition { Text = "go" } RunOptions.Default CancellationToken.None

        let detail =
            if response.IsSuccess then
                ""
            else
                response.Failure.Message
                + " "
                + (response.Failure.Exception
                   |> ValueOption.map string
                   |> ValueOption.defaultValue "")

        Assert.True(response.IsSuccess, detail)
        Assert.Equal("done", response.Value.Text)
        Assert.Single(runtime.Calls) |> ignore
    }

[<Fact>]
let ``scripted stream deltas are observational scheduler events`` () =
    task {
        let runtime = ScriptedRuntime([ ScriptedResponses.Stream([ "\"do"; "ne\"" ]) ])

        let streamSignature =
            Signature.create<string, string> "stream" "1.0.0" "Stream" "Return output"

        let! run =
            Circuit.start runtime (Circuit.agent agent streamSignature) "go" RunOptions.Default CancellationToken.None

        let events = ResizeArray<CircuitEvent<string>>()
        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                events.Add enumerator.Current

        do! enumerator.DisposeAsync().AsTask()

        Assert.Equal(
            2,
            events
            |> Seq.filter (function
                | OutputDelta _ -> true
                | _ -> false)
            |> Seq.length
        )

        Assert.Single(
            events
            |> Seq.filter (function
                | OutputProduced _ -> true
                | _ -> false)
        )
        |> ignore

        do! (run :> System.IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``FSharp aggregate creates a new output type`` () =
    task {
        let runtime = ScriptedRuntime([])

        let source =
            Circuit.items "aggregate-items" "1.0.0" (fun (_: unit) -> [| 1; 2; 3 |])

        let definition =
            source
            |> Circuit.aggregate "summary" "1.0.0" (fun context responses _ ->
                { Total = responses |> Seq.sumBy _.Value
                  Count = responses.Count }
                |> Response.succeed context
                |> Task.FromResult)

        let! response = Circuit.run runtime definition () RunOptions.Default CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal({ Total = 6; Count = 3 }, response.Value)
    }

[<Fact>]
let ``FSharp facade exposes finite collection and source ordering`` () =
    task {
        let runtime = ScriptedRuntime([])
        let source = Circuit.items "items" "1.0.0" (fun (_: unit) -> [| 3; 1; 2 |])
        let! response = Circuit.collectSourceOrder runtime source () RunOptions.Default CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal<int list>([ 3; 1; 2 ], response.Value |> Seq.map _.Value |> Seq.toList)
    }
