namespace Circuit.Core

open System.Diagnostics

module internal TelemetryContracts =
    [<Literal>]
    let ActivitySourceName = "CircuitDotNet"

    let ActivitySource = new ActivitySource(ActivitySourceName)
