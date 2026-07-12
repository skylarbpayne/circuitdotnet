namespace Circuit.FSharp

open System.Text.Json
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

module Agent =
    let run (runtime: ICircuitRuntime) agent signature input options cancellationToken =
        runtime.RunAsync(agent, signature, input, options, cancellationToken)
