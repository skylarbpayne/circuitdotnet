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

/// F# helpers for running agents.
module Agent =
    /// Runs an agent to completion with the supplied runtime and options.
    let run (runtime: ICircuitRuntime) agent signature input options cancellationToken =
        runtime.RunAsync(agent, signature, input, options, cancellationToken)

    /// Starts an interactive agent run and returns its live event and approval handle.
    let start (runtime: IInteractiveCircuitRuntime) agent signature input options cancellationToken =
        runtime.StartAsync(agent, signature, input, options, cancellationToken)

/// Represents a composable F# Circuit computation expression.
[<Sealed>]
type CircuitProgram<'T> internal (program: CircuitPrograms.ProgramExpr<'T>) =
    member internal _.Program = program

/// Computation-expression builder for composing Circuit programs.
[<Sealed>]
type CircuitBuilder() =
    /// Lifts a pure value into a Circuit program.
    member _.Return(value: 'T) =
        CircuitProgram<'T>(CircuitPrograms.succeed value)

    /// Returns an existing Circuit program unchanged.
    member _.ReturnFrom(program: CircuitProgram<'T>) = program

    /// Sequences two Circuit programs.
    member _.Bind(program: CircuitProgram<'T>, binder: 'T -> CircuitProgram<'U>) =
        if isNull (box program) then
            nullArg "program"

        if isNull (box binder) then
            nullArg "binder"

        CircuitProgram<'U>(CircuitPrograms.bind program.Program (fun value -> (binder value).Program))

    /// Produces a no-op program.
    member _.Zero() =
        CircuitProgram<unit>(CircuitPrograms.succeed ())

    /// Defers program creation until execution.
    member _.Delay(generator: unit -> CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.delay (fun () -> (generator ()).Program))

    /// Runs one program and then another.
    member _.Combine(first: CircuitProgram<unit>, second: CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.combine first.Program second.Program)

    /// Handles exceptions raised by a program.
    member _.TryWith(body: CircuitProgram<'T>, handler: exn -> CircuitProgram<'T>) =
        CircuitProgram<'T>(CircuitPrograms.tryWith body.Program (fun ex -> (handler ex).Program))

    /// Runs compensation after a program finishes or fails.
    member _.TryFinally(body: CircuitProgram<'T>, compensation: unit -> unit) =
        CircuitProgram<'T>(CircuitPrograms.tryFinally body.Program compensation)

    /// Disposes a resource after the bound program completes.
    member _.Using(resource: 'T, binder: 'T -> CircuitProgram<'U>) : CircuitProgram<'U> when 'T :> IDisposable =
        CircuitProgram<'U>(CircuitPrograms.using resource (fun value -> (binder value).Program))

/// The default computation-expression builder for Circuit programs.
let circuit = CircuitBuilder()

/// F# combinators for building and running Circuit programs.
module Circuit =
    /// Creates a program step that invokes an agent/signature pair.
    let call agent signature input =
        CircuitProgram<_>(CircuitPrograms.call agent signature input)

    /// Creates a program step backed by user code.
    let code name operation =
        CircuitProgram<_>(CircuitPrograms.code name (fun cancellationToken -> operation cancellationToken))

    /// Runs several programs concurrently and collects their results in order.
    let ``parallel`` maxConcurrency (programs: CircuitProgram<'T> list) =
        let innerPrograms = programs |> List.map _.Program
        CircuitProgram<'T list>(CircuitPrograms.parallelPrograms maxConcurrency innerPrograms)

    /// Creates a failed program.
    let fail failure =
        CircuitProgram<_>(CircuitPrograms.fail failure)

    /// Executes a program with the supplied runtime and options.
    let run runtime options cancellationToken (program: CircuitProgram<'T>) =
        CircuitPrograms.run runtime options cancellationToken program.Program

/// F# aliases for the core workflow helpers.
module Workflow =
    /// See <see cref="M:Circuit.Core.Workflow.code``2(System.String,Microsoft.FSharp.Core.FSharpFunc{Circuit.Core.WorkflowContext,Microsoft.FSharp.Core.FSharpFunc{``0,System.Threading.Tasks.Task{``1}}})" />.
    let code = Circuit.Core.Workflow.code

    /// See <see cref="M:Circuit.Core.Workflow.agent``2(System.String,Circuit.Core.AgentDefinition,Circuit.Core.Signature{``0,``1})" />.
    let agent = Circuit.Core.Workflow.agent

    /// See <see cref="M:Circuit.Core.Workflow.thenStep``3(Circuit.Core.WorkflowStep{``0,``1},Circuit.Core.WorkflowDefinition{``2,``0})" />.
    let thenStep = Circuit.Core.Workflow.thenStep

    /// See <see cref="M:Circuit.Core.Workflow.choose``2(System.String,Microsoft.FSharp.Core.FSharpFunc{``0,System.String},Microsoft.FSharp.Collections.FSharpMap{System.String,Circuit.Core.WorkflowDefinition{``0,``1}},Microsoft.FSharp.Core.FSharpOption{Circuit.Core.WorkflowDefinition{``0,``1}})" />.
    let choose = Circuit.Core.Workflow.choose

    /// See <see cref="M:Circuit.Core.Workflow.parallel``3(System.String,System.Int32,Microsoft.FSharp.Collections.FSharpList{Circuit.Core.WorkflowDefinition{``0,``1}},Microsoft.FSharp.Core.FSharpFunc{Microsoft.FSharp.Collections.FSharpList{``1},System.Threading.Tasks.Task{``2}})" />.
    let ``parallel`` = Circuit.Core.Workflow.``parallel``

    /// See <see cref="M:Circuit.Core.Workflow.request``1(System.String,Microsoft.FSharp.Core.FSharpFunc{``0,Circuit.Core.ApprovalPrompt})" />.
    let request = Circuit.Core.Workflow.request

    /// See <see cref="M:Circuit.Core.Workflow.loop``1(System.String,System.Int32,Microsoft.FSharp.Core.FSharpFunc{``0,System.Boolean},Circuit.Core.WorkflowDefinition{``0,``0})" />.
    let loop = Circuit.Core.Workflow.loop

    /// See <see cref="M:Circuit.Core.Workflow.define``2(System.String,System.String,Circuit.Core.WorkflowStep{``0,``1})" />.
    let define = Circuit.Core.Workflow.define

    /// See <see cref="M:Circuit.Core.Workflow.validate``2(Circuit.Core.WorkflowDefinition{``0,``1})" />.
    let validate = Circuit.Core.Workflow.validate

    /// See <see cref="M:Circuit.Core.Workflow.run``2(Circuit.Core.IWorkflowRuntime,Circuit.Core.WorkflowDefinition{``0,``1},``0,Circuit.Core.WorkflowRunOptions,System.Threading.CancellationToken)" />.
    let run = Circuit.Core.Workflow.run

    /// See <see cref="M:Circuit.Core.Workflow.start``2(Circuit.Core.IWorkflowRuntime,Circuit.Core.WorkflowDefinition{``0,``1},``0,Circuit.Core.WorkflowRunOptions,System.Threading.CancellationToken)" />.
    let start = Circuit.Core.Workflow.start

    /// See <see cref="M:Circuit.Core.Workflow.resume``2(Circuit.Core.IWorkflowRuntime,Circuit.Core.WorkflowDefinition{``0,``1},Circuit.Core.WorkflowCheckpoint{``1},System.Threading.CancellationToken)" />.
    let resume = Circuit.Core.Workflow.resume
