namespace Circuit.Core

open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<Sealed>]
type RunContext
    internal
    (
        runId: RunId,
        agent: AgentDefinition,
        signatureId: DefinitionId,
        signatureVersion: SemanticVersion,
        options: RunOptions
    ) =
    member _.RunId = runId
    member _.Agent = agent
    member _.SignatureId = signatureId
    member _.SignatureVersion = signatureVersion
    member _.Options = options

type ICircuitRuntime =
    abstract RunAsync<'Input, 'Output> :
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        cancellationToken: CancellationToken ->
            Task<RunResult<'Output>>

    abstract RunStreamingAsync<'Input, 'Output> :
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        [<EnumeratorCancellation>] cancellationToken: CancellationToken ->
            IAsyncEnumerable<RunEvent<'Output>>

    abstract SerializeSessionAsync:
        agent: AgentDefinition * session: CircuitSession * cancellationToken: CancellationToken ->
            ValueTask<JsonElement>

    abstract DeserializeSessionAsync:
        agent: AgentDefinition * state: JsonElement * cancellationToken: CancellationToken -> ValueTask<CircuitSession>
