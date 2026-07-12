#nowarn "57"

namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection
open Microsoft.FSharp.Reflection
open Xunit

[<AllowNullLiteral>]
type private FixedResponseAgent(responseFactory: unit -> AgentResponse, ?providerSession: AgentSession) =
    inherit AIAgent()

    let providerSession =
        defaultArg providerSession (DummyAgentSession() :> AgentSession)

    override _.CreateSessionCoreAsync(_cancellationToken) =
        ValueTask<AgentSession>(providerSession)

    override _.SerializeSessionCoreAsync(_session, _options, _cancellationToken) =
        ValueTask<JsonElement>(JsonDocument.Parse("{\"provider\":true}").RootElement.Clone())

    override _.DeserializeSessionCoreAsync(_element, _options, _cancellationToken) =
        ValueTask<AgentSession>(providerSession)

    override _.RunCoreAsync(_messages, _session, _options, _cancellationToken) = Task.FromResult(responseFactory ())

    override _.RunCoreStreamingAsync(_messages, _session, _options, _cancellationToken) =
        ArrayAsyncEnumerable(Array.empty) :> IAsyncEnumerable<AgentResponseUpdate>

type private CancellingSessionAgent(cancellationToken: CancellationToken) =
    inherit AIAgent()

    override _.CreateSessionCoreAsync(_cancellationToken) =
        ValueTask<AgentSession>(Task.FromCanceled<AgentSession>(cancellationToken))

    override _.SerializeSessionCoreAsync(_session, _options, _cancellationToken) =
        ValueTask<JsonElement>(JsonDocument.Parse("{}").RootElement.Clone())

    override _.DeserializeSessionCoreAsync(_element, _options, _cancellationToken) =
        ValueTask<AgentSession>(Task.FromCanceled<AgentSession>(cancellationToken))

    override _.RunCoreAsync(_messages, _session, _options, _cancellationToken) =
        Task.FromCanceled<AgentResponse>(cancellationToken)

    override _.RunCoreStreamingAsync(_messages, _session, _options, _cancellationToken) =
        ArrayAsyncEnumerable(Array.empty) :> IAsyncEnumerable<AgentResponseUpdate>

type private ThrowingAsyncEnumerable<'T>(exceptionFactory: unit -> exn) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            { new IAsyncEnumerator<'T> with
                member _.Current = Unchecked.defaultof<'T>

                member _.MoveNextAsync() =
                    ValueTask<bool>(Task.FromException<bool>(exceptionFactory ()))

                member _.DisposeAsync() = ValueTask() }

type private ThrowingPublicObserver() =
    interface Circuit.IRunObserver with
        member _.OnEventAsync(_event, _cancellationToken) =
            raise (InvalidOperationException("observer boom"))

type private EmptyPublicToolResolver() =
    interface Circuit.IToolResolver with
        member _.ResolveAsync(_context, _cancellationToken) =
            ValueTask<IReadOnlyList<Circuit.ResolvedTool>>(Array.empty<Circuit.ResolvedTool>)

type private EmptyPublicSkillResolver() =
    interface Circuit.ISkillResolver with
        member _.ResolveAsync(_context, _cancellationToken) =
            ValueTask<IReadOnlyList<Circuit.ResolvedSkill>>(Array.empty<Circuit.ResolvedSkill>)

type private PublicRecordingObserver() =
    let events = ResizeArray<Circuit.RunEventEnvelope>()

    member _.Events = events

    interface Circuit.IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            events.Add event
            ValueTask()

type private NonWorkflowRuntime() =
    interface ICircuitRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _cancellationToken: CancellationToken
            ) : Task<RunResult<'Output>> =
            raise (InvalidOperationException("not used"))

        member _.RunStreamingAsync<'Input, 'Output>
            (
                _agent: AgentDefinition,
                _signature: Signature<'Input, 'Output>,
                _input: 'Input,
                _options: RunOptions,
                _cancellationToken: CancellationToken
            ) : IAsyncEnumerable<RunEvent<'Output>> =
            raise (InvalidOperationException("not used"))

        member _.SerializeSessionAsync
            (_agent: AgentDefinition, _session: CircuitSession, _cancellationToken: CancellationToken)
            =
            raise (InvalidOperationException("not used"))

        member _.DeserializeSessionAsync
            (_agent: AgentDefinition, _state: JsonElement, _cancellationToken: CancellationToken)
            =
            raise (InvalidOperationException("not used"))

module private AdapterCoverageHelpers =
    let private runOptionsCtor =
        typeof<RunOptions>
            .GetConstructor(
                Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic,
                null,
                [| typeof<CircuitSession voption>
                   typeof<string voption>
                   typeof<string voption>
                   typeof<IReadOnlyDictionary<string, string>>
                   typeof<StructuredOutputPolicy>
                   typeof<SensitiveDataMode>
                   typeof<IServiceProvider> |],
                null
            )

    let createRunOptionsWithSensitiveData sensitiveDataMode =
        let tags =
            Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let sessionValue: CircuitSession voption = ValueNone
        let tenantValue: string voption = ValueNone
        let userValue: string voption = ValueNone

        runOptionsCtor.Invoke(
            [| box sessionValue
               box tenantValue
               box userValue
               box tags
               box StructuredOutputPolicy.NativeOnly
               box sensitiveDataMode
               box (NullServiceProvider() :> IServiceProvider) |]
        )
        :?> RunOptions

    let createJsonResponseFormat<'T> () =
        ChatResponseFormat.ForJsonSchema<'T>(CircuitJson.createOptions (), typeof<'T>.Name, null)

module MafErrorsTests =
    open AdapterCoverageHelpers

    [<Fact>]
    let ``maf errors sanitize unsafe model visible messages and preserve safe ones`` () =
        Assert.Equal("fallback", MafErrors.sanitizeModelVisibleMessage "fallback" null)
        Assert.Equal("fallback", MafErrors.sanitizeModelVisibleMessage "fallback" " ")
        Assert.Equal("fallback", MafErrors.sanitizeModelVisibleMessage "fallback" "bad\npath")
        Assert.Equal("fallback", MafErrors.sanitizeModelVisibleMessage "fallback" "/tmp/secret.txt")
        Assert.Equal("fallback", MafErrors.sanitizeModelVisibleMessage "fallback" "C:\\secret.txt")
        Assert.Equal("safe message", MafErrors.sanitizeModelVisibleMessage "fallback" "safe message")

    [<Fact>]
    let ``maf errors format validation issues and classify representative exceptions`` () =
        let issues =
            [| { Path = "$.token"
                 Code = "required"
                 Message = "Required." }
               { Path = "$.text"
                 Code = "invalid"
                 Message = "Invalid." } |]
            :> IReadOnlyList<ValidationIssue>

        Assert.Equal("Validation failed.", MafErrors.formatValidationIssues null)
        Assert.Equal("Validation failed.", MafErrors.formatValidationIssues (Array.empty<ValidationIssue>))
        Assert.Equal("Validation failed: $.token: Required.; $.text: Invalid.", MafErrors.formatValidationIssues issues)

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let aggregate =
            AggregateException(InvalidOperationException("boom"), OperationCanceledException())

        Assert.True(MafErrors.isCancellationRequested cts.Token aggregate)
        Assert.True(MafErrors.isCancellationRequested CancellationToken.None (OperationCanceledException()))
        Assert.False(MafErrors.isCancellationRequested CancellationToken.None (InvalidOperationException("boom")))

        Assert.True(MafErrors.isStructuredOutputUnsupported (NotSupportedException("unsupported")))

        Assert.True(
            MafErrors.isStructuredOutputUnsupported (
                InvalidOperationException("response format is not supported by this provider")
            )
        )

        Assert.False(MafErrors.isStructuredOutputUnsupported (InvalidOperationException("ordinary")))

        Assert.True(MafErrors.isDecodeFailure (JsonException("bad json")))
        Assert.True(MafErrors.isDecodeFailure (InvalidOperationException("did not contain json")))

        Assert.True(
            MafErrors.isDecodeFailure (InvalidOperationException("structured output wrapper missing data property"))
        )

        Assert.False(MafErrors.isDecodeFailure (InvalidOperationException("ordinary")))

    [<Fact>]
    let ``maf errors combine usage details saturates counts and create usage clamps values`` () =
        let left = UsageDetails()
        left.InputTokenCount <- Nullable(Int64.MaxValue - 3L)
        left.OutputTokenCount <- Nullable<int64>(10L)
        left.TotalTokenCount <- Nullable(Int64.MaxValue - 2L)
        left.CachedInputTokenCount <- Nullable<int64>(-5L)
        left.ReasoningTokenCount <- Nullable<int64>(1L)
        left.AdditionalCounts <- AdditionalPropertiesDictionary<int64>(dict [ "cache", 3L; "left", 4L ])

        let right = UsageDetails()
        right.InputTokenCount <- Nullable<int64>(10L)
        right.OutputTokenCount <- Nullable(Int64.MaxValue)
        right.TotalTokenCount <- Nullable<int64>(50L)
        right.CachedInputTokenCount <- Nullable<int64>(6L)
        right.ReasoningTokenCount <- Nullable<int64>(2L)
        right.InputAudioTokenCount <- Nullable<int64>(-1L)
        right.OutputTextTokenCount <- Nullable<int64>(7L)
        right.AdditionalCounts <- AdditionalPropertiesDictionary<int64>(dict [ "cache", 8L; "right", 9L ])

        let combined = MafErrors.combineUsageDetails left right

        Assert.Equal(Int64.MaxValue, combined.InputTokenCount.Value)
        Assert.Equal(Int64.MaxValue, combined.OutputTokenCount.Value)
        Assert.Equal(Int64.MaxValue, combined.TotalTokenCount.Value)
        Assert.Equal(6L, combined.CachedInputTokenCount.Value)
        Assert.Equal(3L, combined.ReasoningTokenCount.Value)
        Assert.Equal(0L, combined.InputAudioTokenCount.Value)
        Assert.Equal(7L, combined.OutputTextTokenCount.Value)
        Assert.Equal(11L, combined.AdditionalCounts.["cache"])
        Assert.Equal(4L, combined.AdditionalCounts.["left"])
        Assert.Equal(9L, combined.AdditionalCounts.["right"])

        let usage = UsageDetails()
        usage.InputTokenCount <- Nullable<int64>(-5L)
        usage.OutputTokenCount <- Nullable(Int64.MaxValue)

        let circuitUsage = MafErrors.createUsage usage
        Assert.Equal(0, circuitUsage.InputTokens)
        Assert.Equal(Int32.MaxValue, circuitUsage.OutputTokens)
        Assert.Equal(0, (MafErrors.createUsage null).TotalTokens)

