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

module private AdapterCoverageHelpers =
    let private runOptionsCtor =
        typeof<RunOptions>
            .GetConstructor(
                Reflection.BindingFlags.Instance
                ||| Reflection.BindingFlags.Public
                ||| Reflection.BindingFlags.NonPublic,
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
