using Circuit;
using Circuit.MicrosoftAgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.FSharp.Core;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class MafAdapterExample
{
    public static MafRuntime CreateRuntime(IChatClient chatClient)
    {
        var options = new MafRuntimeOptions();
        options.DefaultModelId = FSharpValueOption<string>.Some("gpt-4.1-mini");
        options.Observers = [new OpenTelemetryRunObserver(options: null)];
        return new MafRuntime(chatClient, options);
    }

    private static MafRuntimeOptions CreateOptions()
    {
        var options = new MafRuntimeOptions();
        options.DefaultModelId = FSharpValueOption<string>.Some("gpt-4.1-mini");
        return options;
    }
}
