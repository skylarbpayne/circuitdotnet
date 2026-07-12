namespace Circuit

open System
open System.Collections.Generic
open System.Text.Json
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.FSharp.Core

module internal CSharpFacadeAdapters =
    let private toValueOption (value: 'T) =
        if isNull (box value) then ValueNone else ValueSome value

    let createRuntimeOptions
        (options: Circuit.MicrosoftAgentFrameworkOptions)
        (toolResolvers: IReadOnlyList<Circuit.IToolResolver>)
        (skillResolvers: IReadOnlyList<Circuit.ISkillResolver>)
        (observers: IReadOnlyList<Circuit.IRunObserver>)
        =
        let runtimeOptions = MafRuntimeOptions()

        if not (isNull (box options)) then
            runtimeOptions.DefaultModelId <- toValueOption options.DefaultModelId

            match options.JsonSerializerOptions with
            | null -> ()
            | jsonOptions ->
                let snapshot = JsonSerializerOptions(jsonOptions)
                snapshot.MakeReadOnly()
                runtimeOptions.JsonSerializerOptions <- snapshot

            runtimeOptions.SecondaryStructuredOutputClient <- toValueOption options.SecondaryStructuredOutputClient

        runtimeOptions.ToolResolvers <-
            toolResolvers
            |> Seq.map (fun (resolver: Circuit.IToolResolver) -> Circuit.CoreAdapters.ToCore resolver)
            |> Seq.toArray
            :> IReadOnlyList<Circuit.Core.IToolResolver>

        runtimeOptions.SkillResolvers <-
            skillResolvers
            |> Seq.map (fun (resolver: Circuit.ISkillResolver) -> Circuit.CoreAdapters.ToCore resolver)
            |> Seq.toArray
            :> IReadOnlyList<Circuit.Core.ISkillResolver>

        runtimeOptions.Observers <-
            observers
            |> Seq.map (fun (observer: Circuit.IRunObserver) ->
                { new Circuit.MicrosoftAgentFramework.IRunObserver with
                    member _.OnRunStartedAsync(context, startedAt, cancellationToken) =
                        observer.OnRunStartedAsync(Circuit.RunContext.FromCore(context), startedAt, cancellationToken)

                    member _.OnRunEventAsync(event, cancellationToken) =
                        observer.OnRunEventAsync(
                            Circuit.ObservedRunEvent.FromCore(
                                event.RunId.Value,
                                event.Timestamp,
                                enum<Circuit.AgentRunEventKind> (int event.Kind),
                                (if event.OperationId.IsSome then
                                     Some event.OperationId.Value
                                 else
                                     None)
                                |> Option.toObj,
                                (if event.TextDelta.IsSome then
                                     Some event.TextDelta.Value
                                 else
                                     None)
                                |> Option.toObj,
                                (if event.Failure.IsSome then
                                     Some(Circuit.AgentFailure.FromCore(event.Failure.Value))
                                 else
                                     None)
                                |> Option.toObj,
                                (if event.Approval.IsSome then
                                     Some(Circuit.ApprovalRequest.FromCore(event.Approval.Value))
                                 else
                                     None)
                                |> Option.toObj
                            ),
                            cancellationToken
                        )

                    member _.OnRunCompletedAsync(observation, cancellationToken) =
                        observer.OnRunCompletedAsync(
                            Circuit.RunObservation.FromCore(
                                Circuit.RunContext.FromCore(observation.Context),
                                observation.StartedAt,
                                observation.CompletedAt,
                                observation.Repaired,
                                Circuit.RunUsage.FromCore(observation.Usage),
                                (if observation.Session.IsSome then
                                     Some(Circuit.CircuitSession.FromCore(observation.Session.Value))
                                 else
                                     None)
                                |> Option.toObj,
                                (if observation.Failure.IsSome then
                                     Some(Circuit.AgentFailure.FromCore(observation.Failure.Value))
                                 else
                                     None)
                                |> Option.toObj,
                                observation.DiagnosticMetadata
                            ),
                            cancellationToken
                        ) })
            |> Seq.toArray
            :> IReadOnlyList<Circuit.MicrosoftAgentFramework.IRunObserver>

        runtimeOptions

[<AbstractClass; Sealed>]
type MicrosoftAgentFrameworkRuntimeFactory private () =
    static member CreateClient
        (
            chatClient: IChatClient,
            options: Circuit.MicrosoftAgentFrameworkOptions,
            toolResolvers: IReadOnlyList<Circuit.IToolResolver>,
            skillResolvers: IReadOnlyList<Circuit.ISkillResolver>,
            observers: IReadOnlyList<Circuit.IRunObserver>
        ) =
        if isNull (box chatClient) then
            nullArg "chatClient"

        if isNull (box toolResolvers) then
            nullArg "toolResolvers"

        if isNull (box skillResolvers) then
            nullArg "skillResolvers"

        if isNull (box observers) then
            nullArg "observers"

        let runtime =
            MafRuntime(
                chatClient,
                CSharpFacadeAdapters.createRuntimeOptions options toolResolvers skillResolvers observers
            )

        CircuitClientFactory.Create(runtime :> Circuit.Core.ICircuitRuntime)

type private DefaultMafRuntime(runtime: Circuit.Core.ICircuitRuntime) =
    member _.Runtime = runtime

type private UnsupportedWorkflowRuntime(message: string) =
    let unsupported () = invalidOp message

    interface Circuit.Core.IWorkflowRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                input: 'Input,
                options: WorkflowRunOptions,
                cancellationToken: CancellationToken
            ) : Task<RunResult<'Output>> =
            unsupported ()

        member _.StartAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                input: 'Input,
                options: WorkflowRunOptions,
                cancellationToken: CancellationToken
            ) : Task<WorkflowRun<'Output>> =
            unsupported ()

        member _.ResumeAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                checkpoint: WorkflowCheckpoint<'Output>,
                cancellationToken: CancellationToken
            ) : Task<WorkflowRun<'Output>> =
            unsupported ()

