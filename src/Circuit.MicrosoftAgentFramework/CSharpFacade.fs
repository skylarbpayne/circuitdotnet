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
                snapshot.MakeReadOnly(populateMissingResolver = true)
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

        runtimeOptions.Observers <- observers

        runtimeOptions

/// Creates high-level Circuit clients backed by the Microsoft Agent Framework runtime.
[<AbstractClass; Sealed>]
type MicrosoftAgentFrameworkRuntimeFactory private () =
    /// Creates an <see cref="T:Circuit.ICircuitClient" /> from a chat client and optional adapters.
    /// <remarks>
    /// Tool and skill resolvers are runtime extension points, not security boundaries. Approval and script-execution behavior still depends on the configured runtime options.
    /// </remarks>
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

module private AddCircuitServiceRegistration =
    [<Literal>]
    let MissingChatClientMessage =
        "AddCircuit requires an IChatClient singleton to be registered when no ICircuitRuntime is supplied."

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

/// Extension methods for registering the unified Circuit runtime and client.
[<AbstractClass; Sealed; Extension>]
type ServiceCollectionExtensions private () =
    /// Registers one <see cref="T:Circuit.Core.ICircuitRuntime" /> and <see cref="T:Circuit.ICircuitClient" />.
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

        services.TryAddSingleton<Circuit.ICircuitClient>(fun serviceProvider ->
            CircuitClientFactory.Create(serviceProvider.GetRequiredService<Circuit.Core.ICircuitRuntime>()))
        |> ignore

        services
