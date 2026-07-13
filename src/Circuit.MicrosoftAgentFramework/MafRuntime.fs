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
        runId: RunId *
        nodePath: string *
        idempotencyKey: string *
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        runOptions: RunOptions *
        jsonOptions: JsonSerializerOptions *
        onSession: (CircuitSession -> Task) *
        cancellationToken: CancellationToken ->
            IAsyncEnumerable<RunEvent<'Output>>

type internal IMafRuntimeInteractiveDispatcher =
    abstract Start<'Input, 'Output> :
        runtime: obj *
        runId: RunId *
        nodePath: string *
        idempotencyKey: string *
        agent: AgentDefinition *
        signature: Signature<'Input, 'Output> *
        input: 'Input *
        runOptions: RunOptions *
        onSession: (CircuitSession -> Task) *
        cancellationToken: CancellationToken ->
            Task<AgentRun<'Output>>

module internal MafRuntimeInteractiveDispatch =
    let mutable Dispatcher: IMafRuntimeInteractiveDispatcher =
        { new IMafRuntimeInteractiveDispatcher with
            member _.Start
                (
                    _runtime,
                    _runId,
                    _nodePath,
                    _idempotencyKey,
                    _agent,
                    _signature,
                    _input,
                    _runOptions,
                    _onSession,
                    _cancellationToken
                ) =
                invalidOp "Interactive support has not been initialized." }

    let private initialization =
        Lazy<unit>(
            (fun () ->
                let registrationType =
                    Type.GetType(
                        "Circuit.MicrosoftAgentFramework.MafInteractiveRegistration, Circuit.MicrosoftAgentFramework",
                        false
                    )

                if isNull registrationType then
                    invalidOp "Interactive support has not been initialized."

                Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(registrationType.TypeHandle)),
            LazyThreadSafetyMode.ExecutionAndPublication
        )

    let Start runtime runId nodePath idempotencyKey agent signature input runOptions onSession cancellationToken =
        initialization.Value |> ignore

        Dispatcher.Start(
            runtime,
            runId,
            nodePath,
            idempotencyKey,
            agent,
            signature,
            input,
            runOptions,
            onSession,
            cancellationToken
        )