module MafSessionContractTests =
    open Helpers

    [<Fact>]
    let ``session contracts round trip provider session lookup and binding metadata`` () =
        let agent = createAgent "Persist state."
        let providerSession = DummyAgentSession() :> AgentSession
        let binding = "binding-fingerprint"
        let metadata = MafSessionContracts.createSessionMetadata binding

        let session =
            MafSessionContracts.createCircuitSession agent metadata providerSession

        let fingerprint = MafSessionContracts.definitionFingerprint agent

        let resolved = MafSessionContracts.getProviderSession fingerprint session
        let matching = MafSessionContracts.ensureMatchingSession agent session

        Assert.True(MafSessionContracts.hasMatchingSessionBinding binding session)
        Assert.Same(providerSession, resolved.Value)
        Assert.Same(providerSession, matching)
        Assert.False(MafSessionContracts.hasMatchingSessionBinding "other-binding" session)

    [<Fact>]
    let ``session contracts generate fallback ids and reject mismatched provider sessions`` () =
        let agent = createAgent "Persist state."
        let providerSession = DummyAgentSession() :> AgentSession

        let wrapped = MafSessionContracts.createCircuitSession agent null providerSession

        Assert.False(String.IsNullOrWhiteSpace wrapped.Id)

        let emptyMetadata =
            Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let wrongAdapter =
            CircuitSession(
                "id",
                emptyMetadata,
                ValueSome "different-adapter",
                ValueSome(MafSessionContracts.definitionFingerprint agent),
                ValueSome(providerSession :> obj)
            )

        let wrongFingerprint =
            CircuitSession(
                "id",
                emptyMetadata,
                ValueSome MafSessionContracts.AdapterId,
                ValueSome "different-fingerprint",
                ValueSome(providerSession :> obj)
            )

        let wrongProvider =
            CircuitSession(
                "id",
                emptyMetadata,
                ValueSome MafSessionContracts.AdapterId,
                ValueSome(MafSessionContracts.definitionFingerprint agent),
                ValueSome("not-a-session" :> obj)
            )

        Assert.True(
            MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) wrongAdapter
            |> ValueOption.isNone
        )

        Assert.True(
            MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) wrongFingerprint
            |> ValueOption.isNone
        )

        Assert.True(
            MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) wrongProvider
            |> ValueOption.isNone
        )

    [<Fact>]
    let ``session contracts parse envelope validates required properties and metadata shapes`` () =
        let agent = createAgent "Persist state."
        let fingerprint = MafSessionContracts.definitionFingerprint agent
        let providerState = JsonDocument.Parse("{\"provider\":true}").RootElement.Clone()
        let metadataDictionary = Dictionary<string, string>(StringComparer.Ordinal)
        metadataDictionary.Add("alpha", "one")
        let metadata = metadataDictionary :> IReadOnlyDictionary<string, string>

        let envelope =
            MafSessionContracts.serializeEnvelope fingerprint metadata providerState

        let parsedFingerprint, parsedMetadata, parsedProviderState =
            MafSessionContracts.parseEnvelope fingerprint envelope

        Assert.Equal(fingerprint, parsedFingerprint)
        Assert.Equal("one", parsedMetadata.["alpha"])
        Assert.True(parsedProviderState.GetProperty("provider").GetBoolean())

        let expectInvalid stateFragment message =
            let ex =
                Assert.Throws<ArgumentException>(fun () ->
                    MafSessionContracts.parseEnvelope fingerprint stateFragment |> ignore)

            Assert.Contains(message, ex.Message)

        expectInvalid (JsonDocument.Parse("[]").RootElement.Clone()) "must be a JSON object"

        expectInvalid
            (JsonDocument
                .Parse(
                    "{\"formatVersion\":0,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":\"x\",\"providerState\":{}}"
                )
                .RootElement.Clone())
            "unsupported formatVersion"

        expectInvalid
            (JsonDocument
                .Parse("{\"formatVersion\":1,\"definitionFingerprint\":\"x\",\"providerState\":{}}")
                .RootElement.Clone())
            "missing 'adapter'"

        expectInvalid
            (JsonDocument
                .Parse(
                    "{\"formatVersion\":1,\"adapter\":\"wrong\",\"definitionFingerprint\":\"x\",\"providerState\":{}}"
                )
                .RootElement.Clone())
            "adapter is not supported"

        expectInvalid
            (JsonDocument
                .Parse("{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"providerState\":{}}")
                .RootElement.Clone())
            "missing 'definitionFingerprint'"

        expectInvalid
            (JsonDocument
                .Parse(
                    $"{{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":\"other\",\"providerState\":{{}}}}"
                )
                .RootElement.Clone())
            "does not match the supplied agent definition"

        expectInvalid
            (JsonDocument
                .Parse(
                    $"{{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":\"{fingerprint}\",\"metadata\":[],\"providerState\":{{}}}}"
                )
                .RootElement.Clone())
            "metadata must be a JSON object"

        expectInvalid
            (JsonDocument
                .Parse(
                    $"{{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":\"{fingerprint}\",\"metadata\":{{\"alpha\":1}},\"providerState\":{{}}}}"
                )
                .RootElement.Clone())
            "metadata values must be strings"

        expectInvalid
            (JsonDocument
                .Parse(
                    $"{{\"formatVersion\":1,\"adapter\":\"circuit.microsoft-agent-framework\",\"definitionFingerprint\":\"{fingerprint}\"}}"
                )
                .RootElement.Clone())
            "missing 'providerState'"

module MafObserverTests =
    open Helpers

    [<Fact>]
    let ``maf observer sessions dispatch agent and workflow events and tolerate observer failures`` () =
        let recording = RecordingObserver()
        let failing = ThrowingPublicObserver()

        let observers =
            [| failing :> Circuit.IRunObserver; recording :> Circuit.IRunObserver |]

        let signature = createSignature<TestOutput> ()
        let runId = RunId.New()
        let startedAt = DateTimeOffset.UtcNow
        let completedAt = startedAt.AddSeconds(1)

        let approval =
            ApprovalRequest("approval-1", "tool.read", ValueSome "{\"token\":\"value\"}")

        let failure = MafErrors.toolFailure runId "tool failed" ValueNone

        let agentSession =
            MafObserver.createAgentRunSession
                observers
                runId
                "Agent Name"
                signature.Id
                signature.Version
                (ValueSome "model-1")
                null

        MafObserver.notifyStartedAsync
            agentSession
            startedAt
            (ValueSome "prompt")
            (ValueSome "input")
            CancellationToken.None
        |> _.Wait()

        MafObserver.notifyToolStartedAsync runId "tool-op" "tool.read" (ValueSome "{}") CancellationToken.None
        |> _.Wait()

        MafObserver.notifyApprovalRequestedAsync agentSession "approval-op" "tool.read" approval CancellationToken.None
        |> _.Wait()

        MafObserver.notifyToolCompletedAsync runId "tool-op" "tool.read" (ValueSome failure) CancellationToken.None
        |> _.Wait()

        let diagnosticMetadata = Dictionary<string, string>(StringComparer.Ordinal)
        diagnosticMetadata.Add("meta", "value")

        MafObserver.notifyRootEventAsync
            agentSession
            RunEventKind.RunCompleted
            ValueNone
            (ValueSome "output")
            ValueNone
            ValueNone
            (ValueSome startedAt)
            (ValueSome completedAt)
            true
            (ValueSome(RunUsage(3, 4)))
            ValueNone
            (diagnosticMetadata :> IReadOnlyDictionary<string, string>)
            CancellationToken.None
        |> _.Wait()

        let workflowRunId = RunId.New()

        let workflowSession =
            MafObserver.createWorkflowRunSession observers workflowRunId signature.Id signature.Version

        MafObserver.notifyWorkflowStepStartedAsync workflowSession "step.one" CancellationToken.None
        |> _.Wait()

        MafObserver.notifyWorkflowStepCompletedAsync workflowSession "step.one" ValueNone CancellationToken.None
        |> _.Wait()

        MafObserver.notifyWorkflowRootEventAsync
            workflowSession
            RunEventKind.RunFailed
            ValueNone
            (ValueSome failure)
            ValueNone
            (ValueSome completedAt)
            (Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>)
            CancellationToken.None
        |> _.Wait()

        MafObserver.unregisterSession agentSession
        MafObserver.unregisterSession workflowSession

        let events = recording.Events |> Seq.toArray

        Assert.Contains(
            events,
            fun event -> event.Kind = AgentRunEventKind.RunStarted && event.OperationName = "agent.run"
        )

        Assert.Contains(
            events,
            fun event -> event.Kind = AgentRunEventKind.ToolStarted && event.OperationName = "tool.read"
        )

        Assert.Contains(
            events,
            fun event ->
                event.Kind = AgentRunEventKind.ApprovalRequested
                && event.OperationName = "tool.read"
        )

        Assert.Contains(
            events,
            fun event ->
                event.Kind = AgentRunEventKind.ToolCompleted
                && event.Failure.Code = AgentFailureCode.Tool
        )

        Assert.Contains(events, fun event -> event.Kind = AgentRunEventKind.RunCompleted && event.Repaired)

        Assert.Contains(
            events,
            fun event -> event.Kind = AgentRunEventKind.StepStarted && event.OperationName = "step.one"
        )

        Assert.Contains(
            events,
            fun event -> event.Kind = AgentRunEventKind.StepCompleted && event.OperationName = "step.one"
        )

        Assert.Contains(
            events,
            fun event -> event.Kind = AgentRunEventKind.RunFailed && event.OperationName = "workflow.run"
        )

        Assert.True(MafObserver.tryGetSession runId |> ValueOption.isNone)
        Assert.True(MafObserver.tryGetSession workflowRunId |> ValueOption.isNone)

module CSharpFacadeAndRuntimeHelperTests =
    open AdapterCoverageHelpers
    open Helpers

    [<Fact>]
    let ``csharp facade creates frozen runtime options and runtime factory guards null inputs`` () =
        let secondary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"secondary\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let publicOptions = Circuit.MicrosoftAgentFrameworkOptions()
        publicOptions.DefaultModelId <- "model-default"
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.PropertyNamingPolicy <- ApprovalNamingPolicy()
        publicOptions.JsonSerializerOptions <- jsonOptions
        publicOptions.SecondaryStructuredOutputClient <- secondary

        let publicObserver = PublicRecordingObserver()

        let runtimeOptions =
            CSharpFacadeAdapters.createRuntimeOptions
                publicOptions
                [| EmptyPublicToolResolver() :> Circuit.IToolResolver |]
                [| EmptyPublicSkillResolver() :> Circuit.ISkillResolver |]
                [| publicObserver :> Circuit.IRunObserver |]

        Assert.Equal(ValueSome "model-default", runtimeOptions.DefaultModelId)
        Assert.True(runtimeOptions.JsonSerializerOptions.IsReadOnly)
        Assert.NotSame(jsonOptions, runtimeOptions.JsonSerializerOptions)
        Assert.Equal(1, runtimeOptions.ToolResolvers.Count)
        Assert.Equal(1, runtimeOptions.SkillResolvers.Count)
        Assert.Equal(1, runtimeOptions.Observers.Count)
        Assert.Same(secondary, runtimeOptions.SecondaryStructuredOutputClient.Value)

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(null, null, null, null, null)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(secondary, null, null, null, null)
            |> ignore)
        |> ignore

        let client =
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(
                secondary,
                publicOptions,
                [| EmptyPublicToolResolver() :> Circuit.IToolResolver |],
                [| EmptyPublicSkillResolver() :> Circuit.ISkillResolver |],
                [| publicObserver :> Circuit.IRunObserver |]
            )

        Assert.NotNull(client)

    [<Fact>]
    let ``service collection registrations surface missing chat clients and unsupported workflow runtimes`` () =
        let servicesWithoutChat = ServiceCollection()

        ServiceCollectionExtensions.AddCircuit(servicesWithoutChat, Action<Circuit.CircuitOptions>(fun _ -> ()))
        |> ignore

        use providerWithoutChat = servicesWithoutChat.BuildServiceProvider()

        let missingChat =
            Assert.Throws<InvalidOperationException>(fun () ->
                providerWithoutChat.GetRequiredService<Circuit.ICircuitClient>() |> ignore)

        Assert.Contains("AddCircuit requires an IChatClient singleton", missingChat.Message)

        let services = ServiceCollection()
        services.AddSingleton<ICircuitRuntime>(NonWorkflowRuntime()) |> ignore

        ServiceCollectionExtensions.AddCircuit(services, Action<Circuit.CircuitOptions>(fun _ -> ()))
        |> ignore

        use provider = services.BuildServiceProvider()

        let workflowRuntime = provider.GetRequiredService<Circuit.Core.IWorkflowRuntime>()

        let ex =
            Assert.Throws<InvalidOperationException>(fun () ->
                workflowRuntime.RunAsync(
                    Workflow.define
                        "wf.unsupported"
                        "1.0.0"
                        (Workflow.code "step" (fun _ value -> Task.FromResult(value + 1))),
                    1,
                    WorkflowRunOptions.Default,
                    CancellationToken.None
                )
                |> ignore)

        Assert.Contains("does not support workflows", ex.Message)

    [<Fact>]
    let ``add maf runtime stores frozen snapshot and registers circuit runtime`` () =
        let services = ServiceCollection()

        let chatClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let options = MafRuntimeOptions()
        options.DefaultModelId <- ValueSome "maf-model"
        let jsonOptions = CircuitJson.createOptions ()
        jsonOptions.PropertyNamingPolicy <- ApprovalNamingPolicy()
        options.JsonSerializerOptions <- jsonOptions

        services.AddMafRuntime(chatClient, options) |> ignore
        use provider = services.BuildServiceProvider()

        let snapshot = provider.GetRequiredService<MafRuntimeOptions>()
        let runtime = provider.GetRequiredService<ICircuitRuntime>()

        Assert.Equal(ValueSome "maf-model", snapshot.DefaultModelId)
        Assert.True(snapshot.JsonSerializerOptions.IsReadOnly)
        Assert.NotSame(jsonOptions, snapshot.JsonSerializerOptions)
        Assert.NotNull(runtime)

        Assert.Throws<InvalidOperationException>(fun () -> snapshot.DefaultModelId <- ValueSome "mutate")
        |> ignore

    [<Fact>]
    let ``runtime helpers resolve request models create prompts and preserve or redact repair diagnostics`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtimeWithDefault =
            createMafRuntimeWith
                (fun options -> options.DefaultModelId <- ValueSome "default-model")
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let agentWithHint =
            AgentDefinition.Create(
                "agent.model",
                "1.0.0",
                "Agent Model",
                "Base instructions",
                ValueSome "agent-model",
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let agentWithoutHint = createAgent "Base instructions"
        let signature = createSignature<TestOutput> ()

        Assert.Equal(ValueSome "agent-model", runtimeWithDefault.ResolveRequestModel agentWithHint)
        Assert.Equal(ValueSome "default-model", runtimeWithDefault.ResolveRequestModel agentWithoutHint)

        Assert.Equal(
            "Base instructions\n\nReturn structured JSON.",
            runtimeWithDefault.CreatePrompt(agentWithoutHint, signature)
        )

        let repairClient =
            new FakeChatClient(
                (fun _messages _options _ct ->
                    let repaired = jsonResponse "{\"text\":\"ok\"}"
                    repaired.Usage <- Helpers.usageDetails 4 5
                    Task.FromResult(repaired)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let original = AgentResponse(ChatMessage(ChatRole.Assistant, "raw-original"))
        original.Usage <- Helpers.usageDetails 2 3
        let repairOptions = MafStructuredOutputAgentOptions()
        repairOptions.ChatOptions <- ChatOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let repaired =
            MafStructuredOutputAgent(FixedResponseAgent(fun () -> original), repairClient, repairOptions)
                .RunAsync("input", DummyAgentSession(), null, null, CancellationToken.None)
                .Result

        let standardMetadata =
            runtimeWithDefault.CreateDiagnosticMetadata(
                createRunOptionsWithSensitiveData SensitiveDataMode.Standard,
                repaired
            )

        let redactedMetadata =
            runtimeWithDefault.CreateDiagnosticMetadata(
                createRunOptionsWithSensitiveData SensitiveDataMode.Redact,
                repaired
            )

        Assert.Equal("true", standardMetadata.["circuit.repaired"])
        Assert.Equal("raw-original", standardMetadata.["circuit.repair.originalResponse"])
        Assert.Equal("true", redactedMetadata.["circuit.repaired"])
        Assert.False(redactedMetadata.ContainsKey("circuit.repair.originalResponse"))

    [<Fact>]
    let ``prepare session reuses matching sessions and rejects binding mismatches`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore primary None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Reuse session."
        let signature = createSignature<TestOutput> ()
        let runId = RunId.New()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let runContext = runtime.CreateRunContext(runId, agent, signature, runOptions)

        let binding =
            MafSessionContracts.createSessionBinding
                runContext
                signature
                Array.empty<ResolvedMafTool>
                Array.empty<ResolvedSkill>

        let providerSession = DummyAgentSession() :> AgentSession

        let wrapped =
            MafSessionContracts.createCircuitSession
                agent
                (MafSessionContracts.createSessionMetadata binding)
                providerSession

        let runtimeAgent =
            FixedResponseAgent((fun () -> AgentResponse(ChatMessage(ChatRole.Assistant, "ok"))), providerSession)

        match
            runtime.PrepareSessionAsync
                runId
                runtimeAgent
                agent
                binding
                (createRunOptions (Some wrapped) StructuredOutputPolicy.NativeOnly)
                CancellationToken.None
            |> _.Result
        with
        | Ok(resolvedSession, wrappedSession) ->
            Assert.Same(providerSession, resolvedSession)
            Assert.Equal(Some wrapped.Id, wrappedSession |> ValueOption.toOption |> Option.map _.Id)
        | Error failure -> Assert.True(false, failure.Message)

        let mismatchedSession =
            MafSessionContracts.createCircuitSession agent null providerSession

        match
            runtime.PrepareSessionAsync
                runId
                runtimeAgent
                agent
                binding
                (createRunOptions (Some mismatchedSession) StructuredOutputPolicy.NativeOnly)
                CancellationToken.None
            |> _.Result
        with
        | Ok _ -> Assert.True(false, "Expected checkpoint mismatch.")
        | Error failure -> Assert.Equal(CircuitFailureCode.CheckpointMismatch, failure.Code)

