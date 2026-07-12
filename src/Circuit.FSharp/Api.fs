namespace Circuit.FSharp

open System
open System.Text.Json
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
