/// F#-first helpers for building Circuit definitions, programs, and workflows.
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

/// F# helpers for creating and refining signatures.
module Signature =
    /// Creates a signature using Circuit's default F# JSON settings and no custom validators.
    let create<'Input, 'Output> id version description instructions =
        Circuit.Core.Signature<'Input, 'Output>
            .Create(id, version, description, instructions, Defaults.jsonOptions, Seq.empty, Seq.empty)

    /// Appends an input validator to an existing signature.
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

    /// Appends an output validator to an existing signature.
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

/// F# helpers for creating and refining agent definitions.
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

    /// Creates an agent definition with no model hint, tool tags, skills, or metadata.
    let create id version name instructions =
        Circuit.Core.AgentDefinition.Create(id, version, name, instructions, ValueNone, Seq.empty, Seq.empty, Seq.empty)

    /// Replaces the model hint on an existing agent definition.
    let withModelHint modelHint (definition: Circuit.Core.AgentDefinition) =
        recreate definition (ValueSome modelHint) definition.ToolTags definition.Skills

    /// Replaces the tool tags on an existing agent definition.
    let withToolTags toolTags (definition: Circuit.Core.AgentDefinition) =
        recreate definition definition.ModelHint toolTags definition.Skills

    /// Replaces the skills on an existing agent definition.
    let withSkills skills (definition: Circuit.Core.AgentDefinition) =
        recreate definition definition.ModelHint definition.ToolTags skills

/// F# helpers for creating and refining tool definitions.
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

    /// Creates a tool definition using Circuit's default F# JSON settings and no custom validators.
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

    /// Appends an input validator to an existing tool definition.
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

    /// Appends an output validator to an existing tool definition.
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

    /// Replaces the approval mode on an existing tool definition.
    let withApproval approval (definition: Circuit.Core.ToolDefinition<'Input, 'Output>) =
        let approvalPolicy =
            if approval = ApprovalMode.ByPolicy then
                definition.ApprovalPolicy
            else
                ValueNone

        recreate definition definition.Input definition.Output approval approvalPolicy

    /// Sets or clears the named approval policy on an existing tool definition.
    /// <remarks>Supplying a policy implicitly sets approval mode to <see cref="F:Circuit.Core.ApprovalMode.ByPolicy" />.</remarks>
    let withApprovalPolicy approvalPolicy (definition: Circuit.Core.ToolDefinition<'Input, 'Output>) =
        let approval =
            match approvalPolicy with
            | ValueSome _ -> ApprovalMode.ByPolicy
            | ValueNone -> definition.Approval

        recreate definition definition.Input definition.Output approval approvalPolicy

/// F# helpers for immutably refining run options.
module RunOptions =
    /// Creates a copy that continues the supplied non-null session.
    let withSession (session: CircuitSession) (options: Circuit.Core.RunOptions) = options.WithSession(session)

    /// Creates a copy with the supplied structured-output policy.
    let withStructuredOutputPolicy (policy: StructuredOutputPolicy) (options: Circuit.Core.RunOptions) =
        options.WithStructuredOutputPolicy(policy)

/// F# helpers for rebinding process-local dependencies during checkpoint resume.
module ResumeOptions =
    /// Creates resume options with the supplied process-local services.
    let create (services: IServiceProvider) = Circuit.Core.ResumeOptions(services)

/// F#-first graph composition and execution helpers.
module Circuit =
    /// <summary>Assigns an explicit root identity and semantic version.</summary>
    let define = Circuit.Core.Circuit.define
    /// <summary>Creates a typed agent-leaf Circuit.</summary>
    let agent = Circuit.Core.Circuit.agent
    /// <summary>Creates a durable trusted-code Circuit leaf.</summary>
    let code = Circuit.Core.Circuit.code
    /// <summary>Creates a serialized immutable constant Circuit.</summary>
    let value = Circuit.Core.Circuit.value
    /// <summary>Creates a finite ordinal-keyed source.</summary>
    let items = Circuit.Core.Circuit.items
    /// <summary>Creates a finite source with explicit stable keys.</summary>
    let keyedItems = Circuit.Core.Circuit.keyedItems
    /// <summary>Creates a durable cursor-aware source.</summary>
    let source = Circuit.Core.Circuit.source
    /// <summary>Creates a non-checkpointable asynchronous source.</summary>
    let asyncSource = Circuit.Core.Circuit.asyncSource
    /// <summary>Pipelines successful lanes into the next Circuit.</summary>
    let thenStep = Circuit.Core.Circuit.thenStep
    /// <summary>Builds a validated dynamic child for each successful lane.</summary>
    let thenDynamic = Circuit.Core.Circuit.thenDynamic
    /// <summary>Captures lane failures as response values.</summary>
    let attempt = Circuit.Core.Circuit.attempt
    /// <summary>Maps failed lanes into replacement values.</summary>
    let recover = Circuit.Core.Circuit.recover
    /// <summary>Selects one named branch for each input.</summary>
    let branch = Circuit.Core.Circuit.branch
    /// <summary>Merges bounded independent branches.</summary>
    let merge = Circuit.Core.Circuit.merge
    /// <summary>Repeats a Circuit body under a bounded predicate.</summary>
    let loop = Circuit.Core.Circuit.loop
    /// <summary>Creates a host-approval pause.</summary>
    let approval = Circuit.Core.Circuit.approval
    /// <summary>Aggregates lane responses into a new typed output.</summary>
    let aggregate = Circuit.Core.Circuit.aggregate
    /// <summary>Adds a stable graph name segment.</summary>
    let named = Circuit.Core.Circuit.named
    /// <summary>Validates a Circuit definition without execution.</summary>
    let validate = Circuit.Core.Circuit.validate
    /// <summary>Starts the full live Circuit protocol.</summary>
    let start = Circuit.Core.Circuit.start
    /// <summary>Resumes a checkpoint with rebound process-local services.</summary>
    let resume = Circuit.Core.Circuit.resume
    /// <summary>Runs a Circuit expecting exactly one root response.</summary>
    let run = Circuit.Core.Circuit.run
    /// <summary>Collects root responses in completion order.</summary>
    let collect = Circuit.Core.Circuit.collect
    /// <summary>Collects root responses in stable source order.</summary>
    let collectSourceOrder = Circuit.Core.Circuit.collectSourceOrder
    /// <summary>Streams root responses as lanes complete.</summary>
    let stream = Circuit.Core.Circuit.stream

/// Response constructors for trusted code and aggregation handlers.
module Response =
    /// <summary>Creates a successful response in the current Circuit context.</summary>
    let succeed = Circuit.Core.Response.succeed
    /// <summary>Creates a failed response in the current Circuit context.</summary>
    let fail = Circuit.Core.Response.fail
