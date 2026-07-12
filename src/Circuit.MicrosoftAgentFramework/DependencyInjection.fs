namespace Circuit.MicrosoftAgentFramework

open System
open Circuit.Core
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection

/// Dependency-injection helpers for registering the Microsoft Agent Framework runtime.
[<AutoOpen>]
module DependencyInjection =
    type IServiceCollection with
        /// Registers a singleton <see cref="T:Circuit.Core.ICircuitRuntime" /> backed by <see cref="T:Circuit.MicrosoftAgentFramework.MafRuntime" />.
        /// <remarks>The supplied options are snapshotted when registration occurs.</remarks>
        member services.AddMafRuntime(chatClient: IChatClient, options: MafRuntimeOptions) =
            if isNull (box services) then
                nullArg "services"

            if isNull (box chatClient) then
                nullArg "chatClient"

            if isNull (box options) then
                nullArg "options"

            let snapshot = options.Snapshot()

            services.AddSingleton(snapshot) |> ignore

            services.AddSingleton<ICircuitRuntime>(MafRuntime(chatClient, snapshot))
            |> ignore

            services
