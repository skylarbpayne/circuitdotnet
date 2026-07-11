namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

type internal IMafRuntimeStreamingDispatcher =
    abstract RunStreaming<'Input, 'Output> :
        runtime: obj *
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        runOptions: RunOptions *
        cancellationToken: CancellationToken ->
            IAsyncEnumerable<RunEvent<'Output>>

module internal MafRuntimeStreamingDispatch =
    let mutable Dispatcher: IMafRuntimeStreamingDispatcher =
        { new IMafRuntimeStreamingDispatcher with
            member _.RunStreaming(_runtime, _agent, _signature, _input, _runOptions, _cancellationToken) =
                invalidOp "Streaming support has not been initialized." }

    let private initialization =
        Lazy<unit>(
            (fun () ->
                let registrationType =
                    Type.GetType(
                        "Circuit.MicrosoftAgentFramework.MafStreamingRegistration, Circuit.MicrosoftAgentFramework",
                        false
                    )

                if isNull registrationType then
                    invalidOp "Streaming support has not been initialized."

                Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(registrationType.TypeHandle)),
            LazyThreadSafetyMode.ExecutionAndPublication
        )

    let private ensureInitialized () = initialization.Value |> ignore

    let RunStreaming runtime agent signature input runOptions cancellationToken =
        ensureInitialized ()
        Dispatcher.RunStreaming(runtime, agent, signature, input, runOptions, cancellationToken)

