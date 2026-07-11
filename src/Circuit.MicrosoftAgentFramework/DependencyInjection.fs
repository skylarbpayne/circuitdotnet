namespace Circuit.MicrosoftAgentFramework

open System
open Circuit.Core
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection

[<AutoOpen>]
module DependencyInjection =
    type IServiceCollection with
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