module internal MafRuntimeStreamingDispatch =
    let mutable Dispatcher: IMafRuntimeStreamingDispatcher =
        { new IMafRuntimeStreamingDispatcher with
            member _.RunStreaming
                (
                    _runtime,
                    _runId,
                    _nodePath,
                    _idempotencyKey,
                    _agent,
                    _signature,
                    _input,
                    _runOptions,
                    _jsonOptions,
                    _onSession,
                    _cancellationToken
                ) =
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

    let RunStreaming
        runtime
        runId
        nodePath
        idempotencyKey
        agent
        signature
        input
        runOptions
        jsonOptions
        onSession
        cancellationToken
        =
        ensureInitialized ()

        Dispatcher.RunStreaming(
            runtime,
            runId,
            nodePath,
            idempotencyKey,
            agent,
            signature,
            input,
            runOptions,
            jsonOptions,
            onSession,
            cancellationToken
        )

module internal MafRuntimeInternals =
    let classifyProviderExecutionFailure runId cancellationToken ex =
        if MafErrors.isCancellationRequested cancellationToken ex then
            MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex)
        elif MafErrors.isStructuredOutputUnsupported ex then
            MafErrors.structuredOutputUnsupportedFailure
                runId
                "Structured output is not supported for this run."
                ValueNone
                (ValueSome ex)
        else
            MafErrors.providerFailure runId "The provider request failed." ValueNone (ValueSome ex)

    let decodeResponseResult<'Input, 'Output>
        (runId: RunId)
        (cancellationToken: CancellationToken)
        (signature: Signature<'Input, 'Output>)
        (getOutput: unit -> 'Output)
        =
        try
            let output = getOutput ()
            let outputIssues = signature.Output.Validate output

            if outputIssues.Count > 0 then
                Error(MafErrors.validationFailure runId (MafErrors.formatValidationIssues outputIssues))
            else
                Ok output
        with
        | ex when MafErrors.isCancellationRequested cancellationToken ex ->
            Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
        | ex when MafErrors.isStructuredOutputUnsupported ex ->
            Error(
                MafErrors.structuredOutputUnsupportedFailure
                    runId
                    "Structured output is not supported for this run."
                    ValueNone
                    (ValueSome ex)
            )
        | ex when MafErrors.isDecodeFailure ex ->
            Error(MafErrors.decodeFailure runId "The provider response could not be decoded." ValueNone (ValueSome ex))
        | ex -> Error(MafErrors.providerFailure runId "The provider request failed." ValueNone (ValueSome ex))

/// Implements the unified Circuit runtime as a Microsoft Agent Framework leaf executor.
/// <param name="chatClient">The primary chat client used for provider calls.</param>
/// <param name="options">Runtime options that are snapshotted during construction.</param>
/// <remarks>
/// This runtime normalizes provider failures into <see cref="T:Circuit.Core.CircuitFailure" /> values when possible.
/// Interactive runs use non-streaming provider rounds, do not promise token-level output-delta events, and fail after
/// 16 approval rounds to bound provider, tool, and resource use.
/// Tool resolvers, skill resolvers, approval policies, and script runners remain trusted in-process extensions.
/// </remarks>
[<Sealed>]
type MafRuntime(chatClient: IChatClient, options: MafRuntimeOptions) =
    inherit CircuitRuntime()

    let runtimeOptions =
        if isNull (box options) then
            nullArg "options"

        options.Snapshot()

    let options = runtimeOptions

    do
        if isNull (box chatClient) then
            nullArg "chatClient"

        if isNull runtimeOptions.JsonSerializerOptions then
            nullArg "options.JsonSerializerOptions"

        if isNull runtimeOptions.ToolResolvers then
            nullArg "options.ToolResolvers"

        if isNull runtimeOptions.SkillResolvers then
            nullArg "options.SkillResolvers"

        if isNull runtimeOptions.Observers then
            nullArg "options.Observers"

    /// <summary>Gets the internal value.</summary>
    member internal _.ChatClient = chatClient
    /// <summary>Gets the internal value.</summary>
    member internal _.RuntimeOptions = runtimeOptions

    /// <summary>Gets the internal value.</summary>
    member internal _.CreateRunContext<'Input, 'Output>
        (runId: RunId, agent: AgentDefinition, signature: Signature<'Input, 'Output>, runOptions: RunOptions)
        =
        RunContext(runId, agent, signature.Id, signature.Version, runOptions)

    /// <summary>Gets the internal value.</summary>
    member internal _.CreateScheduledRunContext<'Input, 'Output>
        (
            runId: RunId,
            nodePath: string,
            idempotencyKey: string,
            agent: AgentDefinition,
            signature: Signature<'Input, 'Output>,
            runOptions: RunOptions
        ) =
        RunContext(runId, agent, signature.Id, signature.Version, runOptions, nodePath, idempotencyKey)

    /// <summary>Gets the internal value.</summary>
    member internal _.ResolveRequestModel(agent: AgentDefinition) =
        match agent.ModelHint, options.DefaultModelId with
        | ValueSome modelId, _ -> ValueSome modelId
        | ValueNone, ValueSome modelId -> ValueSome modelId
        | ValueNone, ValueNone -> ValueNone

    /// <summary>Gets the internal value.</summary>
    member internal _.CreatePrompt<'Input, 'Output>(agent: AgentDefinition, signature: Signature<'Input, 'Output>) =
        if String.IsNullOrWhiteSpace signature.Instructions then
            agent.Instructions
        else
            agent.Instructions + "\n\n" + signature.Instructions

    /// <summary>Gets the internal value.</summary>
    member internal _.TrySerializeInputPayload<'Input, 'Output>
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        : string voption =
        try
            ValueSome(JsonSerializer.Serialize(input, signature.JsonSerializerOptions))
        with _ ->
            ValueNone

    /// <summary>Gets the internal value.</summary>
    member internal _.TrySerializeOutputPayload<'Input, 'Output>
        (signature: Signature<'Input, 'Output>)
        (output: 'Output)
        : string voption =
        try
            ValueSome(JsonSerializer.Serialize(output, signature.JsonSerializerOptions))
        with _ ->
            ValueNone

    member private _.ValidateApprovalConfiguration
        (runId: RunId)
        (tools: IReadOnlyList<ResolvedMafTool>)
        : Result<IReadOnlyList<ResolvedMafTool>, CircuitFailure> =
        let missingNamedPolicies =
            tools
            |> Seq.filter (fun tool -> tool.Tool.Approval = ApprovalMode.ByPolicy && tool.Tool.ApprovalPolicy.IsNone)
            |> Seq.map (fun tool -> $"{tool.Tool.Name.Value}@{tool.Tool.Version}")
            |> Seq.toArray

        if missingNamedPolicies.Length > 0 then
            let toolList = String.concat ", " missingNamedPolicies

            Error(
                MafErrors.toolFailure
                    runId
                    $"ApprovalMode.ByPolicy requires a configured tool approval policy name. Invalid tools: {toolList}."
                    ValueNone
            )
        elif options.ToolApprovalPolicy.IsNone then
            let policyTools =
                tools
                |> Seq.filter (fun tool -> tool.Tool.Approval = ApprovalMode.ByPolicy)
                |> Seq.map (fun tool -> $"{tool.Tool.Name.Value}@{tool.Tool.Version}")
                |> Seq.toArray

            if policyTools.Length > 0 then
                let toolList = String.concat ", " policyTools

                Error(
                    MafErrors.toolFailure
                        runId
                        $"MafRuntimeOptions.ToolApprovalPolicy must be configured before running tools that use ApprovalMode.ByPolicy. Affected tools: {toolList}."
                        ValueNone
                )
            else
                Ok tools
        else
            Ok tools

    /// <summary>Gets the internal value.</summary>
    member internal this.ResolveCapabilitiesAsync
        (runId: RunId)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        : Task<Result<IReadOnlyList<ResolvedMafTool> * IReadOnlyList<ResolvedSkill>, CircuitFailure>> =
        task {
            try
                let! tools = MafAgentFactory.resolveToolsAsync options context agent cancellationToken

                match this.ValidateApprovalConfiguration runId tools with
                | Error failure -> return Error failure
                | Ok validatedTools ->
                    try
                        let! skills = MafAgentFactory.resolveSkillsAsync options context agent cancellationToken
                        return Ok(validatedTools, skills)
                    with
                    | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                        return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
                    | ex -> return Error(MafErrors.skillFailure runId "Skill resolution failed." (ValueSome ex))
            with
            | ex when MafErrors.isCancellationRequested cancellationToken ex ->
                return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
            | :? ToolCapabilityFailureException as ex -> return Error ex.Failure
            | :? ToolResolverFailureException as ex ->
                let diagnosticException =
                    if isNull ex.InnerException then
                        ex :> exn
                    else
                        ex.InnerException

                return Error(MafErrors.toolResolutionFailure runId (ValueSome diagnosticException))
            | ex -> return Error(MafErrors.toolResolutionFailure runId (ValueSome ex))
        }

    member internal _.CreateInputEnvelope<'Input, 'Output>(signature: Signature<'Input, 'Output>, input: 'Input) =
        let serializedInput =
            JsonSerializer.Serialize(input, signature.JsonSerializerOptions)

        $"Execute signature `{signature.Id.Value}` version `{signature.Version}`.\n\nInput JSON:\n{serializedInput}"

    /// <summary>Gets the internal value.</summary>
    member internal this.TryCreateInputEnvelope<'Input, 'Output>
        (runId: RunId)
        (signature: Signature<'Input, 'Output>)
        (input: 'Input)
        (cancellationToken: CancellationToken)
        : Result<string, CircuitFailure> =
        try
            Ok(this.CreateInputEnvelope(signature, input))
        with
        | ex when MafErrors.isCancellationRequested cancellationToken ex ->
            Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
        | ex -> Error(MafErrors.decodeFailure runId "The run input could not be serialized." ValueNone (ValueSome ex))

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

    /// <summary>Gets the internal value.</summary>
    member internal _.PrepareSessionAsync
        (runId: RunId)
        (runtimeAgent: AIAgent)
        (agent: AgentDefinition)
        (sessionBinding: string)
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
                    | ValueSome providerSession when
                        MafSessionContracts.hasMatchingSessionBinding sessionBinding session
                        ->
                        return Ok(providerSession, ValueSome session)
                    | ValueSome _
                    | ValueNone ->
                        return
                            Error(
                                MafErrors.checkpointMismatchFailure
                                    runId
                                    "The supplied Circuit session does not match this runtime, agent definition, signature, tenant/user context, or resolved capabilities."
                            )
                | ValueNone ->
                    let! providerSession = runtimeAgent.CreateSessionAsync(cancellationToken).AsTask()

                    return
                        Ok(
                            providerSession,
                            ValueSome(
                                MafSessionContracts.createCircuitSession
                                    agent
                                    (MafSessionContracts.createSessionMetadata sessionBinding)
                                    providerSession
                            )
                        )
            with ex when MafErrors.isCancellationRequested cancellationToken ex ->
                return Error(MafErrors.cancelledFailure runId "The run was cancelled." (ValueSome ex))
        }

    member internal this.BuildSessionAgentAsync
        (runId: RunId)
        (agent: AgentDefinition)
        (runOptions: RunOptions)
        (cancellationToken: CancellationToken)
        : Task<Result<MafAgentFactory.MafCompiledAgent, CircuitFailure>> =
        task {
            let context = RunContext(runId, agent, agent.Id, agent.Version, runOptions)
            let! capabilityResult = this.ResolveCapabilitiesAsync runId context agent cancellationToken

            match capabilityResult with
            | Error failure -> return Error failure
            | Ok(tools, skills) ->
                try
                    let sessionAgent =
                        MafAgentFactory.createSessionAgent chatClient options context agent tools skills

                    return Ok sessionAgent
                with ex ->
                    return Error(MafErrors.skillFailure runId "Skill initialization failed." (ValueSome ex))
        }

    /// <summary>Gets the internal value.</summary>
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

        let observerSession =
            MafObserver.createAgentRunSession
                options.Observers
                runId
                agent.Name
                signature.Id
                signature.Version
                (this.ResolveRequestModel agent)
                runOptions.Services

        let prompt = ValueSome(this.CreatePrompt(agent, signature))
        let inputPayload = this.TrySerializeInputPayload signature input

        task {
            let mutable usage = RunUsage(0, 0)
            let mutable repaired = false
            let mutable resultSession = runOptions.Session
            let mutable failureForObservers: CircuitFailure voption = ValueNone
            let mutable diagnosticMetadata = MafErrors.emptyDiagnosticMetadata

            let fail failure =
                task {
                    failureForObservers <- ValueSome failure
                    let completedAt = DateTimeOffset.UtcNow

                    do!
                        MafObserver.notifyRootEventAsync
                            observerSession
                            RunEventKind.RunFailed
                            ValueNone
                            ValueNone
                            (ValueSome failure)
                            ValueNone
                            (ValueSome startedAt)
                            (ValueSome completedAt)
                            repaired
                            (ValueSome usage)
                            resultSession
                            diagnosticMetadata
                            cancellationToken

                    return
                        RunResult(
                            runId,
                            CircuitResult<'Output>.Error failure,
                            usage,
                            resultSession,
                            startedAt,
                            completedAt
                        )
                }

            try
                if cancellationToken.IsCancellationRequested then
                    return! fail (MafErrors.cancelledFailure runId "The run was cancelled before it started." ValueNone)
                else
                    do! MafObserver.notifyStartedAsync observerSession startedAt prompt inputPayload cancellationToken

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
                                    let sessionBinding =
                                        MafSessionContracts.createSessionBinding runContext signature tools skills

                                    let runtimeAgentResult =
                                        try
                                            Ok(
                                                MafAgentFactory.createAgent
                                                    chatClient
                                                    options
                                                    runContext
                                                    agent
                                                    signature
                                                    tools
                                                    skills
                                                    enableSecondaryRepair
                                            )
                                        with ex ->
                                            Error(
                                                MafErrors.skillFailure
                                                    runId
                                                    "Skill initialization failed."
                                                    (ValueSome ex)
                                            )

                                    match runtimeAgentResult with
                                    | Error failure -> return! fail failure
                                    | Ok runtimeAgent ->
                                        use runtimeAgent = runtimeAgent

                                        let! sessionResult =
                                            this.PrepareSessionAsync
                                                runId
                                                runtimeAgent.Agent
                                                agent
                                                sessionBinding
                                                runOptions
                                                cancellationToken

                                        match sessionResult with
                                        | Error failure -> return! fail failure
                                        | Ok(providerSession, wrappedSession) ->
                                            resultSession <- wrappedSession

                                            match
                                                this.TryCreateInputEnvelope runId signature input cancellationToken
                                            with
                                            | Error failure -> return! fail failure
                                            | Ok inputEnvelope ->
                                                let! responseResult =
                                                    task {
                                                        try
                                                            let! response =
                                                                runtimeAgent.Agent.RunAsync<'Output>(
                                                                    inputEnvelope,
                                                                    providerSession,
                                                                    signature.JsonSerializerOptions,
                                                                    null,
                                                                    cancellationToken
                                                                )

                                                            return Ok response
                                                        with ex ->
                                                            return
                                                                Error(
                                                                    MafRuntimeInternals.classifyProviderExecutionFailure
                                                                        runId
                                                                        cancellationToken
                                                                        ex
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

                                                    diagnosticMetadata <-
                                                        this.CreateDiagnosticMetadata(runOptions, response)

                                                    match
                                                        MafRuntimeInternals.decodeResponseResult
                                                            runId
                                                            cancellationToken
                                                            signature
                                                            (fun () -> response.Result)
                                                    with
                                                    | Error failure -> return! fail failure
                                                    | Ok output ->
                                                        let completedAt = DateTimeOffset.UtcNow

                                                        do!
                                                            MafObserver.notifyRootEventAsync
                                                                observerSession
                                                                RunEventKind.RunCompleted
                                                                ValueNone
                                                                (this.TrySerializeOutputPayload signature output)
                                                                ValueNone
                                                                ValueNone
                                                                (ValueSome startedAt)
                                                                (ValueSome completedAt)
                                                                repaired
                                                                (ValueSome usage)
                                                                resultSession
                                                                diagnosticMetadata
                                                                cancellationToken

                                                        return
                                                            RunResult(
                                                                runId,
                                                                CircuitResult<'Output>.Success output,
                                                                usage,
                                                                resultSession,
                                                                startedAt,
                                                                completedAt
                                                            )
            finally
                MafObserver.unregisterSession observerSession
        }

    /// <summary>Gets the internal value.</summary>
    member internal this.SerializeSessionAsyncCore
        (agent: AgentDefinition, session: CircuitSession, runOptions: RunOptions, cancellationToken: CancellationToken)
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
                    let! agentResult = this.BuildSessionAgentAsync runId agent runOptions cancellationToken

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
                        use sessionAgent = sessionAgent

                        let! providerState =
                            sessionAgent.Agent
                                .SerializeSessionAsync(
                                    providerSession,
                                    options.JsonSerializerOptions,
                                    cancellationToken
                                )
                                .AsTask()

                        return
                            MafSessionContracts.serializeEnvelope
                                (MafSessionContracts.definitionFingerprint agent)
                                session.Metadata
                                providerState
                }

            ValueTask<JsonElement>(work)

    member internal this.DeserializeSessionAsyncCore
        (agent: AgentDefinition, state: JsonElement, runOptions: RunOptions, cancellationToken: CancellationToken)
        =
        if isNull (box agent) then
            nullArg "agent"

        let _, metadata, providerState =
            MafSessionContracts.parseEnvelope (MafSessionContracts.definitionFingerprint agent) state

        let work: Task<CircuitSession> =
            task {
                let runId = RunId.New()
                let! agentResult = this.BuildSessionAgentAsync runId agent runOptions cancellationToken

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
                    use sessionAgent = sessionAgent

                    let! providerSession =
                        sessionAgent.Agent
                            .DeserializeSessionAsync(providerState, options.JsonSerializerOptions, cancellationToken)
                            .AsTask()

                    return MafSessionContracts.createCircuitSession agent metadata providerSession
            }

        ValueTask<CircuitSession>(work)

    /// <summary>Gets the execute agent async value.</summary>
    override this.ExecuteAgentAsync<'Input, 'Output>
        (
            schedulerRunId,
            nodePath,
            agent,
            signature: Signature<'Input, 'Output>,
            input: 'Input,
            runOptions,
            idempotencyKey,
            onDelta,
            onApproval,
            onSession,
            cancellationToken
        ) : Task<RunResult<'Output>> =
        task {
            let startedAt = DateTimeOffset.UtcNow
            let mutable output: 'Output voption = ValueNone
            let mutable failure: CircuitFailure voption = ValueNone
            let mutable usage = RunUsage(0, 0)
            let mutable resultSession = runOptions.Session

            if options.ToolResolvers.Count = 0 then
                // Preserve provider token streaming when no tool can request an approval.
                let stream =
                    MafRuntimeStreamingDispatch.RunStreaming
                        (box this)
                        schedulerRunId
                        nodePath
                        idempotencyKey
                        agent
                        signature
                        input
                        runOptions
                        options.JsonSerializerOptions
                        onSession
                        cancellationToken

                let enumerator = stream.GetAsyncEnumerator(cancellationToken)

                try
                    let mutable more = true

                    while more do
                        let! available = enumerator.MoveNextAsync().AsTask()
                        more <- available

                        if available then
                            let event = enumerator.Current

                            match event.Kind with
                            | RunEventKind.OutputDelta ->
                                match event.TextDelta with
                                | ValueSome delta -> do! onDelta delta
                                | ValueNone -> ()
                            | RunEventKind.RunCompleted ->
                                output <- event.Value
                                usage <- event.RuntimeUsage
                                resultSession <- event.RuntimeSession
                            | RunEventKind.RunFailed ->
                                failure <- event.Failure
                                usage <- event.RuntimeUsage
                                resultSession <- event.RuntimeSession
                            | _ -> ()
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            else
                // Approval-capable leaves retain one compiled agent and provider session across
                // rounds. Core owns publication and matching of each approval response.
                let! interactive =
                    MafRuntimeInteractiveDispatch.Start
                        (box this)
                        schedulerRunId
                        nodePath
                        idempotencyKey
                        agent
                        signature
                        input
                        runOptions
                        onSession
                        cancellationToken

                try
                    let enumerator = interactive.Events.GetAsyncEnumerator(cancellationToken)

                    try
                        let mutable more = true

                        while more do
                            let! available = enumerator.MoveNextAsync().AsTask()
                            more <- available

                            if available then
                                let event = enumerator.Current

                                match event.Kind with
                                | RunEventKind.OutputDelta ->
                                    match event.TextDelta with
                                    | ValueSome delta -> do! onDelta delta
                                    | ValueNone -> ()
                                | RunEventKind.ApprovalRequested ->
                                    match event.Approval with
                                    | ValueSome request ->
                                        let! response = onApproval request
                                        do! interactive.RespondAsync(response, cancellationToken).AsTask()
                                    | ValueNone -> ()
                                | RunEventKind.RunCompleted ->
                                    output <- event.Value
                                    usage <- event.RuntimeUsage
                                    resultSession <- event.RuntimeSession
                                | RunEventKind.RunFailed ->
                                    failure <- event.Failure
                                    usage <- event.RuntimeUsage
                                    resultSession <- event.RuntimeSession
                                | _ -> ()
                    finally
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                finally
                    (interactive :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

            let result =
                match output, failure with
                | ValueSome value, _ -> CircuitResult<'Output>.Success value
                | _, ValueSome error -> CircuitResult<'Output>.Error error
                | _ ->
                    CircuitResult<'Output>
                        .Error(
                            MafErrors.providerFailure
                                schedulerRunId
                                "The provider execution ended without a terminal response."
                                ValueNone
                                ValueNone
                        )

            return RunResult(schedulerRunId, result, usage, resultSession, startedAt, DateTimeOffset.UtcNow)
        }

    /// Projects unified Circuit observations into configured MAF observers and telemetry.
    override _.ObserveCircuitAsync(observation, runOptions, cancellationToken) =
        task {
            match unbox<CircuitObservation> observation with
            | CircuitRunStarted info ->
                let session =
                    MafObserver.createCircuitRunSession
                        options.Observers
                        info.RunId
                        info.DefinitionId
                        info.DefinitionVersion
                        runOptions.Services

                do! MafObserver.notifyStartedAsync session info.StartedAt ValueNone ValueNone cancellationToken
            | CircuitNodeStarted(runId, info) ->
                do! MafObserver.notifyNodeStartedAsync (MafObserver.tryGetSession runId) info.NodePath cancellationToken
            | CircuitApprovalRequested(runId, approval) ->
                do!
                    MafObserver.notifyApprovalRequestedAsync
                        (MafObserver.tryGetSession runId)
                        approval.RequestId
                        approval.ToolName
                        approval
                        cancellationToken
            | CircuitNodeCompleted(runId, info, failure) ->
                do!
                    MafObserver.notifyNodeCompletedAsync
                        (MafObserver.tryGetSession runId)
                        info.NodePath
                        failure
                        cancellationToken
            | CircuitRunCompleted(runId, failure, usage, startedAt, completedAt) ->
                let session = MafObserver.tryGetSession runId

                let kind =
                    if failure.IsSome then
                        RunEventKind.RunFailed
                    else
                        RunEventKind.RunCompleted

                do!
                    MafObserver.notifyCircuitRootEventAsync
                        session
                        kind
                        ValueNone
                        failure
                        (ValueSome startedAt)
                        (ValueSome completedAt)
                        (ValueSome usage)
                        MafErrors.emptyDiagnosticMetadata
                        cancellationToken

                MafObserver.unregisterSession session
        }

    /// <summary>Gets the serialize session core async value.</summary>
    override this.SerializeSessionCoreAsync(agent, session, runOptions, cancellationToken) =
        this.SerializeSessionAsyncCore(agent, session, runOptions, cancellationToken)

    /// Restores a provider session with the receiving process's rebound services and capabilities.
    override this.DeserializeSessionCoreAsync(agent, state, runOptions, cancellationToken) =
        this.DeserializeSessionAsyncCore(agent, state, runOptions, cancellationToken)
