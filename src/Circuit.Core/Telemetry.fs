namespace Circuit.Core

open System.Diagnostics
open System.Diagnostics.Metrics

module internal TelemetryContracts =
    [<Literal>]
    let ActivitySourceName = "CircuitDotNet"

    let ActivitySource = new ActivitySource(ActivitySourceName)
    let Meter = new Meter(ActivitySourceName)
