module Circuit.FSharp

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core

module private Defaults =
    let jsonOptions =
        let options = CircuitJson.createOptions ()
        options.MakeReadOnly()
        options

module Signature =
    let create<'Input, 'Output> id version description instructions =
        Circuit.Core.Signature<'Input, 'Output>
            .Create(id, version, description, instructions, Defaults.jsonOptions, Seq.empty, Seq.empty)

    let withInputValidator
        (validator: IContractValidator<'Input>)
        (signature: Circuit.Core.Signature<'Input, 'Output>)
        =
        Circuit.Core.Signature<'Input, 'Output>
            .Create(
                signature.Id.Value,
                signature.Version.ToString(),
                signature.Description,
                signature.Instructions,
                signature.JsonSerializerOptions,
                Seq.append signature.Input.Validators [ validator ],
                signature.Output.Validators
            )

    let withOutputValidator
        (validator: IContractValidator<'Output>)
        (signature: Circuit.Core.Signature<'Input, 'Output>)
        =
        Circuit.Core.Signature<'Input, 'Output>
            .Create(
                signature.Id.Value,
                signature.Version.ToString(),
                signature.Description,
                signature.Instructions,
                signature.JsonSerializerOptions,
                signature.Input.Validators,
                Seq.append signature.Output.Validators [ validator ]
            )

module AgentDefinition =
    let private recreate (definition: Circuit.Core.AgentDefinition) modelHint toolTags skills =
        Circuit.Core.AgentDefinition.Create(
            definition.Id.Value,
            definition.Version.ToString(),
            definition.Name,
            definition.Instructions,
            modelHint,
            toolTags,
            skills,
            definition.Metadata
        )

    let create id version name instructions =
        Circuit.Core.AgentDefinition.Create(id, version, name, instructions, ValueNone, Seq.empty, Seq.empty, Seq.empty)

    let withModelHint modelHint (definition: Circuit.Core.AgentDefinition) =
        recreate definition (ValueSome modelHint) definition.ToolTags definition.Skills

    let withToolTags toolTags (definition: Circuit.Core.AgentDefinition) =
        recreate definition definition.ModelHint toolTags definition.Skills

    let withSkills skills (definition: Circuit.Core.AgentDefinition) =
        recreate definition definition.ModelHint definition.ToolTags skills

module ToolDefinition =
    let private recreate
        (definition: Circuit.Core.ToolDefinition<'Input, 'Output>)
        input
        output
        approval
        approvalPolicy
        =
        Circuit.Core.ToolDefinition<'Input, 'Output>
            .Create(
                definition.Name.Value,
                definition.Version.ToString(),
                definition.Description,
                input,
                output,
                approval,
                approvalPolicy,
                Func<ToolContext, 'Input, Task<'Output>>(fun context value -> definition.InvokeAsync(context, value))
            )

    let create<'Input, 'Output> id version description invoke =
        Circuit.Core.ToolDefinition<'Input, 'Output>
            .Create(
                id,
                version,
                description,
                Contract<'Input>.Create(Defaults.jsonOptions, Seq.empty),
                Contract<'Output>.Create(Defaults.jsonOptions, Seq.empty),
                Func<ToolContext, 'Input, Task<'Output>>(invoke)
            )

    let withInputValidator
        (validator: IContractValidator<'Input>)
        (definition: Circuit.Core.ToolDefinition<'Input, 'Output>)
        =
        recreate
            definition
            (Contract<'Input>.Create(Defaults.jsonOptions, Seq.append definition.Input.Validators [ validator ]))
            definition.Output
            definition.Approval
            definition.ApprovalPolicy

    let withOutputValidator
        (validator: IContractValidator<'Output>)
        (definition: Circuit.Core.ToolDefinition<'Input, 'Output>)
        =
        recreate
            definition
            definition.Input
            (Contract<'Output>.Create(Defaults.jsonOptions, Seq.append definition.Output.Validators [ validator ]))
            definition.Approval
            definition.ApprovalPolicy

    let withApproval approval (definition: Circuit.Core.ToolDefinition<'Input, 'Output>) =
        let approvalPolicy =
            if approval = ApprovalMode.ByPolicy then
                definition.ApprovalPolicy
            else
                ValueNone

        recreate definition definition.Input definition.Output approval approvalPolicy

    let withApprovalPolicy approvalPolicy (definition: Circuit.Core.ToolDefinition<'Input, 'Output>) =
        let approval =
            match approvalPolicy with
            | ValueSome _ -> ApprovalMode.ByPolicy
            | ValueNone -> definition.Approval

        recreate definition definition.Input definition.Output approval approvalPolicy

module Agent =
    let run (runtime: ICircuitRuntime) agent signature input options cancellationToken =
        runtime.RunAsync(agent, signature, input, options, cancellationToken)

[<Sealed>]
type CircuitProgram<'T> internal (program: CircuitPrograms.ProgramExpr<'T>) =
    member internal _.Program = program

[<Sealed>]
type CircuitBuilder() =
    member _.Return(value: 'T) =
        CircuitProgram<'T>(CircuitPrograms.succeed value)

    member _.ReturnFrom(program: CircuitProgram<'T>) = program

    member _.Bind(program: CircuitProgram<'T>, binder: 'T -> CircuitProgram<'U>) =
        if isNull (box program) then
            nullArg "program"

        if isNull (box binder) then
            nullArg "binder"

        CircuitProgram<'U>(CircuitPrograms.bind program.Program (fun value -> (binder value).Program))

    member _.Zero() =
        CircuitProgram<unit>(CircuitPrograms.succeed ())

    member _.Delay(generator: unit -> CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.delay (fun () -> (generator ()).Program))

    member _.Combine(first: CircuitProgram<unit>, second: CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.combine first.Program second.Program)

    member _.TryWith(body: CircuitProgram<'T>, handler: exn -> CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.tryWith body.Program (fun ex -> (handler ex).Program))

    member _.TryFinally(body: CircuitProgram<'T>, compensation: unit -> unit) =
        CircuitProgram<'T>(CircuitPrograms.tryFinally body.Program compensation)

    member _.Using(resource: 'T, binder: 'T -> CircuitProgram<'U>) : CircuitProgram<'U> when 'T :> IDisposable =
        CircuitProgram<'U>(CircuitPrograms.using resource (fun value -> (binder value).Program))

let circuit = CircuitBuilder()

module Circuit =
    let call agent signature input =
        CircuitProgram<_>(CircuitPrograms.call agent signature input)

    let code name operation =
        CircuitProgram<_>(CircuitPrograms.code name (fun cancellationToken -> operation cancellationToken))

    let ``parallel`` maxConcurrency (programs: CircuitProgram<'T> list) =
        let innerPrograms = programs |> List.map _.Program
        CircuitProgram<'T list>(CircuitPrograms.parallelPrograms maxConcurrency innerPrograms)

    let fail failure =
        CircuitProgram<_>(CircuitPrograms.fail failure)

    let run runtime options cancellationToken (program: CircuitProgram<'T>) =
        CircuitPrograms.run runtime options cancellationToken program.Program

module Workflow =
    let code = Circuit.Core.Workflow.code
    let agent = Circuit.Core.Workflow.agent
    let thenStep = Circuit.Core.Workflow.thenStep
    let choose = Circuit.Core.Workflow.choose
    let ``parallel`` = Circuit.Core.Workflow.``parallel``
    let request = Circuit.Core.Workflow.request
    let loop = Circuit.Core.Workflow.loop
    let define = Circuit.Core.Workflow.define
    let validate = Circuit.Core.Workflow.validate
    let run = Circuit.Core.Workflow.run
    let start = Circuit.Core.Workflow.start
    let resume = Circuit.Core.Workflow.resume
