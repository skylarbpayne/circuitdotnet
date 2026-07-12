module Circuit.Tutorial.Testing.Tests

open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Threading
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing
open Xunit

[<AllowNullLiteral>]
type TicketInput() =
    [<property: Required; StringLength(120, MinimumLength = 3)>]
    member val Subject = "" with get, set

    [<property: Required; StringLength(2000, MinimumLength = 10)>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type TicketOutput() =
    [<property: Required; StringLength(40, MinimumLength = 3)>]
    member val Category = "" with get, set

    [<property: Required; StringLength(500, MinimumLength = 10)>]
    member val SuggestedReply = "" with get, set

let private agent =
    AgentDefinition.create
        "support.agent"
        "1.0.0"
        "Support assistant"
        "Read the support ticket. Return a short category and a helpful suggested reply."

let private signature =
    Signature.create<TicketInput, TicketOutput>
        "support.reply"
        "1.0.0"
        "Support ticket reply"
        "Return category and suggestedReply as structured output."

let private ticket =
    TicketInput(
        Subject = "Password reset email never arrived",
        Message = "I requested a password reset twice, but no email has arrived."
    )

let private collectAsync (stream: System.Collections.Generic.IAsyncEnumerable<'T>) =
    task {
        let items = ResizeArray<'T>()
        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable reading = true

            while reading do
                let! moved = enumerator.MoveNextAsync().AsTask()

                if moved then
                    items.Add(enumerator.Current)
                else
                    reading <- false
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        return items |> Seq.toArray
    }

[<Fact>]
let ``scripted ticket runs are typed ordered and offline`` () =
    task {
        let providerFailure =
            TestFailures.Create(CircuitFailureCode.Provider, "The scripted provider is unavailable.")

        let runtime =
            ScriptedRuntime(
                [ ScriptedResponses.OutputJson(
                      """{"category":"account access","suggestedReply":"Check the spam folder and request one new reset email."}"""
                  )
                  ScriptedResponses.Stream(
                      [ """{"category":"account access","suggestedReply":"Check """
                        """the spam folder."}""" ]
                  )
                  ScriptedResponses.Failure(providerFailure) ]
            )

        let circuitRuntime = runtime :> ICircuitRuntime

        let! normal = Agent.run circuitRuntime agent signature ticket RunOptions.Default CancellationToken.None

        Assert.True(normal.Result.IsSuccess)
        Assert.Equal("account access", normal.Result.Value.Category)
        Assert.StartsWith("Check the spam folder", normal.Result.Value.SuggestedReply)

        let! streamEvents =
            circuitRuntime.RunStreamingAsync(agent, signature, ticket, RunOptions.Default, CancellationToken.None)
            |> collectAsync

        RunAssertions.AssertMonotonicSequence(streamEvents)
        RunAssertions.AssertTerminalEventCount(streamEvents, 1)
        Assert.Equal(RunEventKind.RunCompleted, streamEvents[streamEvents.Length - 1].Kind)
        Assert.Equal("account access", streamEvents[streamEvents.Length - 1].Value.Value.Category)

        let! failed = Agent.run circuitRuntime agent signature ticket RunOptions.Default CancellationToken.None

        Assert.False(failed.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Provider, failed.Result.Failure.Code)
        Assert.Equal("The scripted provider is unavailable.", failed.Result.Failure.Message)

        Assert.Equal(3, runtime.Calls.Count)
        Assert.Equal(0, runtime.RemainingResponses)
        Assert.Equal(ScriptedCallKind.Run, runtime.Calls[0].Kind)
        Assert.Equal(ScriptedCallKind.Streaming, runtime.Calls[1].Kind)
        Assert.Equal(ScriptedCallKind.Run, runtime.Calls[2].Kind)

        runtime.Calls
        |> Seq.iter (fun call ->
            Assert.Equal("support.agent", call.AgentId)
            Assert.Equal("1.0.0", call.AgentVersion)
            Assert.Equal("support.reply", call.SignatureId)
            Assert.Equal("1.0.0", call.SignatureVersion)

            use recordedInput = JsonDocument.Parse(call.InputJson)
            Assert.Equal(ticket.Subject, recordedInput.RootElement.GetProperty("subject").GetString())
            Assert.Equal(ticket.Message, recordedInput.RootElement.GetProperty("message").GetString())
            Assert.Equal(signature.Input.Schema.ToJsonString(), call.InputSchemaJson)
            Assert.Equal(signature.Output.Schema.ToJsonString(), call.OutputSchemaJson))
    }
