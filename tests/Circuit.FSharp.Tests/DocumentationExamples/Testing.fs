namespace Circuit.FSharp.Tests.DocumentationExamples

open System.ComponentModel.DataAnnotations
open System.Threading
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing

[<AllowNullLiteral>]
type PingInput() =
    [<property: Required>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type PongOutput() =
    [<property: Required>]
    member val Message = "" with get, set

module TestingExample =
    let run () =
        let runtime =
            ScriptedRuntime(
                [ ScriptedResponses.OutputJson "{\"message\":\"pong\"}"
                  ScriptedResponses.Stream [ "{\"message\":\"po"; "ng\"}" ] ]
            )
            :> ICircuitRuntime

        let agent = AgentDefinition.create "testing.agent" "1.0.0" "Testing" "Return pong."

        let signature =
            Signature.create<PingInput, PongOutput> "testing.signature" "1.0.0" "Ping" "Return pong."

        let first =
            Agent.run runtime agent signature (PingInput(Message = "ping")) RunOptions.Default CancellationToken.None
            |> _.Result

        let events = ResizeArray<RunEvent<PongOutput>>()

        let stream =
            runtime.RunStreamingAsync(
                agent,
                signature,
                PingInput(Message = "stream"),
                RunOptions.Default,
                CancellationToken.None
            )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    events.Add enumerator.Current
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().Wait()

        RunAssertions.AssertMonotonicSequence(events)
        RunAssertions.AssertTerminalEventCount(events, 1)
        first.Result.Value.Message
