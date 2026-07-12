namespace Circuit.FSharp.Tests.DocumentationExamples

open Circuit
open Circuit.MicrosoftAgentFramework

module ObservabilityExample =
    let createObserver () =
        let options = OpenTelemetryRunObserverOptions()
        options.CaptureOutput <- true
        options.Redactor <- System.Func<string, string>(fun text -> text.Replace("secret", "[redacted]"))
        OpenTelemetryRunObserver(options) :> Circuit.IRunObserver