module MafStructuredOutputTests =
    open AdapterCoverageHelpers
    open Helpers

    [<Fact>]
    let ``structured output helpers unwrap repaired responses and clear response formats safely`` () =
        let captured = ResizeArray<IReadOnlyList<ChatMessage> * ChatOptions>()

        let chatClient =
            new FakeChatClient(
                (fun messages options _ct ->
                    captured.Add(messages, options)
                    let repaired = jsonResponse "{\"text\":\"ok\"}"
                    repaired.Usage <- Helpers.usageDetails 4 5
                    Task.FromResult(repaired)),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let original = AgentResponse(ChatMessage(ChatRole.Assistant, "raw"))
        let originalUsage = UsageDetails()
        originalUsage.InputTokenCount <- Nullable<int64>(4L)
        original.Usage <- originalUsage

        let inner = FixedResponseAgent(fun () -> original)
        let fallbackOptions = MafStructuredOutputAgentOptions()
        fallbackOptions.ChatOptions <- ChatOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())
        let agent = MafStructuredOutputAgent(inner, chatClient, fallbackOptions)

        let repairedResponse =
            agent.RunAsync("input", DummyAgentSession(), null, null, CancellationToken.None).Result

        Assert.True(MafStructuredOutput.wasRepaired repairedResponse)
        Assert.Equal(ValueSome "raw", MafStructuredOutput.tryGetOriginalResponseText repairedResponse)

        Assert.Equal(
            Some originalUsage,
            MafStructuredOutput.tryGetOriginalUsage repairedResponse |> ValueOption.toOption
        )

        Assert.True(MafStructuredOutput.tryGetOriginalResponseText (original) |> ValueOption.isNone)
        Assert.Null(MafStructuredOutput.removeResponseFormat null)

        let options = AgentRunOptions()
        options.ResponseFormat <- createJsonResponseFormat<TestOutput> ()
        let clone = MafStructuredOutput.removeResponseFormat options
        Assert.NotSame(options, clone)
        Assert.NotNull(options.ResponseFormat)
        Assert.Null(clone.ResponseFormat)

    [<Fact>]
    let ``structured output agent uses explicit or fallback json response formats`` () =
        let captured = ResizeArray<IReadOnlyList<ChatMessage> * ChatOptions>()

        let chatClient =
            new FakeChatClient(
                (fun messages options _ct ->
                    captured.Add(messages, options)
                    Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let inner =
            FixedResponseAgent(fun () -> AgentResponse(ChatMessage(ChatRole.Assistant, "plain text")))

        let fallbackOptions = MafStructuredOutputAgentOptions()
        fallbackOptions.ChatClientSystemMessage <- "repair-system"
        fallbackOptions.ChatOptions <- ChatOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let fallbackAgent = MafStructuredOutputAgent(inner, chatClient, fallbackOptions)

        let explicitRunOptions =
            AgentRunOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let fallbackResponse =
            fallbackAgent
                .RunAsync("input", DummyAgentSession(), null, explicitRunOptions, CancellationToken.None)
                .Result

        Assert.NotNull(fallbackResponse)

        let messages, usedOptions = captured |> Seq.last
        Assert.Equal(ChatRole.System, messages[0].Role)
        Assert.Equal("repair-system", messages[0].Text)
        Assert.Equal(ChatRole.User, messages[1].Role)
        Assert.Equal("plain text", messages[1].Text)
        Assert.NotSame(explicitRunOptions.ResponseFormat, usedOptions.ResponseFormat)

module RuntimeFailureCoverageTests =
    open Helpers

    [<Fact>]
    let ``non streaming output validation failures surface as validation failures`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":null}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime client None Array.empty<Circuit.IRunObserver>

        let result =
            (runtime.RunAsync(
                createAgent "Return structured JSON.",
                createSignature<TestOutput> (),
                TestInput(Token = "invalid-output"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            ))
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Validation, result.Result.Failure.Code)

    [<Fact>]
    let ``streaming output validation failures emit a validation terminal event`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, "{\"text\":null}") |])
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime client None Array.empty<Circuit.IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Return structured JSON.",
                createSignature<TestOutput> (),
                TestInput(Token = "invalid-output"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        let failed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed))

        Assert.Equal(CircuitFailureCode.Validation, failed.Failure.Value.Code)

    [<Fact>]
    let ``provider structured output unsupported errors map cleanly for run and stream`` () =
        let unsupported =
            InvalidOperationException("response format is not supported by this provider")

        let nonStreamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> raise unsupported),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createRuntime nonStreamingClient None Array.empty<Circuit.IRunObserver>

        let runResult =
            (runtime.RunAsync(
                createAgent "Return structured JSON.",
                createSignature<TestOutput> (),
                TestInput(Token = "unsupported"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            ))
                .Result

        Assert.False(runResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.StructuredOutputUnsupported, runResult.Result.Failure.Code)

        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct ->
                    ThrowingAsyncEnumerable<ChatResponseUpdate>(fun () -> unsupported) :> IAsyncEnumerable<_>)
            )

        let streamingRuntime =
            createRuntime streamingClient None Array.empty<Circuit.IRunObserver>

        let events =
            streamingRuntime.RunStreamingAsync(
                createAgent "Return structured JSON.",
                createSignature<TestOutput> (),
                TestInput(Token = "unsupported"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        let failed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed))

        Assert.Equal(CircuitFailureCode.StructuredOutputUnsupported, failed.Failure.Value.Code)

    [<Fact>]
    let ``prepare session maps session creation cancellation to cancelled failure`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Prepare session."
        let signature = createSignature<TestOutput> ()
        let runId = RunId.New()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let runContext = runtime.CreateRunContext(runId, agent, signature, runOptions)

        let binding =
            MafSessionContracts.createSessionBinding
                runContext
                signature
                Array.empty<ResolvedMafTool>
                Array.empty<ResolvedSkill>

        use cts = new CancellationTokenSource()
        cts.Cancel()

        match
            runtime.PrepareSessionAsync runId (CancellingSessionAgent(cts.Token)) agent binding runOptions cts.Token
            |> _.Result
        with
        | Ok _ -> Assert.True(false, "Expected cancellation.")
        | Error failure -> Assert.Equal(CircuitFailureCode.Cancelled, failure.Code)

module WorkflowAndSessionCoverageTests =
    open Helpers
    open Circuit.MicrosoftAgentFramework.MafWorkflows

    let private createWorkflowRuntime () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"workflow\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver> :> IWorkflowRuntime

    let private collectWorkflowEvents<'T> (run: WorkflowRun<'T>) =
        task {
            let events = ResizeArray<RunEvent<'T>>()
            let enumerator = run.Events.GetAsyncEnumerator(CancellationToken.None)

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if moved then
                        events.Add enumerator.Current
                    else
                        keepGoing <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return events |> Seq.toArray
        }

    let private getPrivateGenericType name arity =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = $"{name}`{arity}")

    let private getModuleType name =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = name && valueType.IsAbstract && valueType.IsSealed)

    let private createParallelWaveState<'Input, 'Output> branchCount =
        let moduleType = getModuleType "ParallelWaveCollectorState"

        let methodInfo =
            moduleType.GetMethod(
                "create",
                Reflection.BindingFlags.Static
                ||| Reflection.BindingFlags.Public
                ||| Reflection.BindingFlags.NonPublic
            )

        methodInfo.MakeGenericMethod([| typeof<'Input>; typeof<'Output> |]).Invoke(null, [| box branchCount |])

    let private instanceFlags =
        Reflection.BindingFlags.Instance
        ||| Reflection.BindingFlags.Public
        ||| Reflection.BindingFlags.NonPublic

    let private setInstanceProperty (target: obj) name value =
        target.GetType().GetProperty(name, instanceFlags).SetValue(target, value)

    let private createParallelWaveEnvelope<'Input, 'Output>
        input
        (completed: WorkflowGraph.ParallelBranchResult<'Output>[])
        =
        let envelopeType =
            getPrivateGenericType "ParallelWaveEnvelope" 2
            |> fun valueType -> valueType.MakeGenericType([| typeof<'Input>; typeof<'Output> |])

        let envelope = Activator.CreateInstance(envelopeType, true)
        setInstanceProperty envelope "Input" (box input)
        setInstanceProperty envelope "Completed" (box completed)
        envelope

    let private createParallelWaveItem<'Input, 'Output> isSeed branchIndex branchValue envelope =
        let itemType =
            getPrivateGenericType "ParallelWaveItem" 2
            |> fun valueType -> valueType.MakeGenericType([| typeof<'Input>; typeof<'Output> |])

        let item = Activator.CreateInstance(itemType, true)
        setInstanceProperty item "IsSeed" (box isSeed)
        setInstanceProperty item "BranchIndex" (box branchIndex)
        setInstanceProperty item "BranchValue" (box branchValue)
        setInstanceProperty item "Envelope" envelope
        item

    let private captureParallelWave<'Input, 'Output> (expectedBranchIndices: int[]) (item: obj) (state: obj) =
        let moduleType = getModuleType "ParallelWaveCollectorState"

        let methodInfo =
            moduleType.GetMethod(
                "capture",
                Reflection.BindingFlags.Static
                ||| Reflection.BindingFlags.Public
                ||| Reflection.BindingFlags.NonPublic
            )

        methodInfo
            .MakeGenericMethod([| typeof<'Input>; typeof<'Output> |])
            .Invoke(null, [| box expectedBranchIndices; item; state |])

    let private unionCaseName (value: obj) =
        let caseInfo, _ =
            FSharpValue.GetUnionFields(
                value,
                value.GetType(),
                Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
            )

        caseInfo.Name

    let private unionFields (value: obj) =
        let _, fields =
            FSharpValue.GetUnionFields(
                value,
                value.GetType(),
                Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
            )

        fields

    [<Fact>]
    let ``workflow agent steps execute maf agents and prompt failures surface as workflow failures`` () =
        let runtime = createWorkflowRuntime ()

        let agentStep =
            Workflow.agent "agent.step" (createAgent "Return workflow text.") (createSignature<TestOutput> ())

        let definition = Workflow.define "workflow.agent.step" "1.0.0" agentStep

        let result =
            Workflow.run
                runtime
                definition
                (TestInput(Token = "workflow"))
                WorkflowRunOptions.Default
                CancellationToken.None
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal("workflow", result.Result.Value.Text)

        let failingPrompt =
            Workflow.request "prompt.fail" (fun (_: int) -> raise (InvalidOperationException("prompt boom")))

        let failingDefinition = Workflow.define "workflow.prompt.fail" "1.0.0" failingPrompt

        let failure =
            Workflow.run runtime failingDefinition 1 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.False(failure.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, failure.Result.Failure.Code)
        Assert.Contains("prompt.fail", failure.Result.Failure.Message)

    [<Fact>]
    let ``workflow choice without default and duplicate ids fail deterministically`` () =
        let runtime = createWorkflowRuntime ()

        let left =
            Workflow.define
                "workflow.choice.left"
                "1.0.0"
                (Workflow.code "left" (fun _ value -> Task.FromResult(value + 1)))

        let chooseMissing =
            Workflow.choose "choice.missing" (fun (_: int) -> "missing") (Map.ofList [ "left", left ]) None
            |> Workflow.define "workflow.choice.missing" "1.0.0"

        let missingResult =
            Workflow.run runtime chooseMissing 1 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.False(missingResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, missingResult.Result.Failure.Code)

        let duplicateStep = Workflow.code "dup" (fun _ value -> Task.FromResult(value + 1))

        let duplicateDefinition =
            Workflow.define "workflow.duplicate" "1.0.0" duplicateStep
            |> Workflow.thenStep duplicateStep

        let duplicateResult =
            Workflow.run runtime duplicateDefinition 1 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.False(duplicateResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, duplicateResult.Result.Failure.Code)
        Assert.Equal("The workflow failed to start.", duplicateResult.Result.Failure.Message)

    [<Fact>]
    let ``parallel aggregate state rejects invalid indexes completed envelopes and malformed state`` () =
        let state = ParallelAggregateState.create<int> 2

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = -1; Value = 1 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.InvalidBranchIndex -1 -> ()
        | other -> Assert.True(false, $"Expected invalid branch, got {other}")

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 0; Value = 10 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.Pending -> ()
        | other -> Assert.True(false, $"Expected pending, got {other}")

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 1; Value = 20 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.Complete values -> Assert.Equal<int list>([ 10; 20 ], values)
        | other -> Assert.True(false, $"Expected complete, got {other}")

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 1; Value = 30 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.AlreadyCompleted -> ()
        | other -> Assert.True(false, $"Expected already completed, got {other}")

        let malformed = ParallelAggregateState<int>()
        malformed.Received <- [| true |]
        malformed.Values <- Array.empty
        malformed.ReceivedCount <- 1

        let ex =
            Assert.Throws<InvalidOperationException>(fun () ->
                ParallelAggregateState.capture
                    2
                    ({ BranchIndex = 0; Value = 1 }: WorkflowGraph.ParallelBranchResult<int>)
                    malformed
                |> ignore)

        Assert.Contains("malformed", ex.Message)

    [<Fact>]
    let ``parallel wave collector state handles duplicates invalid indexes completion and malformed seeds`` () =
        let state = createParallelWaveState<int, int> 2

        let emptySeed =
            createParallelWaveEnvelope<int, int> 7 Array.empty<WorkflowGraph.ParallelBranchResult<int>>

        let seedItem = createParallelWaveItem<int, int> true -1 0 emptySeed

        let firstSeed = captureParallelWave<int, int> [| 0; 1 |] seedItem state
        Assert.Equal("Pending", unionCaseName firstSeed)

        let duplicateSeed = captureParallelWave<int, int> [| 0; 1 |] seedItem state
        Assert.Equal("DuplicateSeed", unionCaseName duplicateSeed)

        let invalidBranch =
            captureParallelWave<int, int> [| 0; 1 |] (createParallelWaveItem<int, int> false 9 42 null) state

        Assert.Equal("InvalidBranchIndex", unionCaseName invalidBranch)
        Assert.Equal(9, unbox<int> ((unionFields invalidBranch).[0]))

        let branchZero =
            captureParallelWave<int, int> [| 0; 1 |] (createParallelWaveItem<int, int> false 0 10 null) state

        Assert.Equal("Pending", unionCaseName branchZero)

        let duplicateBranch =
            captureParallelWave<int, int> [| 0; 1 |] (createParallelWaveItem<int, int> false 0 99 null) state

        Assert.Equal("DuplicateBranch", unionCaseName duplicateBranch)
        Assert.Equal(0, unbox<int> ((unionFields duplicateBranch).[0]))

        let branchOne =
            captureParallelWave<int, int> [| 0; 1 |] (createParallelWaveItem<int, int> false 1 20 null) state

        Assert.Equal("Ready", unionCaseName branchOne)

        let readyEnvelope = (unionFields branchOne).[0]

        let completed =
            readyEnvelope.GetType().GetProperty("Completed", instanceFlags).GetValue(readyEnvelope)
            :?> WorkflowGraph.ParallelBranchResult<int>[]

        Assert.Equal<int[]>([| 0; 1 |], completed |> Array.map _.BranchIndex)
        Assert.Equal<int[]>([| 10; 20 |], completed |> Array.map _.Value)

        let alreadyCompleted =
            captureParallelWave<int, int> [| 0; 1 |] (createParallelWaveItem<int, int> false 1 20 null) state

        Assert.Equal("AlreadyCompleted", unionCaseName alreadyCompleted)

        let malformedSeedState = createParallelWaveState<int, int> 2

        let malformedSeedEnvelope =
            createParallelWaveEnvelope<int, int>
                7
                [| ({ BranchIndex = 1; Value = 5 }: WorkflowGraph.ParallelBranchResult<int>) |]

        let malformedSeedItem =
            createParallelWaveItem<int, int> true -1 0 malformedSeedEnvelope

        let malformedSeed =
            Assert.Throws<System.Reflection.TargetInvocationException>(fun () ->
                captureParallelWave<int, int> [| 1; 2 |] malformedSeedItem malformedSeedState
                |> ignore)

        let malformedInner = malformedSeed.InnerException
        Assert.Contains("malformed", malformedInner.Message)

    [<Fact>]
    let ``runtime serializes and deserializes sessions and reports session agent failures clearly`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Persist sessions."

        let providerSession =
            buildSessionAgent runtime agent
            |> fun sessionAgent -> sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let session = MafSessionContracts.createCircuitSession agent null providerSession

        let serialized =
            runtime.SerializeSessionAsyncCore(agent, session, CancellationToken.None).AsTask().Result

        let restored =
            runtime.DeserializeSessionAsyncCore(agent, serialized, CancellationToken.None).AsTask().Result

        Assert.Equal(session.AdapterId, restored.AdapterId)
        Assert.Equal(session.DefinitionFingerprint, restored.DefinitionFingerprint)

        Assert.True(
            MafSessionContracts.getProviderSession (MafSessionContracts.definitionFingerprint agent) restored
            |> ValueOption.isSome
        )

        let mismatch =
            CircuitSession(
                "bad",
                Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let mismatchEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                runtime
                    .SerializeSessionAsyncCore(agent, mismatch, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("does not match this runtime", mismatchEx.Message)

        let failingRuntime =
            createMafRuntimeWith
                (fun options ->
                    options.SkillResolvers <-
                        [| { new Circuit.Core.ISkillResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   raise (InvalidOperationException("skill fail")) } |])
                client
                None
                Array.empty<Circuit.IRunObserver>

        let failure =
            Assert.Throws<InvalidOperationException>(fun () ->
                failingRuntime
                    .SerializeSessionAsyncCore(agent, session, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("Skill resolution failed", failure.Message)

module BindingAndResolverCoverageTests =
    open System.Runtime.ExceptionServices
    open Microsoft.Agents.AI.Workflows
    open Microsoft.Agents.AI.Workflows.Checkpointing
    open Circuit.MicrosoftAgentFramework.MafWorkflows
    open Helpers

    type internal WorkflowContextStub() =
        let traceContext =
            Dictionary<string, string>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

        let states = Dictionary<string, obj>(StringComparer.Ordinal)
        let clearedScopes = ResizeArray<string>()
        let updatedKeys = ResizeArray<string>()

        let makeStateKey scopeName key =
            if String.IsNullOrEmpty scopeName then
                key
            else
                $"{scopeName}|{key}"

        member _.States = states
        member _.ClearedScopes = clearedScopes |> Seq.toArray
        member _.UpdatedKeys = updatedKeys |> Seq.toArray

        interface IWorkflowContext with
            member _.TraceContext = traceContext
            member _.ConcurrentRunsEnabled = true
            member _.AddEventAsync(_workflowEvent, _cancellationToken) = ValueTask()
            member _.SendMessageAsync(_message, _targetId, _cancellationToken) = ValueTask()
            member _.YieldOutputAsync(_output, _cancellationToken) = ValueTask()
            member _.RequestHaltAsync() = ValueTask()

            member _.ReadStateAsync<'T>(key, scopeName, _cancellationToken) =
                match states.TryGetValue(makeStateKey scopeName key) with
                | true, value -> ValueTask<'T>(unbox<'T> value)
                | _ -> ValueTask<'T>(Unchecked.defaultof<'T>)

            member _.ReadOrInitStateAsync<'T>(key, initialStateFactory: Func<'T>, scopeName, _cancellationToken) =
                let stateKey = makeStateKey scopeName key

                match states.TryGetValue stateKey with
                | true, value -> ValueTask<'T>(unbox<'T> value)
                | _ ->
                    let value = initialStateFactory.Invoke()
                    states[stateKey] <- box value
                    ValueTask<'T>(value)

            member this.ReadStateAsync<'T>(key, cancellationToken) =
                (this :> IWorkflowContext).ReadStateAsync<'T>(key, null, cancellationToken)

            member this.ReadOrInitStateAsync<'T>(key, initialStateFactory: Func<'T>, cancellationToken) =
                (this :> IWorkflowContext).ReadOrInitStateAsync<'T>(key, initialStateFactory, null, cancellationToken)

            member _.ReadStateKeysAsync(scopeName, _cancellationToken) =
                let prefix =
                    if String.IsNullOrEmpty scopeName then
                        String.Empty
                    else
                        scopeName + "|"

                let keys = HashSet<string>(StringComparer.Ordinal)

                for stateKey in states.Keys do
                    if prefix.Length = 0 then
                        keys.Add stateKey |> ignore
                    elif stateKey.StartsWith(prefix, StringComparison.Ordinal) then
                        keys.Add(stateKey.Substring(prefix.Length)) |> ignore

                ValueTask<HashSet<string>>(keys)

            member _.QueueStateUpdateAsync<'T>
                (key: string, value: 'T, scopeName: string, _cancellationToken: CancellationToken)
                : ValueTask =
                states[makeStateKey scopeName key] <- box value
                updatedKeys.Add(makeStateKey scopeName key)
                ValueTask()

            member this.QueueStateUpdateAsync<'T>
                (key: string, value: 'T, cancellationToken: CancellationToken)
                : ValueTask =
                (this :> IWorkflowContext).QueueStateUpdateAsync<'T>(key, value, null, cancellationToken)

            member _.QueueClearScopeAsync(scopeName, _cancellationToken) =
                let keysToRemove =
                    states.Keys
                    |> Seq.filter (fun stateKey -> stateKey.StartsWith(scopeName + "|", StringComparison.Ordinal))
                    |> Seq.toArray

                for key in keysToRemove do
                    states.Remove key |> ignore

                clearedScopes.Add scopeName
                ValueTask()

            member _.QueueClearScopeAsync(_cancellationToken) =
                states.Clear()
                clearedScopes.Add "<all>"
                ValueTask()

    let internal rethrowInner (ex: Exception) =
        ExceptionDispatchInfo.Capture(ex).Throw()
        Unchecked.defaultof<'T>

    let internal getWorkflowBindingFactoryType () =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = "BindingFactory")

    let internal getWorkflowBindingMethod name genericArity =
        getWorkflowBindingFactoryType ()
        |> fun factoryType ->
            factoryType.GetMethods(
                Reflection.BindingFlags.Static
                ||| Reflection.BindingFlags.Public
                ||| Reflection.BindingFlags.NonPublic
            )
        |> Array.find (fun methodInfo ->
            methodInfo.Name = name
            && methodInfo.IsGenericMethodDefinition
            && methodInfo.GetGenericArguments().Length = genericArity)

    let internal invokeWorkflowBinding name (genericTypes: Type[]) (args: obj[]) =
        let methodInfo = getWorkflowBindingMethod name genericTypes.Length
        methodInfo.MakeGenericMethod(genericTypes).Invoke(null, args) :?> ExecutorBinding

    let internal executeBinding<'TInput> (binding: ExecutorBinding) (input: 'TInput) (context: IWorkflowContext) =
        let executor = binding.FactoryAsync.Invoke(binding.Id).AsTask().Result

        let executeMethod =
            typeof<Executor>
                .GetMethods(
                    Reflection.BindingFlags.Instance
                    ||| Reflection.BindingFlags.Public
                    ||| Reflection.BindingFlags.NonPublic
                )
            |> Array.find (fun methodInfo ->
                methodInfo.Name = "ExecuteCoreAsync" && methodInfo.GetParameters().Length = 4)

        try
            let valueTask =
                executeMethod.Invoke(
                    executor,
                    [| box input
                       box (TypeId(typeof<'TInput>))
                       box context
                       box CancellationToken.None |]
                )
                :?> ValueTask<obj>

            valueTask.AsTask().GetAwaiter().GetResult()
        with :? Reflection.TargetInvocationException as ex when not (isNull ex.InnerException) ->
            rethrowInner ex.InnerException

    let internal getPrivateGenericType name arity =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = $"{name}`{arity}")

    let internal instanceFlags =
        Reflection.BindingFlags.Instance
        ||| Reflection.BindingFlags.Public
        ||| Reflection.BindingFlags.NonPublic

    let internal setInstanceProperty (target: obj) name value =
        target.GetType().GetProperty(name, instanceFlags).SetValue(target, value)

    let internal createParallelWaveEnvelope<'Input, 'Output>
        input
        (completed: WorkflowGraph.ParallelBranchResult<'Output>[])
        =
        let envelopeType =
            getPrivateGenericType "ParallelWaveEnvelope" 2
            |> fun valueType -> valueType.MakeGenericType([| typeof<'Input>; typeof<'Output> |])

        let envelope = Activator.CreateInstance(envelopeType, true)
        setInstanceProperty envelope "Input" (box input)
        setInstanceProperty envelope "Completed" (box completed)
        envelope

    let internal createParallelWaveItem<'Input, 'Output> isSeed branchIndex branchValue envelope =
        let itemType =
            getPrivateGenericType "ParallelWaveItem" 2
            |> fun valueType -> valueType.MakeGenericType([| typeof<'Input>; typeof<'Output> |])

        let item = Activator.CreateInstance(itemType, true)
        setInstanceProperty item "IsSeed" (box isSeed)
        setInstanceProperty item "BranchIndex" (box branchIndex)
        setInstanceProperty item "BranchValue" (box branchValue)
        setInstanceProperty item "Envelope" envelope
        item

    let internal createWorkflowRuntime () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"workflow\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver> :> IWorkflowRuntime

    let internal collectUntil<'T> (predicate: RunEvent<'T> -> bool) (run: WorkflowRun<'T>) =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            let events = ResizeArray<RunEvent<'T>>()
            let enumerator = run.Events.GetAsyncEnumerator(cts.Token)

            try
                let mutable doneReading = false

                while not doneReading do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if not moved then
                        doneReading <- true
                    else
                        let event = enumerator.Current
                        events.Add event
                        doneReading <- predicate event
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return events |> Seq.toArray
        }

    [<Fact>]
    let ``structured output agent rejects missing or non-json response formats`` () =
        let repairClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let inner =
            FixedResponseAgent(fun () -> AgentResponse(ChatMessage(ChatRole.Assistant, "plain text")))

        let getChatOptionsMethod =
            typeof<MafStructuredOutputAgent>
                .GetMethod("GetChatOptions", Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)

        let invokeGetChatOptions (agent: MafStructuredOutputAgent) (options: AgentRunOptions) =
            try
                getChatOptionsMethod.Invoke(agent, [| box options |]) |> ignore
            with :? Reflection.TargetInvocationException as ex when not (isNull ex.InnerException) ->
                rethrowInner ex.InnerException

        let missingResponseFormat =
            MafStructuredOutputAgent(inner, repairClient, MafStructuredOutputAgentOptions())

        let missingEx =
            Assert.Throws<InvalidOperationException>(fun () -> invokeGetChatOptions missingResponseFormat null)

        Assert.Contains("none was specified", missingEx.Message)

        let unsupportedOptions = MafStructuredOutputAgentOptions()
        unsupportedOptions.ChatOptions <- ChatOptions(ResponseFormat = ChatResponseFormat.Text)

        let unsupported = MafStructuredOutputAgent(inner, repairClient, unsupportedOptions)

        let unsupportedEx =
            Assert.Throws<NotSupportedException>(fun () -> invokeGetChatOptions unsupported null)

        Assert.Contains("ChatResponseFormatText", unsupportedEx.Message)

    [<Fact>]
    let ``tool resolution honors tag filters allows distinct major versions and rejects ambiguous model names`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let toolOne =
            createResolvedTool
                (createTestTool "tool.one" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "one"))))
                [| "alpha"; "shared"; "versioned" |]

        let toolOneV2 =
            createResolvedTool
                (createToolDefinition<TestInput, TestOutput>
                    "tool.one"
                    "2.0.0"
                    "tool.one v2 description"
                    ApprovalMode.Never
                    ValueNone
                    (Contract<TestInput>.Create(CircuitJson.createOptions (), Seq.empty))
                    (Contract<TestOutput>.Create(CircuitJson.createOptions (), Seq.empty))
                    (fun _ _ -> Task.FromResult(TestOutput(Text = "two"))))
                [| "versioned" |]

        let ambiguousTool =
            createResolvedTool
                (createTestTool "tool-one" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "ambiguous"))))
                [| "shared" |]

        let runtime =
            createMafRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| StaticToolResolver([| toolOne; toolOneV2; ambiguousTool |]) :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly

        let taggedAgent =
            AgentDefinition.Create(
                "agent.tags",
                "1.0.0",
                "Agent Tags",
                "Use tagged tools.",
                ValueNone,
                [| "alpha" |],
                Seq.empty,
                Seq.empty
            )

        let taggedContext =
            runtime.CreateRunContext(RunId.New(), taggedAgent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync taggedContext.RunId taggedContext taggedAgent CancellationToken.None
            |> _.Result
        with
        | Error failure -> Assert.True(false, failure.Message)
        | Ok(tools, _skills) ->
            let resolved = Assert.Single tools
            Assert.Equal("tool.one", resolved.Tool.Name.Value)

        let missingTagAgent =
            AgentDefinition.Create(
                "agent.tags.missing",
                "1.0.0",
                "Agent Missing Tag",
                "Use tagged tools.",
                ValueNone,
                [| "missing" |],
                Seq.empty,
                Seq.empty
            )

        let missingContext =
            runtime.CreateRunContext(RunId.New(), missingTagAgent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync missingContext.RunId missingContext missingTagAgent CancellationToken.None
            |> _.Result
        with
        | Ok _ -> Assert.True(false, "Expected missing tag failure.")
        | Error failure ->
            Assert.Equal(CircuitFailureCode.Tool, failure.Code)
            Assert.Contains("requested tag 'missing'", failure.Message)

        let versionedAgent =
            AgentDefinition.Create(
                "agent.tags.versioned",
                "1.0.0",
                "Agent Versioned Tags",
                "Use tagged tools.",
                ValueNone,
                [| "versioned" |],
                Seq.empty,
                Seq.empty
            )

        let versionedContext =
            runtime.CreateRunContext(RunId.New(), versionedAgent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync
                versionedContext.RunId
                versionedContext
                versionedAgent
                CancellationToken.None
            |> _.Result
        with
        | Error failure -> Assert.True(false, failure.Message)
        | Ok(tools, _skills) ->
            Assert.Equal<string>(
                [| "tool.one@1.0.0"; "tool.one@2.0.0" |],
                tools
                |> Seq.map (fun tool -> $"{tool.Tool.Name.Value}@{tool.Tool.Version}")
                |> Seq.toArray
            )

            Assert.Equal<string>([| "tool_one_v1"; "tool_one_v2" |], tools |> Seq.map _.ModelName |> Seq.toArray)

        let ambiguousTagAgent =
            AgentDefinition.Create(
                "agent.tags.ambiguous",
                "1.0.0",
                "Agent Ambiguous Tag",
                "Use tagged tools.",
                ValueNone,
                [| "shared" |],
                Seq.empty,
                Seq.empty
            )

        let ambiguousContext =
            runtime.CreateRunContext(RunId.New(), ambiguousTagAgent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync
                ambiguousContext.RunId
                ambiguousContext
                ambiguousTagAgent
                CancellationToken.None
            |> _.Result
        with
        | Ok _ -> Assert.True(false, "Expected ambiguous tag failure.")
        | Error failure ->
            Assert.Equal(CircuitFailureCode.Tool, failure.Code)
            Assert.Contains("Multiple tools were resolved for requested tag 'shared'", failure.Message)
            Assert.Contains("tool_one_v1", failure.Message)

    [<Fact>]
    let ``tool resolution deduplicates exact matches across requested tags`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let sharedTool =
            createResolvedTool
                (createTestTool "tool.one" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "one"))))
                [| "alpha"; "shared" |]

        let otherTool =
            createResolvedTool
                (createTestTool "tool.two" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "two"))))
                [| "beta" |]

        let runtime =
            createMafRuntimeWith
                (fun options ->
                    options.ToolResolvers <- [| StaticToolResolver([| sharedTool; otherTool |]) :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly

        let agent =
            AgentDefinition.Create(
                "agent.tags.dedup",
                "1.0.0",
                "Agent Dedup",
                "Use tagged tools.",
                ValueNone,
                [| "alpha"; "shared"; "beta" |],
                Seq.empty,
                Seq.empty
            )

        let context = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync context.RunId context agent CancellationToken.None
            |> _.Result
        with
        | Error failure -> Assert.True(false, failure.Message)
        | Ok(tools, _skills) ->
            Assert.Equal(2, tools.Count)

            Assert.Equal<string>(
                [| "tool.one@1.0.0"; "tool.two@1.0.0" |],
                tools
                |> Seq.map (fun tool -> $"{tool.Tool.Name.Value}@{tool.Tool.Version}")
                |> Seq.toArray
            )

    [<Fact>]
    let ``tool resolution rejects colliding model-facing names from distinct requested tags`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let dottedTool =
            createResolvedTool
                (createTestTool "tool.one" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "one"))))
                [| "alpha" |]

        let dashedTool =
            createResolvedTool
                (createTestTool "tool-one" ApprovalMode.Never ValueNone (fun _ _ ->
                    Task.FromResult(TestOutput(Text = "two"))))
                [| "beta" |]

        let runtime =
            createMafRuntimeWith
                (fun options ->
                    options.ToolResolvers <- [| StaticToolResolver([| dottedTool; dashedTool |]) :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly

        let agent =
            AgentDefinition.Create(
                "agent.tags.collision",
                "1.0.0",
                "Agent Collision",
                "Use tagged tools.",
                ValueNone,
                [| "alpha"; "beta" |],
                Seq.empty,
                Seq.empty
            )

        let context = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        match
            runtime.ResolveCapabilitiesAsync context.RunId context agent CancellationToken.None
            |> _.Result
        with
        | Ok _ -> Assert.True(false, "Expected tag collision failure.")
        | Error failure ->
            Assert.Equal(CircuitFailureCode.Tool, failure.Code)
            Assert.Contains("model-facing identity 'tool_one_v1'", failure.Message)
            Assert.Contains("tool.one@1.0.0", failure.Message)
            Assert.Contains("tool-one@1.0.0", failure.Message)

    [<Fact>]
    let ``skill resolution reports missing and duplicate requested skills`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let skillReference =
            SkillReference.Create(
                "skill.coverage",
                "1.0.0",
                "Coverage skill",
                SkillSource.CreateInline("Use the coverage skill.")
            )

        let resolved = ResolvedSkill.Create(skillReference)

        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly

        let missingRuntimeOptions = MafRuntimeOptions()
        missingRuntimeOptions.SkillResolvers <- [| StaticSkillResolver([| resolved |]) :> ISkillResolver |]
        let missingRuntime = MafRuntime(primary, missingRuntimeOptions)

        let missingAgent =
            AgentDefinition.Create(
                "agent.skills.missing",
                "1.0.0",
                "Agent Missing Skill",
                "Use the skill.",
                ValueNone,
                Seq.empty,
                [| SkillReference.Create("skill.other", "1.0.0", "Other skill", SkillSource.CreateInline("Other")) |],
                Seq.empty
            )

        let missingContext =
            missingRuntime.CreateRunContext(RunId.New(), missingAgent, signature, runOptions)

        let missingEx =
            Assert.Throws<AggregateException>(fun () ->
                MafAgentFactory.resolveSkillsAsync
                    missingRuntimeOptions
                    missingContext
                    missingAgent
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("No skill was resolved", missingEx.InnerException.Message)

        let duplicateRuntimeOptions = MafRuntimeOptions()

        duplicateRuntimeOptions.SkillResolvers <-
            [| StaticSkillResolver([| resolved |]) :> ISkillResolver
               StaticSkillResolver([| resolved |]) :> ISkillResolver |]

        let duplicateRuntime = MafRuntime(primary, duplicateRuntimeOptions)

        let duplicateAgent =
            AgentDefinition.Create(
                "agent.skills.duplicate",
                "1.0.0",
                "Agent Duplicate Skill",
                "Use the skill.",
                ValueNone,
                Seq.empty,
                [| skillReference |],
                Seq.empty
            )

        let duplicateContext =
            duplicateRuntime.CreateRunContext(RunId.New(), duplicateAgent, signature, runOptions)

        let duplicateEx =
            Assert.Throws<AggregateException>(fun () ->
                MafAgentFactory.resolveSkillsAsync
                    duplicateRuntimeOptions
                    duplicateContext
                    duplicateAgent
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("Duplicate skill identity", duplicateEx.InnerException.Message)

    [<Fact>]
    let ``workflow bindings cover choice loop and aggregate edge cases`` () =
        let runId = RunId.New()
        let definitionId = DefinitionId.Create("workflow.binding")
        let definitionVersion = SemanticVersion.Parse("1.0.0")
        let context = WorkflowContextStub() :> IWorkflowContext

        let choiceSelector =
            invokeWorkflowBinding
                "ChoiceSelector"
                [| typeof<int> |]
                [| box runId
                   box "choice.selector"
                   box (
                       WorkflowGraph.SelectorHandler<int>(fun value -> if value > 5 then "high" else "low")
                       :> WorkflowGraph.ISelectorHandler
                   ) |]

        let selected =
            executeBinding choiceSelector 9 context :?> WorkflowGraph.BranchSelection<int>

        Assert.Equal("high", selected.Key)
        Assert.Equal(9, selected.Value)

        let selectorFailure =
            invokeWorkflowBinding
                "ChoiceSelector"
                [| typeof<int> |]
                [| box runId
                   box "choice.fail"
                   box (
                       WorkflowGraph.SelectorHandler<int>(fun _ -> raise (InvalidOperationException("selector boom")))
                       :> WorkflowGraph.ISelectorHandler
                   ) |]

        let selectorEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                executeBinding selectorFailure 1 context |> ignore)

        Assert.Contains("choice.fail", selectorEx.Failure.Message)

        let choiceCase =
            invokeWorkflowBinding "ChoiceCaseAdapter" [| typeof<int> |] [| box "choice.case"; box "low" |]

        let choiceDefault =
            invokeWorkflowBinding "ChoiceDefaultAdapter" [| typeof<int> |] [| box "choice.default" |]

        Assert.Equal(
            4,
            executeBinding choiceCase ({ Key = "low"; Value = 4 }: WorkflowGraph.BranchSelection<int>) context :?> int
        )

        Assert.Equal(
            6,
            executeBinding choiceDefault ({ Key = "other"; Value = 6 }: WorkflowGraph.BranchSelection<int>) context
            :?> int
        )

        let wrongCaseEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                executeBinding choiceCase ({ Key = "high"; Value = 7 }: WorkflowGraph.BranchSelection<int>) context
                |> ignore)

        Assert.Contains("unexpected value", wrongCaseEx.Message)

        let loopGuard =
            invokeWorkflowBinding
                "LoopGuard"
                [| typeof<int> |]
                [| box runId
                   box "loop.guard"
                   box "binding-loop"
                   box 2
                   box (
                       WorkflowGraph.LoopConditionHandler<int>(fun value -> value < 10)
                       :> WorkflowGraph.ILoopConditionHandler
                   ) |]

        let firstLoop =
            executeBinding loopGuard 1 context :?> WorkflowGraph.LoopDecision<int>

        let secondLoop =
            executeBinding loopGuard 1 context :?> WorkflowGraph.LoopDecision<int>

        let exitLoop =
            executeBinding loopGuard 1 context :?> WorkflowGraph.LoopDecision<int>

        Assert.True(firstLoop.Continue)
        Assert.True(secondLoop.Continue)
        Assert.False(exitLoop.Continue)

        let recordedScopes = (context :?> WorkflowContextStub).ClearedScopes
        Assert.Contains("loop:binding-loop", recordedScopes)

        let aggregate =
            invokeWorkflowBinding
                "ParallelAggregate"
                [| typeof<int>; typeof<string> |]
                [| box runId
                   box "parallel.aggregate"
                   box "binding-parallel"
                   box 2
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun values _ ->
                           Task.FromResult(String.concat "," (values |> List.map string)))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        let pending =
            executeBinding aggregate ({ BranchIndex = 0; Value = 3 }: WorkflowGraph.ParallelBranchResult<int>) context
            :?> WorkflowGraph.ParallelAggregateDispatch<string>

        let completed =
            executeBinding aggregate ({ BranchIndex = 1; Value = 4 }: WorkflowGraph.ParallelBranchResult<int>) context
            :?> WorkflowGraph.ParallelAggregateDispatch<string>

        Assert.False(pending.IsComplete)
        Assert.True(completed.IsComplete)
        Assert.Equal("3,4", completed.Value)

        let duplicateEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                executeBinding
                    aggregate
                    ({ BranchIndex = 1; Value = 5 }: WorkflowGraph.ParallelBranchResult<int>)
                    context
                |> ignore)

        Assert.Contains("after completion", duplicateEx.Failure.Message)

        let invalidAggregate =
            invokeWorkflowBinding
                "ParallelAggregate"
                [| typeof<int>; typeof<string> |]
                [| box runId
                   box "parallel.aggregate.invalid"
                   box "binding-parallel-invalid"
                   box 1
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun values _ ->
                           Task.FromResult(String.concat "," (values |> List.map string)))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        let invalidEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                executeBinding
                    invalidAggregate
                    ({ BranchIndex = 9; Value = 9 }: WorkflowGraph.ParallelBranchResult<int>)
                    (WorkflowContextStub() :> IWorkflowContext)
                |> ignore)

        Assert.Contains("expected 0..0", invalidEx.Failure.Message)

    [<Fact>]
    let ``workflow wave bindings cover start ready pending and final collector failures`` () =
        let runId = RunId.New()
        let branchContext = WorkflowContextStub()

        let branchStart =
            invokeWorkflowBinding
                "ParallelWaveBranchStart"
                [| typeof<int>; typeof<int> |]
                [| box runId; box "wave.start"; box "parallel-wave"; box 0; box 1 |]

        let envelope =
            createParallelWaveEnvelope<int, int> 12 Array.empty<WorkflowGraph.ParallelBranchResult<int>>

        let firstDispatch =
            executeBinding branchStart envelope (branchContext :> IWorkflowContext)

        let secondDispatch =
            executeBinding branchStart envelope (branchContext :> IWorkflowContext)

        let readyAdapter =
            invokeWorkflowBinding "ParallelWaveBranchReadyAdapter" [| typeof<int> |] [| box "wave.ready" |]

        let pendingAdapter =
            invokeWorkflowBinding "ParallelWaveBranchPending" [| typeof<int> |] [| box "wave.pending" |]

        let firstInput =
            executeBinding readyAdapter firstDispatch (branchContext :> IWorkflowContext) :?> int

        Assert.Equal(12, firstInput)
        Assert.Null(executeBinding pendingAdapter secondDispatch (branchContext :> IWorkflowContext))

        let inactiveEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                executeBinding readyAdapter secondDispatch (branchContext :> IWorkflowContext)
                |> ignore)

        Assert.Contains("inactive dispatch", inactiveEx.Message)

        let collectorContext = WorkflowContextStub()

        let collector =
            invokeWorkflowBinding
                "ParallelWaveCollector"
                [| typeof<int>; typeof<int> |]
                [| box runId; box "wave.collect"; box "parallel-wave"; box 0; box [| 0; 1 |] |]

        let seed = createParallelWaveItem<int, int> true -1 0 envelope
        let duplicateSeed = createParallelWaveItem<int, int> true -1 0 envelope
        let branchZero = createParallelWaveItem<int, int> false 0 10 null
        let branchOne = createParallelWaveItem<int, int> false 1 20 null

        let pendingSeed =
            executeBinding collector seed (collectorContext :> IWorkflowContext)

        Assert.NotNull(pendingSeed)

        let duplicateSeedEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                executeBinding collector duplicateSeed (collectorContext :> IWorkflowContext)
                |> ignore)

        Assert.Contains("duplicate seed envelope", duplicateSeedEx.Failure.Message)

        let pendingBranch =
            executeBinding collector branchZero (collectorContext :> IWorkflowContext)

        Assert.NotNull(pendingBranch)

        let readyDispatch =
            executeBinding collector branchOne (collectorContext :> IWorkflowContext)

        let readyEnvelope =
            executeBinding
                (invokeWorkflowBinding
                    "ParallelWaveReadyAdapter"
                    [| typeof<int>; typeof<int> |]
                    [| box "wave.ready.aggregate" |])
                readyDispatch
                (collectorContext :> IWorkflowContext)

        Assert.NotNull(readyEnvelope)

        let finalCollectorContext = WorkflowContextStub()

        let finalCollector =
            invokeWorkflowBinding
                "ParallelFinalCollector"
                [| typeof<int>; typeof<int>; typeof<string> |]
                [| box runId
                   box "wave.final"
                   box "parallel-wave-final"
                   box 0
                   box [| 1 |]
                   box 3
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun values _ ->
                           Task.FromResult(String.concat "," (values |> List.map string)))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        let malformedCompleted =
            createParallelWaveEnvelope<int, int>
                5
                [| ({ BranchIndex = 0; Value = 1 }: WorkflowGraph.ParallelBranchResult<int>)
                   ({ BranchIndex = 1; Value = 2 }: WorkflowGraph.ParallelBranchResult<int>) |]

        let malformedItem = createParallelWaveItem<int, int> true -1 0 malformedCompleted

        let malformedEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                executeBinding finalCollector malformedItem (finalCollectorContext :> IWorkflowContext)
                |> ignore)

        Assert.Contains("wave.final", malformedEx.Failure.Message)
        Assert.Contains("malformed", malformedEx.Failure.Exception.Value.Message)

    [<Fact>]
    let ``workflow run returns approval required and resume rejects mismatched ids`` () =
        let runtime = createWorkflowRuntime ()

        let requestStep =
            Workflow.request "approval.required" (fun value ->
                ApprovalPrompt.Create($"approve:{value}", "Need approval"))

        let definition = Workflow.define "workflow.approval.required" "1.0.0" requestStep

        let result =
            Workflow.run runtime definition 3 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.ApprovalRequired, result.Result.Failure.Code)

        let run =
            Workflow.start runtime definition 4 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) run
            |> _.Result

        let approval =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask().Result

        let renamed = Workflow.define "workflow.approval.required.other" "1.0.0" requestStep

        let mismatch =
            Workflow.resume runtime renamed checkpoint CancellationToken.None |> _.Result

        let mismatchEvents =
            collectUntil
                (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
                mismatch
            |> _.Result

        let mismatchFailure =
            Assert
                .Single(
                    mismatchEvents
                    |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)
                )
                .Failure.Value

        Assert.Equal(CircuitFailureCode.CheckpointMismatch, mismatchFailure.Code)

        Assert.Equal(
            "The supplied workflow checkpoint does not match this workflow definition ID.",
            mismatchFailure.Message
        )

        run
            .RespondAsync(ApprovalResponse(approval.Approval.Value.RequestId, true, null), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()

module AdditionalRuntimeCoverageTests =
    open Helpers

    [<Fact>]
    let ``streaming wrapper helpers select wrapped schemas and deserialize wrapped payloads`` () =
        let moduleType =
            typeof<MafRuntime>.Assembly.GetType("Circuit.MicrosoftAgentFramework.MafStreaming", true)

        let getMethod name genericArity =
            moduleType.GetMethods(Reflection.BindingFlags.Static ||| Reflection.BindingFlags.NonPublic)
            |> Array.find (fun methodInfo ->
                methodInfo.Name = name
                && methodInfo.IsGenericMethodDefinition
                && methodInfo.GetGenericArguments().Length = genericArity)

        let invokeCreateWrapped (outputType: Type) (signature: obj) =
            let methodInfo = getMethod "createWrappedResponseFormat" 2

            methodInfo.MakeGenericMethod([| typeof<TestInput>; outputType |]).Invoke(null, [| signature |])
            :?> (ChatResponseFormat * bool)

        let invokeDeserialize (outputType: Type) (signature: obj) wrapped text =
            let methodInfo = getMethod "deserializeOutput" 2

            methodInfo
                .MakeGenericMethod([| typeof<TestInput>; outputType |])
                .Invoke(null, [| signature; box wrapped; box text |])

        let objectSignature = createSignature<TestOutput> ()
        let primitiveSignature = createSignature<int> ()

        let _, objectWrapped = invokeCreateWrapped typeof<TestOutput> (box objectSignature)
        let _, primitiveWrapped = invokeCreateWrapped typeof<int> (box primitiveSignature)

        Assert.False(objectWrapped)
        Assert.False(primitiveWrapped)

        let objectValue =
            invokeDeserialize typeof<TestOutput> (box objectSignature) false "{\"text\":\"ok\"}"
            |> Assert.IsType<TestOutput>

        let primitiveValue =
            invokeDeserialize typeof<int> (box primitiveSignature) true "{\"data\":7}"
            |> Assert.IsType<int>

        Assert.Equal("ok", objectValue.Text)
        Assert.Equal(7, primitiveValue)

    [<Fact>]
    let ``streaming input validation fails before provider invocation`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime = createRuntime client None Array.empty<Circuit.IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Validate input before streaming.",
                createSignature<TestOutput> (),
                TestInput(Token = null),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)
        Assert.Equal(CircuitFailureCode.Validation, events[1].Failure.Value.Code)
        Assert.Equal(0, client.StreamingCalls)

    [<Fact>]
    let ``non streaming repair preflight fails before provider or tool resolution when repair client is missing`` () =
        let primary =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let mutable toolResolverCalls = 0

        let runtime =
            createRuntimeWith
                (fun options ->
                    options.ToolResolvers <-
                        [| DelegateToolResolver(
                               Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>
                                   (fun _ _ ->
                                       toolResolverCalls <- toolResolverCalls + 1
                                       ValueTask<IReadOnlyList<ResolvedTool>>(Array.empty<ResolvedTool>))
                           )
                           :> IToolResolver |])
                primary
                None
                Array.empty<Circuit.IRunObserver>

        let result =
            (runtime.RunAsync(
                createAgent "Repair required.",
                createSignature<TestOutput> (),
                TestInput(Token = "missing-repair-client"),
                createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                CancellationToken.None
            ))
                .Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.StructuredOutputUnsupported, result.Result.Failure.Code)
        Assert.Equal(0, primary.ResponseCalls)
        Assert.Equal(1, toolResolverCalls)

    [<Fact>]
    let ``deserialize session failures surface session agent initialization errors`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Persist sessions."

        let providerSession =
            buildSessionAgent runtime agent
            |> fun sessionAgent -> sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let session = MafSessionContracts.createCircuitSession agent null providerSession

        let serialized =
            runtime.SerializeSessionAsyncCore(agent, session, CancellationToken.None).AsTask().Result

        let failingRuntime =
            createMafRuntimeWith
                (fun options ->
                    options.SkillResolvers <-
                        [| { new Circuit.Core.ISkillResolver with
                               member _.ResolveAsync(_context, _cancellationToken) =
                                   raise (InvalidOperationException("skill fail")) } |])
                client
                None
                Array.empty<Circuit.IRunObserver>

        let failure =
            Assert.Throws<InvalidOperationException>(fun () ->
                failingRuntime
                    .DeserializeSessionAsyncCore(agent, serialized, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
                |> ignore)

        Assert.Contains("Skill resolution failed", failure.Message)

    [<Fact>]
    let ``loop guard and final collector wrap thrown workflow failures`` () =
        let runId = RunId.New()

        let loopContext =
            BindingAndResolverCoverageTests.WorkflowContextStub() :> Microsoft.Agents.AI.Workflows.IWorkflowContext

        let loopGuard =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "LoopGuard"
                [| typeof<int> |]
                [| box runId
                   box "loop.throw"
                   box "loop-throw"
                   box 1
                   box (
                       WorkflowGraph.LoopConditionHandler<int>(fun _ ->
                           raise (InvalidOperationException("predicate boom")))
                       :> WorkflowGraph.ILoopConditionHandler
                   ) |]

        let loopEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding loopGuard 1 loopContext |> ignore)

        Assert.Contains("loop.throw", loopEx.Failure.Message)

        let finalCollectorContext = BindingAndResolverCoverageTests.WorkflowContextStub()

        let finalCollector =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelFinalCollector"
                [| typeof<int>; typeof<int>; typeof<string> |]
                [| box runId
                   box "wave.aggregate.throw"
                   box "parallel-wave-throw"
                   box 0
                   box [| 0 |]
                   box 1
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun _ _ ->
                           raise (InvalidOperationException("aggregate boom")))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        let seedEnvelope =
            BindingAndResolverCoverageTests.createParallelWaveEnvelope<int, int>
                5
                Array.empty<WorkflowGraph.ParallelBranchResult<int>>

        let seed =
            BindingAndResolverCoverageTests.createParallelWaveItem<int, int> true -1 0 seedEnvelope

        let branch =
            BindingAndResolverCoverageTests.createParallelWaveItem<int, int> false 0 10 null

        BindingAndResolverCoverageTests.executeBinding
            finalCollector
            seed
            (finalCollectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        let aggregateEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    finalCollector
                    branch
                    (finalCollectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("wave.aggregate.throw", aggregateEx.Failure.Message)

    [<Fact>]
    let ``approval by policy auto approves or requests approval based on policy outcome`` () =
        let mutable executions = 0
        let mutable recordedPolicyNames = ResizeArray<string>()

        let tool =
            createResolvedTool
                (createTestTool "tool.policy.exec" ApprovalMode.ByPolicy (ValueSome "allow") (fun _ input ->
                    executions <- executions + 1
                    Task.FromResult(TestOutput(Text = input.Token))))
                Seq.empty

        let createRuntimeWithPolicy policy =
            createMafRuntimeWith
                (fun options ->
                    options.ToolApprovalPolicy <- ValueSome policy
                    options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                (new FakeChatClient(
                    (fun messages _options _ct ->
                        match tryGetFunctionResult messages with
                        | Some resultContent ->
                            let output = Assert.IsType<TestOutput>(resultContent.Result)
                            Task.FromResult(jsonResponse $"{{\"text\":\"{output.Text}\"}}")
                        | None ->
                            let arguments = Dictionary<string, obj>()
                            arguments["token"] <- "approved"
                            Task.FromResult(functionCallResponse "policy-call" "tool_policy_exec_v1" arguments)),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                Array.empty<Circuit.IRunObserver>

        let allowingRuntime =
            createRuntimeWithPolicy (
                { new IToolApprovalPolicy with
                    member _.IsApprovedAsync(policyName, _context) =
                        recordedPolicyNames.Add policyName
                        ValueTask<bool>(true) }
            )

        let allowed =
            ((allowingRuntime :> ICircuitRuntime)
                .RunAsync(
                    createAgent "Use the policy tool.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "policy-allow"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                ))
                .Result

        Assert.True(allowed.Result.IsSuccess)
        Assert.Equal("approved", allowed.Result.Value.Text)
        Assert.Equal(1, executions)
        Assert.Equal<string[]>([| "allow" |], recordedPolicyNames |> Seq.toArray)

        let denyingRuntime =
            createRuntimeWithPolicy (
                { new IToolApprovalPolicy with
                    member _.IsApprovedAsync(_policyName, _context) = ValueTask<bool>(false) }
            )

        let sessionAgent =
            buildSessionAgent denyingRuntime (createAgent "Use the policy tool.")

        let session =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let approvalResponse =
            sessionAgent
                .RunAsync([ ChatMessage(ChatRole.User, "run the policy tool") ], session, null, CancellationToken.None)
                .Result

        Assert.True(tryGetApprovalRequest approvalResponse.Messages |> Option.isSome)
        Assert.Equal(1, executions)

module InternalHelperCoverageTests =
    open Helpers
    open Circuit.MicrosoftAgentFramework.MafWorkflows

    let private createWaveEnvelope input (completed: WorkflowGraph.ParallelBranchResult<int>[]) =
        let envelope = ParallelWaveEnvelope<int, int>()
        envelope.Input <- input
        envelope.Completed <- completed
        envelope

    let private createWaveItem isSeed branchIndex branchValue envelope =
        let item = ParallelWaveItem<int, int>()
        item.IsSeed <- isSeed
        item.BranchIndex <- branchIndex
        item.BranchValue <- branchValue
        item.Envelope <- envelope
        item

    [<Fact>]
    let ``streaming helper maps every content kind and terminal suppression state`` () =
        let jsonOptions = CircuitJson.createOptions ()
        let arguments = Dictionary<string, obj>()
        arguments["token"] <- "value"

        let cases =
            [| FunctionCallContent("call-1", "tool.call", arguments) :> AIContent
               FunctionCallContent(" ", "tool.call", arguments) :> AIContent
               FunctionResultContent("call-2", "done") :> AIContent
               FunctionResultContent("", "done") :> AIContent
               ToolApprovalRequestContent("approval-1", FunctionCallContent("call-3", "tool.approval", arguments))
               :> AIContent
               ToolApprovalRequestContent("approval-2", UnknownToolCallContent("call-4")) :> AIContent
               TextContent("ignored") :> AIContent |]

        let mapped =
            cases |> Array.map (MafStreaming.StreamingMappedEvent.tryMapContent jsonOptions)

        match mapped[0] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolStarted(ValueSome "call-1")) -> ()
        | other -> Assert.True(false, $"unexpected mapped function call: {other}")

        match mapped[1] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolStarted ValueNone) -> ()
        | other -> Assert.True(false, $"unexpected blank mapped function call: {other}")

        match mapped[2] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolCompleted(ValueSome "call-2")) -> ()
        | other -> Assert.True(false, $"unexpected mapped function result: {other}")

        match mapped[3] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolCompleted ValueNone) -> ()
        | other -> Assert.True(false, $"unexpected blank mapped function result: {other}")

        match mapped[4] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(ValueSome "call-3", approval)) ->
            Assert.Equal("tool.approval", approval.ToolName)
            Assert.Equal("{\"token\":\"value\"}", approval.ArgumentsJson.Value)
        | other -> Assert.True(false, $"unexpected mapped approval: {other}")

        match mapped[5] with
        | ValueSome(MafStreaming.StreamingMappedEvent.ApprovalRequested(ValueSome "call-4", approval)) ->
            Assert.Equal("unknown-tool-call", approval.ToolName)
            Assert.True(approval.ArgumentsJson.IsNone)
        | other -> Assert.True(false, $"unexpected unknown mapped approval: {other}")

        Assert.True(mapped[6] |> ValueOption.isNone)
        Assert.True(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.RunCompleted)
        Assert.True(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.RunFailed)
        Assert.False(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.ToolStarted)
        Assert.True(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal true RunEventKind.RunCompleted)
        Assert.False(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal false RunEventKind.RunCompleted)
        Assert.False(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal true RunEventKind.OutputDelta)

    [<Fact>]
    let ``streaming and runtime decode helpers classify success validation cancellation decode and provider failures``
        ()
        =
        let signature = createSignature<TestOutput> ()
        use cts = new CancellationTokenSource()
        cts.Cancel()

        match
            MafStreaming.decodeFinalOutput (RunId.New()) CancellationToken.None signature false "{\"text\":\"ok\"}"
        with
        | Ok output -> Assert.Equal("ok", output.Text)
        | Error failure -> Assert.True(false, failure.Message)

        match MafStreaming.decodeFinalOutput (RunId.New()) CancellationToken.None signature false "{\"text\":null}" with
        | Error failure -> Assert.Equal(CircuitFailureCode.Validation, failure.Code)
        | Ok _ -> Assert.True(false, "expected validation failure")

        match MafStreaming.decodeFinalOutput (RunId.New()) CancellationToken.None signature false "{not-json" with
        | Error failure -> Assert.Equal(CircuitFailureCode.Decode, failure.Code)
        | Ok _ -> Assert.True(false, "expected decode failure")

        match
            MafRuntimeInternals.decodeResponseResult (RunId.New()) CancellationToken.None signature (fun () ->
                TestOutput(Text = "runtime-ok"))
        with
        | Ok output -> Assert.Equal("runtime-ok", output.Text)
        | Error failure -> Assert.True(false, failure.Message)

        match
            MafRuntimeInternals.decodeResponseResult (RunId.New()) CancellationToken.None signature (fun () ->
                TestOutput(Text = null))
        with
        | Error failure -> Assert.Equal(CircuitFailureCode.Validation, failure.Code)
        | Ok _ -> Assert.True(false, "expected runtime validation failure")

        match
            MafRuntimeInternals.decodeResponseResult (RunId.New()) cts.Token signature (fun () ->
                raise (OperationCanceledException(cts.Token)))
        with
        | Error failure -> Assert.Equal(CircuitFailureCode.Cancelled, failure.Code)
        | Ok _ -> Assert.True(false, "expected runtime cancellation failure")

        match
            MafRuntimeInternals.decodeResponseResult (RunId.New()) CancellationToken.None signature (fun () ->
                raise (JsonException("bad json")))
        with
        | Error failure -> Assert.Equal(CircuitFailureCode.Decode, failure.Code)
        | Ok _ -> Assert.True(false, "expected runtime decode failure")

        match
            MafRuntimeInternals.classifyProviderExecutionFailure
                (RunId.New())
                CancellationToken.None
                (InvalidOperationException("provider boom"))
        with
        | failure -> Assert.Equal(CircuitFailureCode.Provider, failure.Code)

    [<Fact>]
    let ``workflow helper state transitions are directly testable and exhaustive`` () =
        let loopState = LoopState()

        let predicate =
            WorkflowGraph.LoopConditionHandler<int>(fun value -> value < 3) :> WorkflowGraph.ILoopConditionHandler

        Assert.True(LoopState.advance 2 predicate 1 loopState)
        Assert.Equal(1, loopState.Iteration)
        Assert.True(LoopState.advance 2 predicate 1 loopState)
        Assert.Equal(2, loopState.Iteration)
        Assert.False(LoopState.advance 2 predicate 1 loopState)
        Assert.Equal(2, loopState.Iteration)
        Assert.False(LoopState.advance 5 predicate 5 (LoopState()))

        let branchState = ParallelWaveBranchStartState()
        let branchEnvelope = createWaveEnvelope 42 Array.empty
        let firstDispatch = ParallelWaveBranchStartState.dispatch branchEnvelope branchState

        let secondDispatch =
            ParallelWaveBranchStartState.dispatch branchEnvelope branchState

        Assert.True(firstDispatch.IsActive)
        Assert.Equal(42, firstDispatch.Input)
        Assert.False(secondDispatch.IsActive)

        let ordered =
            createWaveEnvelope
                9
                [| ({ BranchIndex = 0; Value = 10 }: WorkflowGraph.ParallelBranchResult<int>)
                   ({ BranchIndex = 1; Value = 11 }: WorkflowGraph.ParallelBranchResult<int>) |]

        let validated = WorkflowBindingInternals.validateCompletedEnvelope 2 ordered
        Assert.Equal<int[]>([| 10; 11 |], validated |> Array.map _.Value)

        let malformed =
            createWaveEnvelope
                9
                [| ({ BranchIndex = 1; Value = 10 }: WorkflowGraph.ParallelBranchResult<int>)
                   ({ BranchIndex = 0; Value = 11 }: WorkflowGraph.ParallelBranchResult<int>) |]

        let malformedEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                WorkflowBindingInternals.validateCompletedEnvelope 2 malformed |> ignore)

        Assert.Contains("malformed", malformedEx.Message)

    [<Fact>]
    let ``parallel wave collector direct state machine covers branch before seed null item and ready merge`` () =
        let state = ParallelWaveCollectorState.create<int, int> 2
        let branchZero = createWaveItem false 1 10 null
        let branchOne = createWaveItem false 2 20 null

        let seedEnvelope =
            createWaveEnvelope 7 [| ({ BranchIndex = 0; Value = 5 }: WorkflowGraph.ParallelBranchResult<int>) |]

        match ParallelWaveCollectorState.capture [| 1; 2 |] branchZero state with
        | ParallelWaveCapture.Pending -> ()
        | other -> Assert.True(false, $"expected pending branch-first capture, got {other}")

        match ParallelWaveCollectorState.capture [| 1; 2 |] branchOne state with
        | ParallelWaveCapture.Pending -> ()
        | other -> Assert.True(false, $"expected pending second branch-first capture, got {other}")

        match ParallelWaveCollectorState.capture [| 1; 2 |] (createWaveItem true -1 0 seedEnvelope) state with
        | ParallelWaveCapture.Ready envelope ->
            Assert.Equal(7, envelope.Input)
            Assert.Equal<int[]>([| 0; 1; 2 |], envelope.Completed |> Array.map _.BranchIndex)
            Assert.Equal<int[]>([| 5; 10; 20 |], envelope.Completed |> Array.map _.Value)
        | other -> Assert.True(false, $"expected ready capture, got {other}")

        let nullItemEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                ParallelWaveCollectorState.capture [| 0; 1 |] null (ParallelWaveCollectorState.create<int, int> 2)
                |> ignore)

        Assert.Contains("malformed", nullItemEx.Message)

    [<Fact>]
    let ``runtime and streaming preflight cancellation plus session mismatch short circuit provider calls`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Short-circuit invalid runs."
        let signature = createSignature<TestOutput> ()

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let cancelledRun =
            (runtime :> ICircuitRuntime)
                .RunAsync(
                    agent,
                    signature,
                    TestInput(Token = "cancelled"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    cancelled.Token
                )
                .Result

        Assert.False(cancelledRun.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, cancelledRun.Result.Failure.Code)

        let cancelledStream =
            (runtime :> ICircuitRuntime)
                .RunStreamingAsync(
                    agent,
                    signature,
                    TestInput(Token = "cancelled"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    cancelled.Token
                )
            |> collectStreamEvents

        Assert.Equal<RunEventKind[]>(
            [| RunEventKind.RunStarted; RunEventKind.RunFailed |],
            cancelledStream |> Array.map _.Kind
        )

        Assert.Equal(CircuitFailureCode.Cancelled, cancelledStream[1].Failure.Value.Code)

        let sessionAgent = buildSessionAgent runtime agent

        let providerSession =
            sessionAgent.CreateSessionAsync(CancellationToken.None).AsTask().Result

        let circuitSession =
            MafSessionContracts.createCircuitSession agent null providerSession

        let mismatchOptions =
            createRunOptionsWith
                (Some circuitSession)
                (Some "tenant-a")
                (Some "user-a")
                StructuredOutputPolicy.NativeOnly

        let mismatchResult =
            (runtime :> ICircuitRuntime)
                .RunAsync(agent, signature, TestInput(Token = "mismatch"), mismatchOptions, CancellationToken.None)
                .Result

        Assert.False(mismatchResult.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.CheckpointMismatch, mismatchResult.Result.Failure.Code)
        Assert.Equal(0, client.ResponseCalls)
        Assert.Equal(0, client.StreamingCalls)

    [<Fact>]
    let ``streaming public event pump emits mixed tool completion approval and ignored content cases`` () =
        let streamingClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                (fun _messages _options _ct ->
                    let arguments = Dictionary<string, obj>()
                    arguments["token"] <- "value"

                    let contentUpdate =
                        ChatResponseUpdate(
                            ChatRole.Assistant,
                            ResizeArray<AIContent>(
                                [ FunctionCallContent("call-1", "tool.call", arguments) :> AIContent
                                  FunctionCallContent("", "tool.call", arguments) :> AIContent
                                  FunctionResultContent("call-2", "ok") :> AIContent
                                  FunctionResultContent("", "ok") :> AIContent
                                  ToolApprovalRequestContent("approval-1", UnknownToolCallContent("")) :> AIContent
                                  TextContent("ignored") :> AIContent ]
                            )
                            :> IList<AIContent>
                        )

                    ArrayAsyncEnumerable(
                        [| contentUpdate
                           ChatResponseUpdate(ChatRole.Assistant, "{\"text\":\"done\"}") |]
                    )
                    :> IAsyncEnumerable<ChatResponseUpdate>)
            )

        let runtime = createRuntime streamingClient None Array.empty<Circuit.IRunObserver>

        let events =
            runtime.RunStreamingAsync(
                createAgent "Translate all stream event kinds.",
                createSignature<TestOutput> (),
                TestInput(Token = "mixed-events"),
                createRunOptions None StructuredOutputPolicy.NativeOnly,
                CancellationToken.None
            )
            |> collectStreamEvents

        Assert.Equal(RunEventKind.RunStarted, events[0].Kind)

        Assert.Contains(
            events,
            fun event -> event.Kind = RunEventKind.OutputDelta && event.TextDelta.Value.Contains("done")
        )

        Assert.Contains(
            events,
            fun event -> event.Kind = RunEventKind.ToolStarted && event.OperationId = ValueSome "call-1"
        )

        Assert.Contains(events, fun event -> event.Kind = RunEventKind.ToolStarted && event.OperationId = ValueNone)

        Assert.Contains(
            events,
            fun event ->
                event.Kind = RunEventKind.ToolCompleted
                && event.OperationId = ValueSome "call-2"
        )

        Assert.Contains(events, fun event -> event.Kind = RunEventKind.ToolCompleted && event.OperationId = ValueNone)

        let approvalEvents =
            events
            |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)

        Assert.NotEmpty(approvalEvents)

        Assert.Contains(
            approvalEvents,
            fun event -> event.OperationId.IsNone && event.Approval.Value.ToolName = "unknown-tool-call"
        )

        Assert.Contains(events[events.Length - 1].Kind, [| RunEventKind.RunCompleted; RunEventKind.RunFailed |])

    [<Fact>]
    let ``workflow binding callbacks cover duplicate invalid already completed and thrown aggregate paths`` () =
        let runId = RunId.New()

        let duplicateAggregateContext =
            BindingAndResolverCoverageTests.WorkflowContextStub()

        let aggregate =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelAggregate"
                [| typeof<int>; typeof<string> |]
                [| box runId
                   box "parallel.aggregate.duplicate"
                   box "binding-parallel-duplicate"
                   box 2
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun values _ ->
                           Task.FromResult(String.concat "," (values |> List.map string)))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        BindingAndResolverCoverageTests.executeBinding
            aggregate
            ({ BranchIndex = 0; Value = 3 }: WorkflowGraph.ParallelBranchResult<int>)
            (duplicateAggregateContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        let duplicateBranchEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    aggregate
                    ({ BranchIndex = 0; Value = 4 }: WorkflowGraph.ParallelBranchResult<int>)
                    (duplicateAggregateContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("duplicate branch envelope 0", duplicateBranchEx.Failure.Message)

        let aggregateThrowContext = BindingAndResolverCoverageTests.WorkflowContextStub()

        let throwingAggregate =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelAggregate"
                [| typeof<int>; typeof<string> |]
                [| box runId
                   box "parallel.aggregate.throw"
                   box "binding-parallel-throw"
                   box 2
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun _ _ ->
                           raise (InvalidOperationException("aggregate explode")))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        BindingAndResolverCoverageTests.executeBinding
            throwingAggregate
            ({ BranchIndex = 0; Value = 3 }: WorkflowGraph.ParallelBranchResult<int>)
            (aggregateThrowContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        let aggregateThrowEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    throwingAggregate
                    ({ BranchIndex = 1; Value = 4 }: WorkflowGraph.ParallelBranchResult<int>)
                    (aggregateThrowContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("parallel.aggregate.throw", aggregateThrowEx.Failure.Message)

        let collectorContext = BindingAndResolverCoverageTests.WorkflowContextStub()

        let collector =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelWaveCollector"
                [| typeof<int>; typeof<int> |]
                [| box runId
                   box "wave.collect.extra"
                   box "parallel-wave-extra"
                   box 0
                   box [| 0; 1 |] |]

        let seedEnvelope =
            createWaveEnvelope 5 Array.empty<WorkflowGraph.ParallelBranchResult<int>>

        let seed = createWaveItem true -1 0 seedEnvelope
        let branchZero = createWaveItem false 0 10 null
        let branchOne = createWaveItem false 1 20 null

        let invalidCollectorEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    collector
                    (createWaveItem false 9 99 null)
                    (collectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("received branch envelope 9", invalidCollectorEx.Failure.Message)

        BindingAndResolverCoverageTests.executeBinding
            collector
            seed
            (collectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        BindingAndResolverCoverageTests.executeBinding
            collector
            branchZero
            (collectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        let duplicateCollectorEx =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    collector
                    branchZero
                    (collectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("duplicate branch envelope 0", duplicateCollectorEx.Failure.Message)

        BindingAndResolverCoverageTests.executeBinding
            collector
            branchOne
            (collectorContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
        |> ignore

        let finalContext = BindingAndResolverCoverageTests.WorkflowContextStub()

        let finalCollector =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelFinalCollector"
                [| typeof<int>; typeof<int>; typeof<string> |]
                [| box runId
                   box "wave.final.extra"
                   box "parallel-wave-final-extra"
                   box 0
                   box [| 0; 1 |]
                   box 2
                   box (
                       WorkflowGraph.AggregateHandler<int, string>(fun values _ ->
                           Task.FromResult(String.concat "," (values |> List.map string)))
                       :> WorkflowGraph.IAggregateHandler
                   ) |]

        let finalDuplicateSeed =
            Assert.Throws<Circuit.MicrosoftAgentFramework.MafWorkflows.WorkflowStepFailureException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    finalCollector
                    (createWaveItem true -1 0 seedEnvelope)
                    (finalContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore

                BindingAndResolverCoverageTests.executeBinding
                    finalCollector
                    (createWaveItem true -1 0 seedEnvelope)
                    (finalContext :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("duplicate seed envelope", finalDuplicateSeed.Failure.Message)
