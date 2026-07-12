namespace Circuit.FSharp.Tests.DocumentationExamples

open Circuit
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI

module MafAdapterExample =
    let createRuntime (chatClient: IChatClient) =
        let options = MafRuntimeOptions()
        options.DefaultModelId <- ValueSome "gpt-4.1-mini"
        options.Observers <- [| OpenTelemetryRunObserver() :> Circuit.IRunObserver |]
        MafRuntime(chatClient, options)