[<Sealed>]
type MafRuntime(chatClient: IChatClient, options: MafRuntimeOptions) =
    do
        if isNull (box chatClient) then
            nullArg "chatClient"

        if isNull (box options) then
            nullArg "options"

        if isNull options.JsonSerializerOptions then
            nullArg "options.JsonSerializerOptions"

        if isNull options.ToolResolvers then
            nullArg "options.ToolResolvers"

        if isNull options.SkillResolvers then
            nullArg "options.SkillResolvers"

        if isNull options.Observers then
            nullArg "options.Observers"

    member internal _.ChatClient = chatClient
    member internal _.RuntimeOptions = options

    member internal _.CreateRunContext<'Input, 'Output>
        (runId: RunId, agent: AgentDefinition, signature: Signature<'Input, 'Output>, runOptions: RunOptions)
        =
        RunContext(runId, agent, signature.Id, signature.Version, runOptions)

    member internal _.ResolveCapabilitiesAsync
        (runId: RunId)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        : Task<Result<IReadOnlyList<ResolvedTool> * IReadOnlyList<ResolvedSkill>, CircuitFailure>> =
        task {
            try
                let! tools = MafAgentFactory.resolveToolsAsync options context agent cancellationToken

                try
                    let! skills = MafAgentFactory.resolveSkillsAsync options context agent cancellationToken
                    return Ok(tools, skills)
                with
                | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                    return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
                | ex -> return Error(MafErrors.skillFailure runId ex.Message (ValueSome ex))
            with
            | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
            | ex -> return Error(MafErrors.toolFailure runId ex.Message (ValueSome ex))
        }

    member internal _.CreateInputEnvelope<'Input, 'Output>(signature: Signature<'Input, 'Output>, input: 'Input) =
        let serializedInput =
            JsonSerializer.Serialize(input, signature.JsonSerializerOptions)

        $"Execute signature `{signature.Id.Value}` version `{signature.Version}`.\n\nInput JSON:\n{serializedInput}"

    member internal _.CreateDiagnosticMetadata(runOptions: RunOptions, response: AgentResponse) =
        if not (MafStructuredOutput.wasRepaired response) then
            MafErrors.emptyDiagnosticMetadata
        else
            let entries = Dictionary<string, string>(StringComparer.Ordinal)
            entries["circuit.repaired"] <- "true"

            match runOptions.SensitiveDataMode, MafStructuredOutput.tryGetOriginalResponseText response with
            | SensitiveDataMode.Standard, ValueSome originalResponseText ->
                entries["circuit.repair.originalResponse"] <- originalResponseText
            | _ -> ()

            entries :> IReadOnlyDictionary<string, string>

    member internal _.PrepareSessionAsync
        (runId: RunId)
        (runtimeAgent: AIAgent)
        (agent: AgentDefinition)
        (runOptions: RunOptions)
        (cancellationToken: CancellationToken)
        : Task<Result<AgentSession * CircuitSession voption, CircuitFailure>> =
        task {
            try
                match runOptions.Session with
                | ValueSome session ->
                    match
                        MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) session
                    with
                    | ValueSome providerSession -> return Ok(providerSession, ValueSome session)
                    | ValueNone ->
                        return
                            Error(
                                MafErrors.checkpointMismatchFailure
                                    runId
                                    "The supplied Circuit session does not match this runtime or agent definition."
                            )
                | ValueNone ->
                    let! providerSession = runtimeAgent.CreateSessionAsync(cancellationToken).AsTask()

                    return
                        Ok(providerSession, ValueSome(MafSessionContracts.createCircuitSession agent providerSession))
            with ex when MafErrors.isCancellationRequested cancellationToken ex ->
                return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
        }

    member internal this.BuildSessionAgentAsync
        (runId: RunId)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        : Task<Result<AIAgent, CircuitFailure>> =
        task {
            let context = RunContext(runId, agent, agent.Id, agent.Version, RunOptions.Default)
            let! capabilityResult = this.ResolveCapabilitiesAsync runId context agent cancellationToken

            match capabilityResult with
            | Error failure -> return Error failure
            | Ok(tools, skills) ->
                let sessionAgent =
                    MafAgentFactory.createSessionAgent chatClient options RunOptions.Default agent tools skills

                return Ok sessionAgent
        }

    member internal this.RunAsyncCore<'Input, 'Output>
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        (runOptions: RunOptions)
        (cancellationToken: CancellationToken)
        =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        if isNull (box runOptions) then
            nullArg "options"

        let runId = RunId.New()
        let startedAt = DateTimeOffset.UtcNow
        let runContext = this.CreateRunContext(runId, agent, signature, runOptions)

        task {
            let mutable usage = RunUsage(0, 0)
            let mutable repaired = false
            let mutable resultSession = runOptions.Session
            let mutable failureForObservers: CircuitFailure voption = ValueNone
            let mutable diagnosticMetadata = MafErrors.emptyDiagnosticMetadata

            let fail failure =
                task {
                    failureForObservers <- ValueSome failure

                    do!
                        MafObserver.notifyEventAsync
                            options.Observers
                            runId
                            RunEventKind.RunFailed
                            ValueNone
                            ValueNone
                            (ValueSome failure)
                            ValueNone
                            cancellationToken

                    return
                        RunResult(
                            runId,
                            CircuitResult<'Output>.Error failure,
                            usage,
                            resultSession,
                            startedAt,
                            DateTimeOffset.UtcNow
                        )
                }

            try
                if cancellationToken.IsCancellationRequested then
                    return! fail (MafErrors.cancelledFailure runId "The run was cancelled before it started." ValueNone)
                else
                    do! MafObserver.notifyStartedAsync options.Observers runContext startedAt cancellationToken

                    if cancellationToken.IsCancellationRequested then
                        return!
                            fail (MafErrors.cancelledFailure runId "The run was cancelled before it started." ValueNone)
                    else
                        let inputIssues = signature.Input.Validate input

                        if inputIssues.Count > 0 then
                            return!
                                fail (MafErrors.validationFailure runId (MafErrors.formatValidationIssues inputIssues))
                        else
                            let! capabilityResult =
                                this.ResolveCapabilitiesAsync runId runContext agent cancellationToken

                            match capabilityResult with
                            | Error failure -> return! fail failure
                            | Ok(tools, skills) ->
                                let enableSecondaryRepair =
                                    runOptions.StructuredOutputPolicy = StructuredOutputPolicy.AllowSecondaryModelRepair

                                if enableSecondaryRepair && options.SecondaryStructuredOutputClient.IsNone then
                                    return!
                                        fail (
                                            MafErrors.structuredOutputUnsupportedFailure
                                                runId
                                                "Structured output repair requires a configured secondary structured output chat client."
                                                ValueNone
                                                ValueNone
                                        )
                                else
                                    let runtimeAgent =
                                        MafAgentFactory.createAgent
                                            chatClient
                                            options
                                            runOptions
                                            agent
                                            signature
                                            tools
                                            skills
                                            enableSecondaryRepair

                                    let! sessionResult =
                                        this.PrepareSessionAsync runId runtimeAgent agent runOptions cancellationToken

                                    match sessionResult with
                                    | Error failure -> return! fail failure
                                    | Ok(providerSession, wrappedSession) ->
                                        resultSession <- wrappedSession
                                        let inputEnvelope = this.CreateInputEnvelope(signature, input)

                                        let! responseResult =
                                            task {
                                                try
                                                    let! response =
                                                        runtimeAgent.RunAsync<'Output>(
                                                            inputEnvelope,
                                                            providerSession,
                                                            signature.JsonSerializerOptions,
                                                            null,
                                                            cancellationToken
                                                        )

                                                    return Ok response
                                                with
                                                | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                                                    return
                                                        Error(
                                                            MafErrors.cancelledFailure
                                                                runId
                                                                "The run was cancelled."
                                                                (ValueSome ex)
                                                        )
                                                | ex when MafErrors.isStructuredOutputUnsupported ex ->
                                                    return
                                                        Error(
                                                            MafErrors.structuredOutputUnsupportedFailure
                                                                runId
                                                                ex.Message
                                                                ValueNone
                                                                (ValueSome ex)
                                                        )
                                                | ex ->
                                                    return
                                                        Error(
                                                            MafErrors.providerFailure
                                                                runId
                                                                ex.Message
                                                                ValueNone
                                                                (ValueSome ex)
                                                        )
                                            }

                                        match responseResult with
                                        | Error failure -> return! fail failure
                                        | Ok response ->
                                            let responseUsage =
                                                match MafStructuredOutput.tryGetOriginalUsage response with
                                                | ValueSome originalUsage ->
                                                    MafErrors.combineUsageDetails originalUsage response.Usage
                                                | ValueNone -> response.Usage

                                            usage <- MafErrors.createUsage responseUsage
                                            repaired <- MafStructuredOutput.wasRepaired response
                                            diagnosticMetadata <- this.CreateDiagnosticMetadata(runOptions, response)

                                            let decodedResult =
                                                try
                                                    Ok response.Result
                                                with
                                                | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                                                    Error(
                                                        MafErrors.cancelledFailure
                                                            runId
                                                            "The run was cancelled."
                                                            (ValueSome ex)
                                                    )
                                                | ex when MafErrors.isStructuredOutputUnsupported ex ->
                                                    Error(
                                                        MafErrors.structuredOutputUnsupportedFailure
                                                            runId
                                                            ex.Message
                                                            ValueNone
                                                            (ValueSome ex)
                                                    )
                                                | ex when MafErrors.isDecodeFailure ex ->
                                                    Error(
                                                        MafErrors.decodeFailure
                                                            runId
                                                            ex.Message
                                                            ValueNone
                                                            (ValueSome ex)
                                                    )
                                                | ex ->
                                                    Error(
                                                        MafErrors.providerFailure
                                                            runId
                                                            ex.Message
                                                            ValueNone
                                                            (ValueSome ex)
                                                    )

                                            match decodedResult with
                                            | Error failure -> return! fail failure
                                            | Ok output ->
                                                let outputIssues = signature.Output.Validate output

                                                if outputIssues.Count > 0 then
                                                    return!
                                                        fail (
                                                            MafErrors.validationFailure
                                                                runId
                                                                (MafErrors.formatValidationIssues outputIssues)
                                                        )
                                                else
                                                    do!
                                                        MafObserver.notifyEventAsync
                                                            options.Observers
                                                            runId
                                                            RunEventKind.RunCompleted
                                                            ValueNone
                                                            ValueNone
                                                            ValueNone
                                                            ValueNone
                                                            cancellationToken

                                                    return
                                                        RunResult(
                                                            runId,
                                                            CircuitResult<'Output>.Success output,
                                                            usage,
                                                            resultSession,
                                                            startedAt,
                                                            DateTimeOffset.UtcNow
                                                        )
            finally
                let observation =
                    MafRunObservation(
                        runContext,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        repaired,
                        usage,
                        resultSession,
                        failureForObservers,
                        diagnosticMetadata
                    )

                try
                    MafObserver.notifyCompletedAsync options.Observers observation cancellationToken
                    |> fun task -> task.GetAwaiter().GetResult()
                with _ ->
                    ()
        }

    member internal this.SerializeSessionAsyncCore
        (agent: AgentDefinition, session: CircuitSession, cancellationToken: CancellationToken)
        =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box session) then
            nullArg "session"

        match MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) session with
        | ValueNone ->
            raise (
                InvalidOperationException(
                    "The supplied Circuit session does not match this runtime or agent definition."
                )
            )
        | ValueSome providerSession ->
            let work: Task<JsonElement> =
                task {
                    let runId = RunId.New()
                    let! agentResult = this.BuildSessionAgentAsync runId agent cancellationToken

                    match agentResult with
                    | Error failure ->
                        return
                            raise (
                                InvalidOperationException(
                                    failure.Message,
                                    failure.Exception |> ValueOption.defaultValue null
                                )
                            )
                    | Ok sessionAgent ->
                        let! providerState =
                            sessionAgent
                                .SerializeSessionAsync(
                                    providerSession,
                                    options.JsonSerializerOptions,
                                    cancellationToken
                                )
                                .AsTask()

                        return
                            MafSessionContracts.serializeEnvelope
                                (MafSessionContracts.definitionFingerprint agent)
                                providerState
                }

            ValueTask<JsonElement>(work)

    member internal this.DeserializeSessionAsyncCore
        (agent: AgentDefinition, state: JsonElement, cancellationToken: CancellationToken)
        =
        if isNull (box agent) then
            nullArg "agent"

        let _, providerState =
            MafSessionContracts.parseEnvelope (MafSessionContracts.definitionFingerprint agent) state

        let work: Task<CircuitSession> =
            task {
                let runId = RunId.New()
                let! agentResult = this.BuildSessionAgentAsync runId agent cancellationToken

                match agentResult with
                | Error failure ->
                    return
                        raise (
                            InvalidOperationException(
                                failure.Message,
                                failure.Exception |> ValueOption.defaultValue null
                            )
                        )
                | Ok sessionAgent ->
                    let! providerSession =
                        sessionAgent
                            .DeserializeSessionAsync(providerState, options.JsonSerializerOptions, cancellationToken)
                            .AsTask()

                    return MafSessionContracts.createCircuitSession agent providerSession
            }

        ValueTask<CircuitSession>(work)

    interface ICircuitRuntime with
        member this.RunAsync(agent, signature, input, runOptions, cancellationToken) =
            this.RunAsyncCore agent signature input runOptions cancellationToken

        member this.RunStreamingAsync(agent, signature, input, runOptions, cancellationToken) =
            MafRuntimeStreamingDispatch.RunStreaming (box this) agent signature input runOptions cancellationToken

        member this.SerializeSessionAsync(agent, session, cancellationToken) =
            this.SerializeSessionAsyncCore(agent, session, cancellationToken)

        member this.DeserializeSessionAsync(agent, state, cancellationToken) =
            this.DeserializeSessionAsyncCore(agent, state, cancellationToken)
