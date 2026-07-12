namespace Circuit.Core

open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Describes the immutable execution context for a single run.
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
    /// Gets the run identifier.
    member _.RunId = runId

    /// Gets the agent definition being executed.
    member _.Agent = agent

    /// Gets the signature identifier for the run.
    member _.SignatureId = signatureId

    /// Gets the signature version for the run.
    member _.SignatureVersion = signatureVersion

    /// Gets the effective run options.
    member _.Options = options

/// Starts interactive Circuit agent runs that may pause for approval.
/// <remarks>
/// This capability is separate from <see cref="T:Circuit.Core.ICircuitRuntime" /> so existing runtime
/// implementations remain source and binary compatible.
/// </remarks>
type IInteractiveCircuitRuntime =
    /// Starts an agent and returns a live handle for streaming events and approval responses.
    abstract StartAsync<'Input, 'Output> :
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        cancellationToken: CancellationToken ->
            Task<AgentRun<'Output>>

/// Executes Circuit agents against a concrete provider runtime.
type ICircuitRuntime =
    /// Executes a run to completion and returns the final typed result.
    /// <remarks>
    /// Implementations should prefer returning a failed <see cref="T:Circuit.Core.RunResult`1" /> over throwing for expected provider,
    /// validation, tool, or skill failures. Cancellation is reported as <see cref="F:Circuit.Core.CircuitFailureCode.Cancelled" />.
    /// </remarks>
    abstract RunAsync<'Input, 'Output> :
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        cancellationToken: CancellationToken ->
            Task<RunResult<'Output>>

    /// Executes a run and yields streaming events as they occur.
    /// <remarks>
    /// Consumers should enumerate until the terminal event. Most failures surface as <see cref="F:Circuit.Core.RunEventKind.RunFailed" />
    /// rather than enumeration exceptions.
    /// </remarks>
    abstract RunStreamingAsync<'Input, 'Output> :
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        options: RunOptions *
        [<EnumeratorCancellation>] cancellationToken: CancellationToken ->
            IAsyncEnumerable<RunEvent<'Output>>

    /// Serializes a runtime-owned session into a stable JSON payload.
    /// <param name="agent">The agent definition the session belongs to.</param>
    /// <param name="session">The session to serialize.</param>
    /// <param name="cancellationToken">Cancels the serialization work.</param>
    /// <returns>An opaque JSON payload suitable for later deserialization by the same runtime family.</returns>
    /// <exception cref="T:System.ArgumentException">The session does not belong to the supplied agent.</exception>
    abstract SerializeSessionAsync:
        agent: AgentDefinition * session: CircuitSession * cancellationToken: CancellationToken ->
            ValueTask<JsonElement>

    /// Recreates a runtime-owned session from a prior serialized payload.
    /// <param name="agent">The agent definition the session will be used with.</param>
    /// <param name="state">The opaque JSON payload produced by <see cref="M:Circuit.Core.ICircuitRuntime.SerializeSessionAsync(Circuit.Core.AgentDefinition,Circuit.Core.CircuitSession,System.Threading.CancellationToken)" />.</param>
    /// <param name="cancellationToken">Cancels the deserialization work.</param>
    /// <returns>The deserialized session.</returns>
    /// <exception cref="T:System.ArgumentException">The payload is malformed or incompatible with the supplied agent.</exception>
    abstract DeserializeSessionAsync:
        agent: AgentDefinition * state: JsonElement * cancellationToken: CancellationToken -> ValueTask<CircuitSession>
