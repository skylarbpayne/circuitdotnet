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