module private AddCircuitServiceRegistration =
    [<Literal>]
    let MissingChatClientMessage =
        "AddCircuit requires an IChatClient singleton to be registered when no ICircuitRuntime is supplied."

    [<Literal>]
    let UnsupportedWorkflowRuntimeMessage =
        "The registered Circuit runtime does not support workflows. Register an IWorkflowRuntime to enable workflow operations."

    let createDefaultRuntime (serviceProvider: IServiceProvider) =
        let chatClient = serviceProvider.GetService(typeof<IChatClient>) :?> IChatClient

        if isNull (box chatClient) then
            invalidOp MissingChatClientMessage

        let optionsSnapshot =
            serviceProvider.GetRequiredService<Circuit.CircuitOptions>().Snapshot()

        let runtime =
            MafRuntime(
                chatClient,
                CSharpFacadeAdapters.createRuntimeOptions
                    optionsSnapshot.MicrosoftAgentFramework
                    optionsSnapshot.ToolResolvers
                    optionsSnapshot.SkillResolvers
                    optionsSnapshot.RunObservers
            )

        DefaultMafRuntime(runtime :> Circuit.Core.ICircuitRuntime)

    let createWorkflowRuntime (serviceProvider: IServiceProvider) =
        match serviceProvider.GetRequiredService<Circuit.Core.ICircuitRuntime>() with
        | :? Circuit.Core.IWorkflowRuntime as workflowRuntime -> workflowRuntime
        | _ -> UnsupportedWorkflowRuntime(UnsupportedWorkflowRuntimeMessage) :> Circuit.Core.IWorkflowRuntime

[<AbstractClass; Sealed; Extension>]
type ServiceCollectionExtensions private () =
    [<Extension>]
    static member AddCircuit(services: IServiceCollection, configure: Action<Circuit.CircuitOptions>) =
        if isNull (box services) then
            nullArg "services"

        if isNull configure then
            nullArg "configure"

        let circuitOptions = Circuit.CircuitOptions()
        configure.Invoke(circuitOptions)
        let snapshot = circuitOptions.Snapshot()

        services.TryAddSingleton<Circuit.CircuitOptions>(snapshot) |> ignore

        services.TryAddSingleton<DefaultMafRuntime>(fun serviceProvider ->
            AddCircuitServiceRegistration.createDefaultRuntime serviceProvider)
        |> ignore

        services.TryAddSingleton<Circuit.Core.ICircuitRuntime>(fun serviceProvider ->
            serviceProvider.GetRequiredService<DefaultMafRuntime>().Runtime)
        |> ignore

        services.TryAddSingleton<Circuit.Core.IWorkflowRuntime>(fun serviceProvider ->
            AddCircuitServiceRegistration.createWorkflowRuntime serviceProvider)
        |> ignore

        services.TryAddSingleton<Circuit.ICircuitClient>(fun serviceProvider ->
            CircuitClientFactory.Create(
                serviceProvider.GetRequiredService<Circuit.Core.ICircuitRuntime>(),
                serviceProvider.GetRequiredService<Circuit.Core.IWorkflowRuntime>()
            ))
        |> ignore

        services.TryAddSingleton<Circuit.IAgentClient>(fun serviceProvider ->
            serviceProvider.GetRequiredService<Circuit.ICircuitClient>() :> Circuit.IAgentClient)
        |> ignore

        services.TryAddSingleton<Circuit.IWorkflowClient>(fun serviceProvider ->
            serviceProvider.GetRequiredService<Circuit.ICircuitClient>() :> Circuit.IWorkflowClient)
        |> ignore

        services
