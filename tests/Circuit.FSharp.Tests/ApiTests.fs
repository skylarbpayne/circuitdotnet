namespace Circuit.FSharp.Tests

open System
open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.FSharp
open Xunit

[<AllowNullLiteral>]
type Input() =
    [<property: Required>]
    member val Name: string = null with get, set

[<AllowNullLiteral>]
type Output() =
    [<property: Required>]
    member val Text: string = null with get, set

type ConstantValidator<'T>(path: string, message: string) =
    interface IContractValidator<'T> with
        member _.Validate(_value) =
            [| { Path = path
                 Code = "custom"
                 Message = message } |]

type EmptyAsyncEnumerable<'T>() =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            { new IAsyncEnumerator<'T> with
                member _.Current = Unchecked.defaultof<'T>
                member _.MoveNextAsync() = ValueTask<bool>(false)
                member _.DisposeAsync() = ValueTask() }

type InteractiveRecordingRuntime() =
    let events =
        EmptyAsyncEnumerable<RunEvent<Output>>() :> IAsyncEnumerable<RunEvent<Output>>

    let runId = RunId.Parse("0123456789abcdef0123456789abcdef")

    let mutable observed: (AgentDefinition * string * obj * RunOptions * CancellationToken) option =
        None

    let mutable response: (ApprovalResponse * CancellationToken) option = None
    let mutable disposed = false

    member _.Events = events
    member _.RunId = runId
    member _.Observed = observed
    member _.Response = response
    member _.Disposed = disposed

    interface IInteractiveCircuitRuntime with
        member _.StartAsync<'TInput, 'TOutput>
            (
                agent: AgentDefinition,
                signature: Signature<'TInput, 'TOutput>,
                input: 'TInput,
                options: RunOptions,
                cancellationToken: CancellationToken
            ) =
            Assert.Equal(typeof<Input>, typeof<'TInput>)
            Assert.Equal(typeof<Output>, typeof<'TOutput>)
            observed <- Some(agent, signature.Id.Value, box input, options, cancellationToken)

            Task.FromResult(
                AgentRun<'TOutput>(
                    runId,
                    unbox<IAsyncEnumerable<RunEvent<'TOutput>>> (box events),
                    (fun (value, ct) ->
                        response <- Some(value, ct)
                        ValueTask()),
                    (fun () ->
                        disposed <- true
                        ValueTask())
                )
            )

type RecordingRuntime(expectedValue: Output) =
    let mutable observedAgent: AgentDefinition option = None
    let mutable observedSignatureId = ""

    member _.ObservedAgent = observedAgent
    member _.ObservedSignatureId = observedSignatureId

    interface ICircuitRuntime with
        member _.RunAsync<'TInput, 'TOutput>(agent, signature: Signature<'TInput, 'TOutput>, _input, _options, _ct) =
            observedAgent <- Some agent
            observedSignatureId <- signature.Id.Value

            Task.FromResult(
                RunResult(
                    RunId.New(),
                    CircuitResult<'TOutput>.Success(unbox<'TOutput> (box expectedValue)),
                    RunUsage(1, 1),
                    ValueNone,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow
                )
            )

        member _.RunStreamingAsync(_agent, _signature, _input, _options, _ct) = raise (NotSupportedException())

        member _.SerializeSessionAsync(_agent, _session, _ct) = raise (NotSupportedException())
        member _.DeserializeSessionAsync(_agent, _state, _ct) = raise (NotSupportedException())

module ApiTests =
    [<Fact>]
    let ``signature helpers preserve validators`` () =
        let inputValidator =
            ConstantValidator<Input>("$.name", "input") :> IContractValidator<Input>

        let outputValidator =
            ConstantValidator<Output>("$.text", "output") :> IContractValidator<Output>

        let signature =
            Signature.create<Input, Output> "signature.test" "1.0.0" "Description" "Instructions"
            |> Signature.withInputValidator inputValidator
            |> Signature.withOutputValidator outputValidator

        let inputIssues = signature.Input.Validate(Input())
        let outputIssues = signature.Output.Validate(Output())

        Assert.Contains(inputIssues, fun issue -> issue.Message = "input")
        Assert.Contains(outputIssues, fun issue -> issue.Message = "output")

    [<Fact>]
    let ``run options helpers support pipeline syntax`` () =
        let session =
            CircuitSession(
                "session-1",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let options =
            Circuit.Core.RunOptions.Default
            |> RunOptions.withStructuredOutputPolicy StructuredOutputPolicy.AllowSecondaryModelRepair
            |> RunOptions.withSession session

        Assert.Equal(ValueSome session, options.Session)
        Assert.Equal(StructuredOutputPolicy.AllowSecondaryModelRepair, options.StructuredOutputPolicy)

    [<Fact>]
    let ``agent module delegates to the runtime`` () =
        let expected = Output(Text = "ok")
        let runtime = RecordingRuntime(expected) :> ICircuitRuntime

        let agent =
            AgentDefinition.create "agent.test" "1.0.0" "Agent" "Do the thing"
            |> AgentDefinition.withModelHint "model-id"

        let signature =
            Signature.create<Input, Output> "signature.test" "1.0.0" "Description" "Instructions"

        let result =
            (Agent.run
                runtime
                agent
                signature
                (Input(Name = "Ada"))
                Circuit.Core.RunOptions.Default
                CancellationToken.None)
                .Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("ok", result.Result.Value.Text)

    [<Fact>]
    let ``agent start delegates generic inputs and forwards the live handle`` () =
        let implementation = InteractiveRecordingRuntime()
        let runtime = implementation :> IInteractiveCircuitRuntime

        let agent =
            AgentDefinition.create "agent.interactive" "1.0.0" "Interactive" "Pause when needed"

        let signature =
            Signature.create<Input, Output> "signature.interactive" "1.0.0" "Description" "Instructions"

        let input = Input(Name = "Ada")
        let options = Circuit.Core.RunOptions.Default
        use cancellationSource = new CancellationTokenSource()

        let run =
            Agent.start runtime agent signature input options cancellationSource.Token
            |> _.Result

        let observedAgent, observedSignatureId, observedInput, observedOptions, observedToken =
            implementation.Observed.Value

        Assert.Same(agent, observedAgent)
        Assert.Equal(signature.Id.Value, observedSignatureId)
        Assert.Same(input, observedInput)
        Assert.Same(options, observedOptions)
        Assert.Equal(cancellationSource.Token, observedToken)
        Assert.Equal(implementation.RunId, run.RunId)
        Assert.Same(implementation.Events, run.Events)

        let response = ApprovalResponse.Create("approval-1", true)
        let responseToken = CancellationToken(true)
        run.RespondAsync(response, responseToken).AsTask().GetAwaiter().GetResult()
        Assert.Equal(Some(response, responseToken), implementation.Response)

        (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
        Assert.True(implementation.Disposed)
