namespace Circuit.FSharp.Tests

open System
open System.Collections.Generic
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core
open Circuit.FSharp
open Circuit.Testing
open Xunit

[<AllowNullLiteral>]
type TestingInput() =
    member val Name = "" with get, set

[<AllowNullLiteral>]
type TestingOutput() =
    member val Text = "" with get, set

type private NullServiceProvider() =
    interface IServiceProvider with
        member _.GetService(_serviceType) = null

module TestingRuntimeTests =
    let private runOptionsCtor =
        typeof<RunOptions>
            .GetConstructor(
                BindingFlags.Instance ||| BindingFlags.NonPublic,
                null,
                [| typeof<CircuitSession voption>
                   typeof<string voption>
                   typeof<string voption>
                   typeof<IReadOnlyDictionary<string, string>>
                   typeof<StructuredOutputPolicy>
                   typeof<SensitiveDataMode>
                   typeof<IServiceProvider> |],
                null
            )

    let private envelopeFactory =
        let methodInfo =
            typeof<RunEventEnvelope>.GetMethod("Create", BindingFlags.Static ||| BindingFlags.NonPublic)

        if isNull methodInfo then
            invalidOp "Could not access the internal RunEventEnvelope factory."

        methodInfo

    let private createAgent () =
        AgentDefinition.create "testing.agent" "1.0.0" "Testing Agent" "Return the scripted payload."

    let private createSignature () =
        Signature.create<TestingInput, TestingOutput>
            "testing.signature"
            "1.0.0"
            "Testing signature"
            "Return the scripted payload."

    let private createRunOptions (tags: IReadOnlyDictionary<string, string>) =
        runOptionsCtor.Invoke(
            [| box (ValueNone: CircuitSession voption)
               box (ValueSome "tenant-1")
               box (ValueSome "user-1")
               box tags
               box StructuredOutputPolicy.AllowSecondaryModelRepair
               box SensitiveDataMode.Redact
               box (NullServiceProvider() :> IServiceProvider) |]
        )
        :?> RunOptions

    let private createEnvelope kind operationId operationName operationKind =
        envelopeFactory.Invoke(
            null,
            [| box "run-00000000000000000000000000000001"
               box DateTimeOffset.UtcNow
               box kind
               box "testing.agent"
               box "1.0.0"
               box "Testing Agent"
               box operationId
               box operationName
               box operationKind
               null
               null
               null
               null
               null
               null
               null
               null
               null
               null
               box false
               null
               null
               null |]
        )
        :?> RunEventEnvelope

    [<Fact>]
    let ``scripted runtime dequeues scripted responses safely under concurrency`` () =
        let responses =
            [| for index in 1..64 -> ScriptedResponses.OutputJson($"{{\"text\":\"value-{index}\"}}") |]

        let runtime = ScriptedRuntime(responses)
        let agent = createAgent ()
        let signature = createSignature ()
        let coreRuntime = runtime :> ICircuitRuntime

        let tasks =
            [| for index in 1..64 ->
                   task {
                       let! result =
                           coreRuntime.RunAsync(
                               agent,
                               signature,
                               TestingInput(Name = $"input-{index}"),
                               RunOptions.Default,
                               CancellationToken.None
                           )

                       Assert.True(result.Result.IsSuccess)
                       return result.Result.Value.Text
                   } |]

        let outputs = Task.WhenAll(tasks).Result

        Assert.Equal(64, outputs |> Array.distinct |> Array.length)
        Assert.Equal(64, runtime.Calls.Count)
        Assert.Equal(0, runtime.RemainingResponses)

        let recordedInputs = runtime.Calls |> Seq.map _.InputJson |> Seq.sort |> Seq.toArray

        Assert.Equal(64, recordedInputs.Length)
        Assert.Contains(recordedInputs, fun json -> json.Contains("input-1", StringComparison.Ordinal))
        Assert.Contains(recordedInputs, fun json -> json.Contains("input-64", StringComparison.Ordinal))

    [<Fact>]
    let ``scripted runtime throws a descriptive exception when the queue is exhausted`` () =
        let runtime = ScriptedRuntime(Array.empty)
        let agent = createAgent ()
        let signature = createSignature ()

        let ex =
            Assert.Throws<ScriptedResponseExhaustedException>(fun () ->
                (runtime :> ICircuitRuntime)
                    .RunAsync(
                        agent,
                        signature,
                        TestingInput(Name = "overflow"),
                        RunOptions.Default,
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("ScriptedRuntime has no scripted responses remaining", ex.Message)
        Assert.Equal(1, runtime.Calls.Count)

    [<Fact>]
    let ``recorded calls snapshot serialized input schemas and options immutably`` () =
        let runtime =
            ScriptedRuntime([| ScriptedResponses.OutputJson("{\"text\":\"ok\"}") |])

        let tags = Dictionary<string, string>(StringComparer.Ordinal)
        tags["stage"] <- "before"

        let agent = createAgent ()
        let signature = createSignature ()
        let input = TestingInput(Name = "before")
        let options = createRunOptions (tags :> IReadOnlyDictionary<string, string>)

        let result =
            (runtime :> ICircuitRuntime)
                .RunAsync(agent, signature, input, options, CancellationToken.None)
                .GetAwaiter()
                .GetResult()

        Assert.True(result.Result.IsSuccess)

        input.Name <- "after"
        tags["stage"] <- "after"

        let call = Assert.Single(runtime.Calls)

        Assert.Contains("\"name\":\"before\"", call.InputJson)
        Assert.DoesNotContain("after", call.InputJson)
        Assert.Equal(signature.Input.Schema.ToJsonString(), call.InputSchemaJson)
        Assert.Equal(signature.Output.Schema.ToJsonString(), call.OutputSchemaJson)
        Assert.Contains("\"stage\":\"before\"", call.OptionsJson)
        Assert.DoesNotContain("\"stage\":\"after\"", call.OptionsJson)
        Assert.Contains("\"tenantId\":\"tenant-1\"", call.OptionsJson)
        Assert.Contains("\"sensitiveDataMode\":\"redact\"", call.OptionsJson)

    [<Fact>]
    let ``recording observer clears snapshots and assertion helpers validate events`` () =
        let observer = RecordingRunObserver()
        let sink = observer :> IRunObserver

        sink
            .OnEventAsync(
                createEnvelope AgentRunEventKind.ToolStarted "tool-1" "lookup" RunOperationKind.Tool,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

        sink
            .OnEventAsync(
                createEnvelope AgentRunEventKind.ToolCompleted "tool-1" "lookup" RunOperationKind.Tool,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

        sink
            .OnEventAsync(
                createEnvelope AgentRunEventKind.StepStarted "step-1" "summarize" RunOperationKind.WorkflowStep,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

        sink
            .OnEventAsync(
                createEnvelope AgentRunEventKind.RunCompleted "" "run" RunOperationKind.Run,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult()

        RunAssertions.AssertTerminalEventCount(observer.Events, 1)
        RunAssertions.AssertOperationOrder(observer.Events, [| "lookup"; "summarize" |])

        observer.Clear()

        Assert.Empty(observer.Events)

    [<Fact>]
    let ``run assertions validate scripted stream terminal events and monotonic sequence`` () =
        let runtime =
            ScriptedRuntime([| ScriptedResponses.Stream([| "{\"text\":\"hel"; "lo\"}" |]) |])

        let events = ResizeArray<RunEvent<TestingOutput>>()

        let stream =
            (runtime :> ICircuitRuntime)
                .RunStreamingAsync(
                    createAgent (),
                    createSignature (),
                    TestingInput(Name = "stream"),
                    RunOptions.Default,
                    CancellationToken.None
                )

        let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable keepGoing = true

            while keepGoing do
                let moved = enumerator.MoveNextAsync().AsTask().Result

                if moved then
                    events.Add(enumerator.Current)
                else
                    keepGoing <- false
        finally
            enumerator.DisposeAsync().AsTask().Wait()

        RunAssertions.AssertMonotonicSequence(events)
        RunAssertions.AssertTerminalEventCount(events, 1)
        Assert.Equal(RunEventKind.RunCompleted, events[events.Count - 1].Kind)
