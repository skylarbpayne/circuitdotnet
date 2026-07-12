namespace Circuit.FSharp.Tests.DocumentationExamples

open System.Text.Json
open System.Threading.Tasks
open Circuit.Core

module SessionsExample =
    let roundTrip (runtime: ICircuitRuntime) (agent: AgentDefinition) (session: CircuitSession) =
        task {
            let! state = runtime.SerializeSessionAsync(agent, session, System.Threading.CancellationToken.None).AsTask()
            return! runtime.DeserializeSessionAsync(agent, state, System.Threading.CancellationToken.None).AsTask()
        }
