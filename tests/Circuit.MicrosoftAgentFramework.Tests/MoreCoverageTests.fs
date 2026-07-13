namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Agents.AI
open Microsoft.Extensions.AI
open Microsoft.Extensions.DependencyInjection
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace
open Microsoft.Extensions.Logging.Abstractions
open Xunit

module MoreRuntimeConstructionCoverageTests =
    open Helpers
    open AdapterCoverageHelpers

    [<Fact>]
    let ``maf runtime and di helpers guard null dependencies and preserve default facade options`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        Assert.Throws<ArgumentNullException>(fun () -> MafRuntime(null, MafRuntimeOptions()) |> ignore)
        |> ignore

        let nullJsonOptions = MafRuntimeOptions()
        nullJsonOptions.JsonSerializerOptions <- null

        Assert.Throws<ArgumentNullException>(fun () -> MafRuntime(client, nullJsonOptions) |> ignore)
        |> ignore

        let nullToolResolvers = MafRuntimeOptions()
        nullToolResolvers.ToolResolvers <- null

        Assert.Throws<ArgumentNullException>(fun () -> MafRuntime(client, nullToolResolvers) |> ignore)
        |> ignore

        let nullSkillResolvers = MafRuntimeOptions()
        nullSkillResolvers.SkillResolvers <- null

        Assert.Throws<ArgumentNullException>(fun () -> MafRuntime(client, nullSkillResolvers) |> ignore)
        |> ignore

        let nullObservers = MafRuntimeOptions()
        nullObservers.Observers <- null

        Assert.Throws<ArgumentNullException>(fun () -> MafRuntime(client, nullObservers) |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            ServiceCollectionExtensions.AddCircuit(null, Action<Circuit.CircuitOptions>(fun _ -> ()))
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            ServiceCollectionExtensions.AddCircuit(ServiceCollection(), null) |> ignore)
        |> ignore

        let nullServices: IServiceCollection = null

        Assert.Throws<ArgumentNullException>(fun () ->
            nullServices.AddMafRuntime(client, MafRuntimeOptions()) |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            ServiceCollection().AddMafRuntime(null, MafRuntimeOptions()) |> ignore)
        |> ignore

        let facadeDefaults =
            CSharpFacadeAdapters.createRuntimeOptions
                null
                (Array.empty<Circuit.IToolResolver> :> IReadOnlyList<Circuit.IToolResolver>)
                (Array.empty<Circuit.ISkillResolver> :> IReadOnlyList<Circuit.ISkillResolver>)
                (Array.empty<Circuit.IRunObserver> :> IReadOnlyList<Circuit.IRunObserver>)

        Assert.True(facadeDefaults.JsonSerializerOptions.IsReadOnly)
        Assert.True(facadeDefaults.DefaultModelId.IsNone)
        Assert.True(facadeDefaults.SecondaryStructuredOutputClient.IsNone)
        Assert.Equal(0, facadeDefaults.ToolResolvers.Count)
        Assert.Equal(0, facadeDefaults.SkillResolvers.Count)
        Assert.Equal(0, facadeDefaults.Observers.Count)

    [<Fact>]
    let ``structured output agent covers override fallback and constructor guard branches`` () =
        let repairClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let inner =
            FixedResponseAgent(fun () -> AgentResponse(ChatMessage(ChatRole.Assistant, "plain text")))

        let getChatOptionsMethod =
            typeof<MafStructuredOutputAgent>
                .GetMethod("GetChatOptions", BindingFlags.Instance ||| BindingFlags.NonPublic)

        let getChatMessagesMethod =
            typeof<MafStructuredOutputAgent>
                .GetMethod("GetChatMessages", BindingFlags.Instance ||| BindingFlags.NonPublic)

        let invokePrivate (methodInfo: MethodInfo) (target: obj) (args: obj[]) =
            try
                methodInfo.Invoke(target, args)
            with :? TargetInvocationException as ex when not (isNull ex.InnerException) ->
                raise ex.InnerException

        Assert.Throws<ArgumentNullException>(fun () -> MafStructuredOutputAgent(inner, null, null) |> ignore)
        |> ignore

        let explicitOnly = MafStructuredOutputAgent(inner, repairClient, null)

        let explicitRunOptions =
            AgentRunOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let explicitChatOptions =
            invokePrivate getChatOptionsMethod explicitOnly [| box explicitRunOptions |] :?> ChatOptions

        Assert.NotNull(explicitChatOptions.ResponseFormat)

        let explicitMessages =
            invokePrivate getChatMessagesMethod explicitOnly [| box "plain text" |] :?> IReadOnlyList<ChatMessage>

        Assert.Single(explicitMessages) |> ignore
        Assert.Equal("plain text", explicitMessages[0].Text)

        let fallbackOptions = MafStructuredOutputAgentOptions()
        fallbackOptions.ChatOptions <- ChatOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let fallbackAgent = MafStructuredOutputAgent(inner, repairClient, fallbackOptions)
        let emptyRunOptions = AgentRunOptions()

        let fallbackChatOptions =
            invokePrivate getChatOptionsMethod fallbackAgent [| box emptyRunOptions |] :?> ChatOptions

        Assert.NotSame(fallbackOptions.ChatOptions, fallbackChatOptions)
        Assert.NotNull(fallbackChatOptions.ResponseFormat)

        let nullFallbackChatOptions =
            invokePrivate getChatOptionsMethod fallbackAgent [| null |] :?> ChatOptions

        Assert.NotNull(nullFallbackChatOptions.ResponseFormat)

        let emptyFallbackOptions = MafStructuredOutputAgentOptions()
        emptyFallbackOptions.ChatOptions <- ChatOptions()

        let emptyFallbackAgent =
            MafStructuredOutputAgent(inner, repairClient, emptyFallbackOptions)

        Assert.Throws<InvalidOperationException>(fun () ->
            invokePrivate getChatOptionsMethod emptyFallbackAgent [| null |] |> ignore)
        |> ignore

        let unsupportedRunOptions =
            AgentRunOptions(ResponseFormat = ChatResponseFormat.Text)

        let unsupported =
            Assert.Throws<NotSupportedException>(fun () ->
                invokePrivate getChatOptionsMethod explicitOnly [| box unsupportedRunOptions |]
                |> ignore)

        Assert.Contains("ChatResponseFormatText", unsupported.Message)

    [<Fact>]
    let ``structured output builder resolves explicit and service provider chat clients`` () =
        let repairClient =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let inner =
            FixedResponseAgent(fun () -> AgentResponse(ChatMessage(ChatRole.Assistant, "plain text"))) :> AIAgent

        Assert.Throws<ArgumentNullException>(fun () ->
            MafStructuredOutputAIAgentBuilderExtensions.UseStructuredOutput(null, repairClient)
            |> ignore)
        |> ignore

        let explicitBuilder = inner.AsBuilder()
        explicitBuilder.UseStructuredOutput(repairClient) |> ignore
        let explicitAgent = explicitBuilder.Build(null)
        Assert.IsType<MafStructuredOutputAgent>(explicitAgent) |> ignore

        let providerBuilder = inner.AsBuilder()

        providerBuilder.UseStructuredOutput(null, Func<_>(fun () -> MafStructuredOutputAgentOptions()))
        |> ignore

        use services =
            ServiceCollection().AddSingleton<IChatClient>(repairClient).BuildServiceProvider()

        let providerAgent = providerBuilder.Build(services)
        Assert.IsType<MafStructuredOutputAgent>(providerAgent) |> ignore

        let missingBuilder = inner.AsBuilder()
        missingBuilder.UseStructuredOutput(null, null) |> ignore

        Assert.Throws<InvalidOperationException>(fun () -> missingBuilder.Build(null) |> ignore)
        |> ignore

    [<Fact>]
    let ``tool function operation id extraction covers call id context and function call fallbacks`` () =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "unused")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Use tools."
        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let runContext = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        let tool =
            createResolvedTool
                (createTestTool "tool.operation" ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(TestOutput(Text = input.Token))))
                Seq.empty

        let toolFunction =
            CircuitResolvedToolFunction(tool, "tool_operation_v1", CircuitJson.createOptions (), runContext)

        let methodInfo =
            typeof<CircuitResolvedToolFunction>
                .GetMethod("TryGetOperationId", BindingFlags.Instance ||| BindingFlags.NonPublic)

        let contextProperty =
            typeof<AIFunctionArguments>
                .GetProperty("Context", BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic)

        let invoke arguments =
            methodInfo.Invoke(toolFunction, [| box arguments |]) :?> string voption

        let fromCallId = AIFunctionArguments()
        let callIdContext = Dictionary<obj, obj>()
        callIdContext[box "callId"] <- box "call-1"
        contextProperty.SetValue(fromCallId, callIdContext)

        let fromFunctionCall = AIFunctionArguments()
        let functionCallContext = Dictionary<obj, obj>()
        functionCallContext[box "callId"] <- box " "
        functionCallContext[box 1] <- box (FunctionCallContent("call-2", "tool.operation", null))
        contextProperty.SetValue(fromFunctionCall, functionCallContext)

        let noneFound = AIFunctionArguments()
        let noneContext = Dictionary<obj, obj>()
        noneContext[box 1] <- box "value"
        contextProperty.SetValue(noneFound, noneContext)

        Assert.Equal(ValueNone, invoke null)
        Assert.Equal(ValueSome "call-1", invoke fromCallId)
        Assert.Equal(ValueSome "call-2", invoke fromFunctionCall)
        Assert.Equal(ValueNone, invoke noneFound)

module MoreApprovalResponseCoverageTests =
    let private approvalSnapshotType =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = "PendingApprovalSnapshot")

    let private approvalStoreType =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name.Contains("PendingApprovalStore"))

    let private approvalResponsesType =
        typeof<MafRuntime>.Assembly.GetTypes()
        |> Array.find (fun valueType -> valueType.Name = "MafApprovalResponses")

    let private approvalStateBagKey = "circuit.pending-tool-approval-requests"

    let private invokeApprovalResponses name args =
        approvalResponsesType
            .GetMethod(name, BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            .Invoke(null, args)

    let private createSnapshot requestId callId toolName argumentsJson =
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
        let snapshot = Activator.CreateInstance(approvalSnapshotType, true)
        approvalSnapshotType.GetProperty("RequestId", flags).SetValue(snapshot, requestId)
        approvalSnapshotType.GetProperty("CallId", flags).SetValue(snapshot, callId)
        approvalSnapshotType.GetProperty("ToolName", flags).SetValue(snapshot, toolName)
        approvalSnapshotType.GetProperty("ArgumentsJson", flags).SetValue(snapshot, argumentsJson)
        snapshot

    let private createApprovalResponse requestId approved toolCall =
        let response = ToolApprovalResponseContent(requestId, approved, toolCall)
        ChatMessage(ChatRole.User, ResizeArray<AIContent>([ response :> AIContent ]) :> IList<AIContent>)

    [<Fact>]
    let ``approval response filtering cleans persisted snapshots and drops invalid or replayed responses`` () =
        let jsonOptions = CircuitJson.createOptions ()

        let validSession = DummyAgentSession()
        let capturedRequestArguments = Dictionary<string, obj>()

        let capturedRequest =
            ToolApprovalRequestContent(
                "req-valid",
                FunctionCallContent("call-valid", "tool.valid", capturedRequestArguments)
            )

        let capturedResponse =
            AgentResponse(
                ChatMessage(
                    ChatRole.Assistant,
                    ResizeArray<AIContent>([ capturedRequest :> AIContent ]) :> IList<AIContent>
                )
            )

        invokeApprovalResponses
            "captureOutboundApprovalRequests"
            [| box capturedResponse; box validSession; box jsonOptions |]
        |> ignore

        let validArguments = Dictionary<string, obj>()

        let validResponse =
            createApprovalResponse "req-valid" true (FunctionCallContent("call-valid", "tool.valid", validArguments))

        let filtered, dropped =
            invokeApprovalResponses
                "filterInboundApprovalResponses"
                [| box [| validResponse |]; box validSession; box jsonOptions |]
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.False(dropped)
        let preservedMessage = filtered[0]
        Assert.Equal(1, filtered.Count)

        let preservedContent =
            preservedMessage.Contents[0] |> Assert.IsType<ToolApprovalResponseContent>

        let preservedToolCall =
            Assert.IsType<FunctionCallContent>(preservedContent.ToolCall)

        Assert.Equal("req-valid", preservedContent.RequestId)
        Assert.Equal("call-valid", preservedToolCall.CallId)
        Assert.Equal("tool.valid", preservedToolCall.Name)
        Assert.NotNull(preservedToolCall.Arguments)
        Assert.Empty(preservedToolCall.Arguments)

        let replayed, replayDropped =
            invokeApprovalResponses
                "filterInboundApprovalResponses"
                [| box [| validResponse |]; box validSession; box jsonOptions |]
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.True(replayDropped)
        Assert.Empty(replayed)

        let invalidSession = DummyAgentSession()

        invalidSession.StateBag.SetValue(
            approvalStateBagKey,
            [| null
               createSnapshot " " "ignored-call" "ignored-tool" null
               createSnapshot "req-other" "call-other" "tool.other" "{\"token\":\"expected\"}"
               createSnapshot "req-other" "call-other" "tool.other" "{\"token\":\"expected\"}" |],
            jsonOptions
        )

        let wrongArgs = Dictionary<string, obj>()
        wrongArgs["token"] <- "actual"

        let wrongArgumentsResponse =
            createApprovalResponse "req-other" true (FunctionCallContent("call-other", "tool.other", wrongArgs))

        let unknownResponse =
            createApprovalResponse "req-missing" true (FunctionCallContent("call-missing", "tool.missing", null))

        let nonFunctionResponse =
            createApprovalResponse "req-other" true (UnknownToolCallContent("call-other"))

        let filteredInvalid, droppedInvalid =
            invokeApprovalResponses
                "filterInboundApprovalResponses"
                [| box [| wrongArgumentsResponse; unknownResponse; nonFunctionResponse |]
                   box invalidSession
                   box jsonOptions |]
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.True(droppedInvalid)
        Assert.Empty(filteredInvalid)

    [<Fact>]
    let ``approval request capture ignores unsupported snapshots and null branches`` () =
        let session = DummyAgentSession()
        let jsonOptions = CircuitJson.createOptions ()

        let loop = System.Collections.Generic.List<obj>()
        loop.Add(box loop)

        let serializableRequest =
            ToolApprovalRequestContent("req-null-args", FunctionCallContent("call-null-args", "tool.keep", null))

        let unserializableArguments = Dictionary<string, obj>()
        unserializableArguments["loop"] <- box loop

        let unserializableRequest =
            ToolApprovalRequestContent(
                "req-bad-args",
                FunctionCallContent("call-bad-args", "tool.drop", unserializableArguments)
            )

        let unsupportedRequest =
            ToolApprovalRequestContent("req-unknown", UnknownToolCallContent("call-unknown"))

        let response =
            AgentResponse(
                ChatMessage(
                    ChatRole.Assistant,
                    ResizeArray<AIContent>(
                        [ serializableRequest :> AIContent
                          unserializableRequest :> AIContent
                          unsupportedRequest :> AIContent ]
                    )
                    :> IList<AIContent>
                )
            )

        invokeApprovalResponses "captureOutboundApprovalRequests" [| box response; box session; box jsonOptions |]
        |> ignore

        invokeApprovalResponses "captureOutboundApprovalRequests" [| null; box session; box jsonOptions |]
        |> ignore

        invokeApprovalResponses "captureOutboundApprovalRequests" [| box response; null; box jsonOptions |]
        |> ignore

        let matching =
            createApprovalResponse "req-null-args" true (FunctionCallContent("call-null-args", "tool.keep", null))

        let filtered, dropped =
            invokeApprovalResponses
                "filterInboundApprovalResponses"
                [| box [| matching |]; box session; box jsonOptions |]
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.False(dropped)
        Assert.Equal(1, filtered.Count)

        let bypassed, bypassedDropped =
            invokeApprovalResponses "filterInboundApprovalResponses" [| box [| matching |]; null; box jsonOptions |]
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.False(bypassedDropped)
        Assert.Equal(1, bypassed.Count)

    [<Fact>]
    let ``approval helper reflection covers direct mismatch and no-op store branches`` () =
        let jsonOptions = CircuitJson.createOptions ()

        let flags =
            BindingFlags.Instance
            ||| BindingFlags.Static
            ||| BindingFlags.Public
            ||| BindingFlags.NonPublic

        let matchesMethod = approvalResponsesType.GetMethod("matchesStoredSnapshot", flags)
        let ensureLoadedMethod = approvalStoreType.GetMethod("EnsureLoaded", flags)
        let filterMethod = approvalStoreType.GetMethod("Filter", flags)
        let captureMethod = approvalStoreType.GetMethod("Capture", flags)

        let snapshot = createSnapshot "req-1" "call-1" "tool.one" "{}"

        let mismatchedRequest =
            ToolApprovalResponseContent(
                "req-2",
                true,
                FunctionCallContent("call-1", "tool.one", Dictionary<string, obj>())
            )

        let mismatchedCall =
            ToolApprovalResponseContent(
                "req-1",
                true,
                FunctionCallContent("call-2", "tool.one", Dictionary<string, obj>())
            )

        let mismatchedName =
            ToolApprovalResponseContent(
                "req-1",
                true,
                FunctionCallContent("call-1", "tool.two", Dictionary<string, obj>())
            )

        let nonFunction =
            ToolApprovalResponseContent("req-1", true, UnknownToolCallContent("call-1"))

        Assert.False(matchesMethod.Invoke(null, [| snapshot; box mismatchedRequest; box jsonOptions |]) :?> bool)
        Assert.False(matchesMethod.Invoke(null, [| snapshot; box mismatchedCall; box jsonOptions |]) :?> bool)
        Assert.False(matchesMethod.Invoke(null, [| snapshot; box mismatchedName; box jsonOptions |]) :?> bool)

        Assert.Throws<TargetInvocationException>(fun () ->
            matchesMethod.Invoke(null, [| null; box mismatchedName; box jsonOptions |])
            |> ignore)
        |> ignore

        Assert.False(matchesMethod.Invoke(null, [| snapshot; box nonFunction; box jsonOptions |]) :?> bool)

        let freshSession = DummyAgentSession()
        let freshStore = Activator.CreateInstance(approvalStoreType, [| box freshSession |])
        ensureLoadedMethod.Invoke(freshStore, [| box jsonOptions |]) |> ignore
        ensureLoadedMethod.Invoke(freshStore, [| box jsonOptions |]) |> ignore

        let preloadedSession = DummyAgentSession()

        preloadedSession.StateBag.SetValue(
            approvalStateBagKey,
            [| null
               createSnapshot " " "ignored" "ignored" null
               createSnapshot "req-loaded" "call-loaded" "tool.loaded" "{}"
               createSnapshot "req-loaded" "call-loaded" "tool.loaded" "{}" |],
            jsonOptions
        )

        let loadedStore =
            Activator.CreateInstance(approvalStoreType, [| box preloadedSession |])

        ensureLoadedMethod.Invoke(loadedStore, [| box jsonOptions |]) |> ignore

        let nullContentsMessage = ChatMessage(ChatRole.User, "plain")

        let contentOnlyMessage =
            ChatMessage(ChatRole.User, ResizeArray<AIContent>([ TextContent("kept") :> AIContent ]) :> IList<AIContent>)

        let filtered, dropped =
            filterMethod.Invoke(freshStore, [| box [| nullContentsMessage; contentOnlyMessage |]; box jsonOptions |])
            :?> (IReadOnlyList<ChatMessage> * bool)

        Assert.False(dropped)
        Assert.Equal(2, filtered.Count)

        let ignoredResponse =
            AgentResponse(
                ChatMessage(
                    ChatRole.Assistant,
                    ResizeArray<AIContent>([ TextContent("ignored") :> AIContent ]) :> IList<AIContent>
                )
            )

        captureMethod.Invoke(freshStore, [| box ignoredResponse; box jsonOptions |])
        |> ignore

module MoreSkillAndSessionCoverageTests =
    open Helpers

    let private emptyStringMap () =
        Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, string>

    let private emptySet () =
        HashSet<string>(StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>

    [<Fact>]
    let ``skill snapshot frontmatter parses defaults overrides and metadata branches`` () =
        let snapshotWithoutFrontmatter =
            new MafSkillAdapter.MafFileSkillSnapshot(
                Path.Combine(Path.GetTempPath(), "default-skill"),
                "Body only",
                emptyStringMap (),
                emptyStringMap (),
                emptySet (),
                emptySet (),
                "fingerprint-a"
            )

        Assert.Throws<ArgumentException>(fun () -> snapshotWithoutFrontmatter.ReadFrontmatter() |> ignore)
        |> ignore

        let snapshotWithFrontmatter =
            new MafSkillAdapter.MafFileSkillSnapshot(
                Path.Combine(Path.GetTempPath(), "custom-skill"),
                String.concat
                    "\n"
                    [ "---"
                      "name: tuned-skill"
                      "description: Tuned description"
                      "owner: platform"
                      "note-without-colon"
                      " : ignored"
                      "---"
                      "Body" ],
                emptyStringMap (),
                emptyStringMap (),
                emptySet (),
                emptySet (),
                "fingerprint-b"
            )

        let customFrontmatter = snapshotWithFrontmatter.ReadFrontmatter()
        Assert.Equal("tuned-skill", customFrontmatter.Name)
        Assert.Equal("Tuned description", customFrontmatter.Description)
        Assert.NotNull(customFrontmatter.Metadata)
        Assert.Equal("platform", string customFrontmatter.Metadata["owner"])

    [<Fact>]
    let ``skill adapter validates custom payloads and snapshots chat client sessions by conversation id`` () =
        let reference =
            SkillReference.Create(
                "skill.custom",
                "1.0.0",
                "Custom skill",
                SkillSource.CreateCustom(),
                seq [ KeyValuePair("team", "platform") ]
            )

        let validCustomSkill =
            AgentInlineSkill(AgentSkillFrontmatter("custom-skill", "desc", null), "Body", null, null)

        let validResolved =
            ResolvedSkill.Create(
                reference,
                seq [ KeyValuePair(MafSkillAdapterProperties.AgentSkill, box validCustomSkill) ]
            )

        let invalidResolved = ResolvedSkill.Create(reference)

        let runContext =
            RunContext(
                RunId.New(),
                createAgent "Use skills.",
                DefinitionId.Create("agent.skills"),
                SemanticVersion.Parse("1.0.0"),
                RunOptions.Default
            )

        let options = MafRuntimeOptions()

        let attachment =
            MafSkillAdapter.createAttachment options runContext ([| validResolved |] :> IReadOnlyList<ResolvedSkill>)

        Assert.True(attachment.IsSome)

        let invalid =
            Assert.Throws<InvalidOperationException>(fun () ->
                MafSkillAdapter.createAttachment
                    options
                    runContext
                    ([| invalidResolved |] :> IReadOnlyList<ResolvedSkill>)
                |> ignore)

        Assert.Contains("missing the", invalid.Message)

        let agent = createAgent "Persist state."

        let wrapped =
            MafSessionContracts.createCircuitSession agent null (DummyAgentSession() :> AgentSession)

        Assert.False(String.IsNullOrWhiteSpace wrapped.Id)

    [<Fact>]
    let ``session fingerprints include rich skill and tool metadata branches`` () =
        let jsonElement = JsonDocument.Parse("{\"kind\":\"element\"}").RootElement.Clone()
        use jsonDocument = JsonDocument.Parse("{\"kind\":\"document\"}")
        let archiveBytes = Text.Encoding.UTF8.GetBytes("archive")
        let cyclic = ResizeArray<obj>()
        cyclic.Add(cyclic :> obj)

        let script =
            SkillScriptDescriptor.Create(
                "normalize-contact",
                "Normalize a contact.",
                seq [ KeyValuePair("lang", "python"); KeyValuePair("runtime", "script") ]
            )

        let skillSource =
            SkillSource.CreateInline(
                "Use the inline skill.",
                [| SkillResource.Create("guide.txt", box "guide", "Guide")
                   SkillResource.Create("archive.bin", box archiveBytes, "Archive")
                   SkillResource.Create("config.json", box jsonElement, "Config")
                   SkillResource.Create("doc.json", box jsonDocument, "Doc")
                   SkillResource.Create("fallback.txt", box cyclic, "Fallback") |],
                [| script |]
            )

        let skill =
            SkillReference.Create(
                "skill.rich",
                "1.0.0",
                "Rich skill",
                skillSource,
                seq [ KeyValuePair("owner", "platform"); KeyValuePair("tier", "gold") ]
            )

        let agent =
            AgentDefinition.Create(
                "agent.rich",
                "1.0.0",
                "Agent Rich",
                "Use rich skills.",
                ValueSome "model-rich",
                [| "tag-a"; "tag-b" |],
                [| skill |],
                seq [ KeyValuePair("agent", "rich") ]
            )

        let resolvedTool =
            createResolvedTool
                (createTestTool "tool.rich" ApprovalMode.ByPolicy (ValueSome "auto") (fun _ input ->
                    Task.FromResult(TestOutput(Text = input.Token))))
                [| "tool-tag" |]

        let runtime =
            createMafRuntimeWith
                ignore
                (new FakeChatClient(
                    (fun _ _ _ -> Task.FromResult(jsonResponse "unused")),
                    (fun _ _ _ -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                Array.empty<Circuit.IRunObserver>

        let signature = createSignature<TestOutput> ()

        let runContext =
            runtime.CreateRunContext(
                RunId.New(),
                agent,
                signature,
                createRunOptions None StructuredOutputPolicy.NativeOnly
            )

        let mafToolFunction =
            CircuitResolvedToolFunction(resolvedTool, "tool_rich_v1", CircuitJson.createOptions (), runContext)

        let mafTool = ResolvedMafTool(resolvedTool, "tool_rich_v1", mafToolFunction)

        let definitionFingerprint = MafSessionContracts.definitionFingerprint agent

        let bindingFingerprint =
            MafSessionContracts.createSessionBinding
                runContext
                signature
                [| mafTool |]
                [| ResolvedSkill.Create(skill) |]

        Assert.False(String.IsNullOrWhiteSpace definitionFingerprint)
        Assert.False(String.IsNullOrWhiteSpace bindingFingerprint)

    [<Fact>]
    let ``definition fingerprints reject delimiter collisions in metadata values`` () =
        let agentWithEmbeddedMetadata =
            AgentDefinition.Create(
                "agent.collision",
                "1.0.0",
                "Agent Collision",
                "Stay stable.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                seq [ KeyValuePair("alpha", "one\nmetadata=beta=two") ]
            )

        let agentWithSplitMetadata =
            AgentDefinition.Create(
                "agent.collision",
                "1.0.0",
                "Agent Collision",
                "Stay stable.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                seq [ KeyValuePair("alpha", "one"); KeyValuePair("beta", "two") ]
            )

        Assert.NotEqual<string>(
            MafSessionContracts.definitionFingerprint agentWithEmbeddedMetadata,
            MafSessionContracts.definitionFingerprint agentWithSplitMetadata
        )

    [<Fact>]
    let ``session bindings include deterministic collection semantics and reject unsupported enumerables`` () =
        let runtime =
            createMafRuntimeWith
                ignore
                (new FakeChatClient(
                    (fun _ _ _ -> Task.FromResult(jsonResponse "unused")),
                    (fun _ _ _ -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Use skill properties."
        let signature = createSignature<TestOutput> ()

        let runContextA =
            runtime.CreateRunContext(
                RunId.New(),
                agent,
                signature,
                createRunOptions None StructuredOutputPolicy.NativeOnly
            )

        let runContextB =
            runtime.CreateRunContext(
                RunId.New(),
                agent,
                signature,
                createRunOptions None StructuredOutputPolicy.NativeOnly
            )

        let skillReference =
            SkillReference.Create("skill.properties", "1.0.0", "Skill properties", SkillSource.CreateCustom())

        let bindingFor runContext properties =
            ResolvedSkill.Create(skillReference, properties)
            |> fun skill -> MafSessionContracts.createSessionBinding runContext signature [||] [| skill |]

        let rec createNestedList depth =
            if depth = 0 then
                box "leaf"
            else
                let list = ResizeArray<obj>()
                list.Add(createNestedList (depth - 1))
                box list

        let jsonElement =
            JsonDocument.Parse("{\"kind\":\"element\",\"value\":1}").RootElement.Clone()

        use jsonDocument = JsonDocument.Parse("{\"kind\":\"document\",\"value\":2}")

        let scalarBindingA =
            bindingFor runContextA (seq { KeyValuePair("mode", box "alpha") })

        let scalarBindingB =
            bindingFor runContextA (seq { KeyValuePair("mode", box "beta") })

        let richBindingA =
            bindingFor
                runContextA
                (seq {
                    KeyValuePair("enabled", box true)
                    KeyValuePair("glyph", box 'a')
                    KeyValuePair("comparison", box StringComparison.Ordinal)
                    KeyValuePair("jsonElement", box jsonElement)
                    KeyValuePair("jsonDocument", box jsonDocument)
                })

        let richBindingB =
            bindingFor
                runContextA
                (seq {
                    KeyValuePair("enabled", box false)
                    KeyValuePair("glyph", box 'a')
                    KeyValuePair("comparison", box StringComparison.OrdinalIgnoreCase)
                    KeyValuePair("jsonElement", box jsonElement)
                    KeyValuePair("jsonDocument", box jsonDocument)
                })

        let hashSetBindingA =
            bindingFor
                runContextA
                (seq { KeyValuePair("items", box (HashSet<string>(seq [ "b"; "a" ], StringComparer.Ordinal))) })

        let hashSetBindingB =
            bindingFor
                runContextA
                (seq { KeyValuePair("items", box (HashSet<string>(seq [ "a"; "b" ], StringComparer.Ordinal))) })

        let dictionaryA = Dictionary<string, obj>(StringComparer.Ordinal)
        dictionaryA.Add("b", box 2)
        dictionaryA.Add("a", box 1)

        let dictionaryB = Dictionary<string, obj>(StringComparer.Ordinal)
        dictionaryB.Add("a", box 1)
        dictionaryB.Add("b", box 2)

        let dictionaryBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box dictionaryA) })

        let dictionaryBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box dictionaryB) })

        let orderedListA = ResizeArray<obj>(seq [ box "a"; box "b" ])
        let orderedListB = ResizeArray<obj>(seq [ box "b"; box "a" ])

        let orderedListBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box orderedListA) })

        let orderedListBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box orderedListB) })

        let unsupportedEnumerableA = LinkedList<obj>(seq [ box "a"; box "b" ])
        let unsupportedEnumerableB = LinkedList<obj>(seq [ box "b"; box "a" ])

        let unsupportedBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box unsupportedEnumerableA) })

        let unsupportedBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box unsupportedEnumerableB) })

        let unsupportedBindingC =
            bindingFor runContextB (seq { KeyValuePair("items", box unsupportedEnumerableA) })

        let nestedUnsupportedDictionaryA = Dictionary<string, obj>(StringComparer.Ordinal)
        nestedUnsupportedDictionaryA.Add("items", box unsupportedEnumerableA)

        let nestedUnsupportedDictionaryB = Dictionary<string, obj>(StringComparer.Ordinal)
        nestedUnsupportedDictionaryB.Add("items", box unsupportedEnumerableB)

        let nestedUnsupportedDictionaryBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box nestedUnsupportedDictionaryA) })

        let nestedUnsupportedDictionaryBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box nestedUnsupportedDictionaryB) })

        let nestedUnsupportedSetA = HashSet<obj>(ReferenceEqualityComparer.Instance)
        nestedUnsupportedSetA.Add(unsupportedEnumerableA :> obj) |> ignore

        let nestedUnsupportedSetB = HashSet<obj>(ReferenceEqualityComparer.Instance)
        nestedUnsupportedSetB.Add(unsupportedEnumerableB :> obj) |> ignore

        let nestedUnsupportedSetBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box nestedUnsupportedSetA) })

        let nestedUnsupportedSetBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box nestedUnsupportedSetB) })

        let deepBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", createNestedList 33) })

        let deepBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", createNestedList 34) })

        let cyclicA = ResizeArray<obj>()
        cyclicA.Add(cyclicA :> obj)

        let cyclicB = ResizeArray<obj>()
        cyclicB.Add(cyclicB :> obj)

        let cyclicBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box cyclicA) })

        let cyclicBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box cyclicB) })

        let largeListA =
            ResizeArray<obj>(
                seq {
                    for value in 0..4099 do
                        yield box value
                }
            )

        let largeListB =
            ResizeArray<obj>(
                seq {
                    for value in 0..4098 do
                        yield box value
                        yield box 999999
                }
            )

        let largeBindingA =
            bindingFor runContextA (seq { KeyValuePair("items", box largeListA) })

        let largeBindingB =
            bindingFor runContextA (seq { KeyValuePair("items", box largeListB) })

        Assert.NotEqual<string>(scalarBindingA, scalarBindingB)
        Assert.NotEqual<string>(richBindingA, richBindingB)
        Assert.Equal<string>(hashSetBindingA, hashSetBindingB)
        Assert.Equal<string>(dictionaryBindingA, dictionaryBindingB)
        Assert.NotEqual<string>(orderedListBindingA, orderedListBindingB)
        Assert.Equal<string>(unsupportedBindingA, unsupportedBindingB)
        Assert.NotEqual<string>(unsupportedBindingA, unsupportedBindingC)
        Assert.Equal<string>(nestedUnsupportedDictionaryBindingA, nestedUnsupportedDictionaryBindingB)
        Assert.Equal<string>(nestedUnsupportedSetBindingA, nestedUnsupportedSetBindingB)
        Assert.Equal<string>(deepBindingA, deepBindingB)
        Assert.Equal<string>(cyclicBindingA, cyclicBindingB)
        Assert.Equal<string>(largeBindingA, largeBindingB)

        let session =
            MafSessionContracts.createCircuitSession
                agent
                (MafSessionContracts.createSessionMetadata unsupportedBindingA)
                (DummyAgentSession() :> AgentSession)

        Assert.False(MafSessionContracts.hasMatchingSessionBinding unsupportedBindingC session)

module CSharpFacadeAndDependencyInjectionCoverageTests =
    open Helpers

    let private createClient () =
        new FakeChatClient(
            (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
            (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
        )

    [<Fact>]
    let ``C sharp facade snapshots configured options and adapts extension points`` () =
        let client = createClient ()
        let options = Circuit.MicrosoftAgentFrameworkOptions()
        options.DefaultModelId <- "model-primary"
        options.JsonSerializerOptions <- CircuitJson.createOptions ()
        options.SecondaryStructuredOutputClient <- client
        let toolResolver = EmptyPublicToolResolver() :> Circuit.IToolResolver
        let skillResolver = EmptyPublicSkillResolver() :> Circuit.ISkillResolver
        let observer = PublicRecordingObserver() :> Circuit.IRunObserver

        let adapted =
            CSharpFacadeAdapters.createRuntimeOptions
                options
                ([| toolResolver |] :> IReadOnlyList<Circuit.IToolResolver>)
                ([| skillResolver |] :> IReadOnlyList<Circuit.ISkillResolver>)
                ([| observer |] :> IReadOnlyList<Circuit.IRunObserver>)

        Assert.Equal(ValueSome "model-primary", adapted.DefaultModelId)
        Assert.True(adapted.JsonSerializerOptions.IsReadOnly)
        Assert.NotSame(options.JsonSerializerOptions, adapted.JsonSerializerOptions)
        Assert.Equal(ValueSome(client :> IChatClient), adapted.SecondaryStructuredOutputClient)
        Assert.Single(adapted.ToolResolvers) |> ignore
        Assert.Single(adapted.SkillResolvers) |> ignore
        Assert.Same(observer, Assert.Single(adapted.Observers))

    [<Fact>]
    let ``C sharp facade accepts mutable web serializer options and freezes an independent snapshot`` () =
        let source = JsonSerializerOptions(JsonSerializerDefaults.Web)
        let options = Circuit.MicrosoftAgentFrameworkOptions()
        options.JsonSerializerOptions <- source

        let adapted =
            CSharpFacadeAdapters.createRuntimeOptions
                options
                (Array.empty<Circuit.IToolResolver> :> IReadOnlyList<Circuit.IToolResolver>)
                (Array.empty<Circuit.ISkillResolver> :> IReadOnlyList<Circuit.ISkillResolver>)
                (Array.empty<Circuit.IRunObserver> :> IReadOnlyList<Circuit.IRunObserver>)

        source.WriteIndented <- true

        Assert.False(source.IsReadOnly)
        Assert.True(adapted.JsonSerializerOptions.IsReadOnly)
        Assert.NotSame(source, adapted.JsonSerializerOptions)
        Assert.NotNull(adapted.JsonSerializerOptions.TypeInfoResolver)
        Assert.False(adapted.JsonSerializerOptions.WriteIndented)

    [<Fact>]
    let ``runtime factory validates collections and creates a client`` () =
        let client = createClient ()

        let tools =
            Array.empty<Circuit.IToolResolver> :> IReadOnlyList<Circuit.IToolResolver>

        let skills =
            Array.empty<Circuit.ISkillResolver> :> IReadOnlyList<Circuit.ISkillResolver>

        let observers =
            Array.empty<Circuit.IRunObserver> :> IReadOnlyList<Circuit.IRunObserver>

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(null, null, tools, skills, observers)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(client, null, null, skills, observers)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(client, null, tools, null, observers)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(client, null, tools, skills, null)
            |> ignore)
        |> ignore

        let facade =
            MicrosoftAgentFrameworkRuntimeFactory.CreateClient(client, null, tools, skills, observers)

        Assert.NotNull(facade)

    [<Fact>]
    let ``DI registrations resolve singleton runtime and facade and require a chat client`` () =
        let client = createClient ()
        let services = ServiceCollection()
        services.AddSingleton<IChatClient>(client) |> ignore

        services.AddCircuit(
            Action<Circuit.CircuitOptions>(fun options ->
                options.MicrosoftAgentFramework.DefaultModelId <- "model-di"
                options.AddToolResolver(EmptyPublicToolResolver())
                options.AddSkillResolver(EmptyPublicSkillResolver())
                options.AddRunObserver(PublicRecordingObserver()))
        )
        |> ignore

        use provider = services.BuildServiceProvider()
        let runtime = provider.GetRequiredService<ICircuitRuntime>()
        let runtimeAgain = provider.GetRequiredService<ICircuitRuntime>()
        let facade = provider.GetRequiredService<Circuit.ICircuitClient>()
        let frozen = provider.GetRequiredService<Circuit.CircuitOptions>()

        Assert.Same(runtime, runtimeAgain)
        Assert.NotNull(facade)
        Assert.Equal("model-di", frozen.MicrosoftAgentFramework.DefaultModelId)
        Assert.Single(frozen.ToolResolvers) |> ignore
        Assert.Single(frozen.SkillResolvers) |> ignore
        Assert.Single(frozen.RunObservers) |> ignore

        let missing = ServiceCollection()
        missing.AddCircuit(Action<Circuit.CircuitOptions>(fun _ -> ())) |> ignore
        use missingProvider = missing.BuildServiceProvider()

        let ex =
            Assert.Throws<InvalidOperationException>(fun () ->
                missingProvider.GetRequiredService<ICircuitRuntime>() |> ignore)

        Assert.Contains("IChatClient singleton", ex.Message)

    [<Fact>]
    let ``MAF DI snapshots options and validates null options`` () =
        let client = createClient ()
        let services = ServiceCollection()

        Assert.Throws<ArgumentNullException>(fun () ->
            services.AddMafRuntime(client, Unchecked.defaultof<MafRuntimeOptions>) |> ignore)
        |> ignore

        let options = MafRuntimeOptions()
        options.DefaultModelId <- ValueSome "model-before"
        services.AddMafRuntime(client, options) |> ignore
        options.DefaultModelId <- ValueSome "model-after"

        use provider = services.BuildServiceProvider()
        let snapshot = provider.GetRequiredService<MafRuntimeOptions>()
        let runtime = provider.GetRequiredService<ICircuitRuntime>()
        Assert.Equal(ValueSome "model-before", snapshot.DefaultModelId)
        Assert.IsType<MafRuntime>(runtime) |> ignore

module RuntimeBranchClassificationCoverageTests =
    open Helpers

    let private createRuntime defaultModel =
        let client =
            new FakeChatClient(
                (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let options = MafRuntimeOptions()
        options.DefaultModelId <- defaultModel
        MafRuntime(client, options)

    let private failureCode (result: Result<'T, CircuitFailure>) =
        match result with
        | Ok _ -> failwith "Expected a classified failure."
        | Error failure -> failure.Code

    [<Fact>]
    let ``runtime classifies decode and provider execution failures`` () =
        let runId = RunId.New()
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        Assert.Equal(
            CircuitFailureCode.Cancelled,
            MafRuntimeInternals.classifyProviderExecutionFailure
                runId
                cancelled.Token
                (OperationCanceledException(cancelled.Token))
            |> _.Code
        )

        Assert.Equal(
            CircuitFailureCode.StructuredOutputUnsupported,
            MafRuntimeInternals.classifyProviderExecutionFailure
                runId
                CancellationToken.None
                (NotSupportedException("unsupported"))
            |> _.Code
        )

        Assert.Equal(
            CircuitFailureCode.Provider,
            MafRuntimeInternals.classifyProviderExecutionFailure
                runId
                CancellationToken.None
                (InvalidOperationException("provider failed"))
            |> _.Code
        )

        let signature = createSignature<TestOutput> ()

        let decode getOutput token =
            MafRuntimeInternals.decodeResponseResult runId token signature getOutput
            |> failureCode

        Assert.Equal(
            CircuitFailureCode.Cancelled,
            decode (fun () -> raise (OperationCanceledException(cancelled.Token))) cancelled.Token
        )

        Assert.Equal(
            CircuitFailureCode.StructuredOutputUnsupported,
            decode (fun () -> raise (NotSupportedException("unsupported"))) CancellationToken.None
        )

        Assert.Equal(
            CircuitFailureCode.Decode,
            decode (fun () -> raise (JsonException("invalid JSON"))) CancellationToken.None
        )

        Assert.Equal(
            CircuitFailureCode.Provider,
            decode (fun () -> raise (InvalidOperationException("provider failed"))) CancellationToken.None
        )

        let valid =
            MafRuntimeInternals.decodeResponseResult runId CancellationToken.None signature (fun () ->
                TestOutput(Text = "valid"))

        match valid with
        | Ok output -> Assert.Equal("valid", output.Text)
        | Error failure -> failwith failure.Message

    [<Fact>]
    let ``runtime resolves model precedence and validates internal entry points`` () =
        let noDefault = createRuntime ValueNone
        let withDefault = createRuntime (ValueSome "default-model")
        let inheritedAgent = createAgent "Use the default model."

        let explicitAgent =
            AgentDefinition.Create(
                "agent.explicit-model",
                "1.0.0",
                "Explicit model",
                "Use the explicit model.",
                ValueSome "explicit-model",
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        Assert.Equal(ValueNone, noDefault.ResolveRequestModel inheritedAgent)
        Assert.Equal(ValueSome "default-model", withDefault.ResolveRequestModel inheritedAgent)
        Assert.Equal(ValueSome "explicit-model", withDefault.ResolveRequestModel explicitAgent)

        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let nullAgent = Unchecked.defaultof<AgentDefinition>
        let nullSignature = Unchecked.defaultof<Signature<TestInput, TestOutput>>
        let nullOptions = Unchecked.defaultof<RunOptions>

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.RunAsyncCore nullAgent signature (TestInput()) runOptions CancellationToken.None
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.RunAsyncCore inheritedAgent nullSignature (TestInput()) runOptions CancellationToken.None
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.RunAsyncCore inheritedAgent signature (TestInput()) nullOptions CancellationToken.None
            |> ignore)
        |> ignore

        let nullSession = Unchecked.defaultof<CircuitSession>

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.SerializeSessionAsyncCore(nullAgent, nullSession, runOptions, CancellationToken.None)
            |> ignore)
        |> ignore

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.SerializeSessionAsyncCore(inheritedAgent, nullSession, runOptions, CancellationToken.None)
            |> ignore)
        |> ignore

        use state = JsonDocument.Parse("{}")

        Assert.Throws<ArgumentNullException>(fun () ->
            noDefault.DeserializeSessionAsyncCore(nullAgent, state.RootElement, runOptions, CancellationToken.None)
            |> ignore)
        |> ignore

type private DisposableProbe() =
    let mutable disposed = false
    member _.Disposed = disposed

    interface IDisposable with
        member _.Dispose() = disposed <- true

module InlineSkillAdapterCoverageTests =
    open Helpers

    [<Fact>]
    let ``inline skill attachment adapts static dynamic and scripted capabilities`` () =
        let staticResource = SkillResource.Create("guide.txt", box "guide", "Guide")

        let dynamicResource =
            SkillResource.CreateDynamic(
                "live.json",
                Func<SkillResourceContext, CancellationToken, Task<obj>>(fun context _ ->
                    Task.FromResult(box context.RunId.Value)),
                "Live data"
            )

        let script = SkillScriptDescriptor.Create("normalize", "Normalize input")

        let source =
            SkillSource.CreateInline("Use every capability.", [| staticResource; dynamicResource |], [| script |])

        let reference =
            SkillReference.Create(
                "skill.inline.adapter",
                "1.2.3",
                "",
                source,
                seq [ KeyValuePair("owner", "platform") ]
            )

        let resolved = ResolvedSkill.Create(reference)

        let runContext =
            RunContext(
                RunId.New(),
                createAgent "Use inline skills.",
                DefinitionId.Create("agent.inline"),
                SemanticVersion.Parse("1.0.0"),
                RunOptions.Default
            )

        let options = MafRuntimeOptions()
        let mutable scripts = 0

        options.SkillScriptRunner <-
            ValueSome
                { new ISkillScriptRunner with
                    member _.RunAsync(request, _cancellationToken) =
                        scripts <- scripts + 1
                        Assert.Equal("normalize", request.Script.Name)
                        Task.FromResult(SkillScriptResult.Create(box "normalized")) }

        let attachment =
            MafSkillAdapter.createAttachment options runContext ([| resolved |] :> IReadOnlyList<ResolvedSkill>)

        Assert.True(attachment.IsSome)
        Assert.True(attachment.Value.ScriptsEnabled)
        Assert.Single(MafSkillAdapter.getProviders attachment) |> ignore
        Assert.Equal(0, scripts)
        (attachment.Value :> IDisposable).Dispose()

        let empty =
            MafSkillAdapter.createAttachment
                options
                runContext
                (Array.empty<ResolvedSkill> :> IReadOnlyList<ResolvedSkill>)

        let nullSkills =
            MafSkillAdapter.createAttachment options runContext (Unchecked.defaultof<IReadOnlyList<ResolvedSkill>>)

        Assert.True(empty.IsNone)
        Assert.True(nullSkills.IsNone)
        Assert.Empty(MafSkillAdapter.getProviders ValueNone)

    [<Fact>]
    let ``skill attachment disposal tolerates absent entries and disposes owned snapshots`` () =
        let probe = new DisposableProbe()

        let owned =
            [| Unchecked.defaultof<IDisposable>; probe :> IDisposable |] :> IReadOnlyList<IDisposable>

        let attachment = new MafSkillProviderAttachment(null, false, owned)
        (attachment :> IDisposable).Dispose()
        Assert.True(probe.Disposed)

        let emptyAttachment =
            new MafSkillProviderAttachment(null, false, Unchecked.defaultof<IReadOnlyList<IDisposable>>)

        (emptyAttachment :> IDisposable).Dispose()
        Assert.False(emptyAttachment.ScriptsEnabled)

module StructuredOutputWrapperCoverageTests =
    open Helpers
    open AdapterCoverageHelpers

    [<Fact>]
    let ``structured output wrapper repairs responses and preserves original diagnostics`` () =
        let mutable repairMessages: IReadOnlyList<ChatMessage> = null
        let mutable repairOptions: ChatOptions = null

        let repairClient =
            new FakeChatClient(
                (fun messages options _ct ->
                    repairMessages <- messages |> Seq.toArray :> IReadOnlyList<ChatMessage>
                    repairOptions <- options
                    Task.FromResult(jsonResponse "{\"text\":\"repaired\"}")),
                (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
            )

        let originalUsage = UsageDetails()
        originalUsage.InputTokenCount <- Nullable 3L
        let original = AgentResponse(ChatMessage(ChatRole.Assistant, "plain original"))
        original.Usage <- originalUsage
        let inner = FixedResponseAgent(fun () -> original)
        let wrapperOptions = MafStructuredOutputAgentOptions()
        wrapperOptions.ChatClientSystemMessage <- "Repair this response."
        wrapperOptions.ChatOptions <- ChatOptions()
        let wrapper = MafStructuredOutputAgent(inner, repairClient, wrapperOptions)

        let runOptions =
            AgentRunOptions(ResponseFormat = createJsonResponseFormat<TestOutput> ())

        let repaired =
            wrapper.RunAsync("input", DummyAgentSession(), runOptions, CancellationToken.None).GetAwaiter().GetResult()

        Assert.IsType<MafStructuredOutputAgentResponse>(repaired) |> ignore
        Assert.Equal(2, repairMessages.Count)
        Assert.Equal(ChatRole.System, repairMessages[0].Role)
        Assert.Equal("Repair this response.", repairMessages[0].Text)
        Assert.Equal("plain original", repairMessages[1].Text)
        Assert.NotNull(repairOptions.ResponseFormat)

        let structured =
            MafStructuredOutputAgentResponse(jsonResponse "{\"text\":\"ok\"}", original)

        let envelope = AgentResponse()
        envelope.RawRepresentation <- structured
        Assert.True(MafStructuredOutput.wasRepaired envelope)
        Assert.Equal(ValueSome "plain original", MafStructuredOutput.tryGetOriginalResponseText envelope)
        Assert.Same(originalUsage, MafStructuredOutput.tryGetOriginalUsage envelope |> ValueOption.get)

        let nested = AgentResponse()
        nested.RawRepresentation <- envelope
        Assert.True(MafStructuredOutput.tryGetStructuredOutputResponse nested |> ValueOption.isSome)

        original.Messages <- ResizeArray<ChatMessage>([ ChatMessage(ChatRole.Assistant, "") ])
        original.Usage <- null
        Assert.Equal(ValueNone, MafStructuredOutput.tryGetOriginalResponseText envelope)
        Assert.Equal(ValueNone, MafStructuredOutput.tryGetOriginalUsage envelope)
        Assert.Equal(ValueNone, MafStructuredOutput.tryGetStructuredOutputResponse null)

        Assert.Null(MafStructuredOutput.removeResponseFormat null)
        let textOptions = AgentRunOptions(ResponseFormat = ChatResponseFormat.Text)
        let withoutFormat = MafStructuredOutput.removeResponseFormat textOptions
        Assert.Null(withoutFormat.ResponseFormat)
        Assert.Same(ChatResponseFormat.Text, textOptions.ResponseFormat)

module ResolvedToolObserverFailureCoverageTests =
    open Helpers

    [<Fact>]
    let ``resolved tool maps execution exception families to observer-safe failures`` () =
        let client =
            new FakeChatClient(
                (fun _ _ _ -> Task.FromResult(jsonResponse "unused")),
                (fun _ _ _ -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Use tools."
        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let runContext = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        let tool =
            createResolvedTool
                (createTestTool "tool.observer" ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(TestOutput(Text = input.Token))))
                Seq.empty

        let toolFunction =
            CircuitResolvedToolFunction(tool, "tool_observer_v1", CircuitJson.createOptions (), runContext)

        let classify =
            typeof<CircuitResolvedToolFunction>
                .GetMethod("CreateObserverFailure", BindingFlags.Instance ||| BindingFlags.NonPublic)

        let invoke exceptionValue =
            classify.Invoke(toolFunction, [| box "operation-1"; box exceptionValue |]) :?> CircuitFailure

        let cancelled = invoke (OperationCanceledException("cancelled"))

        let validation =
            invoke (SanitizedToolException("Validation failed: $.token: Required."))

        let decode = invoke (SanitizedToolException("Tool input could not be parsed."))
        let toolFailure = invoke (SanitizedToolException("Tool invocation failed."))
        let unknown = invoke (InvalidOperationException("secret provider detail"))

        Assert.Equal(CircuitFailureCode.Cancelled, cancelled.Code)
        Assert.Equal("The tool was cancelled.", cancelled.Message)
        Assert.Equal(CircuitFailureCode.Validation, validation.Code)
        Assert.StartsWith("Validation failed", validation.Message)
        Assert.Equal(CircuitFailureCode.Decode, decode.Code)
        Assert.Equal("Tool input could not be parsed.", decode.Message)
        Assert.Equal(CircuitFailureCode.Tool, toolFailure.Code)
        Assert.Equal("Tool invocation failed.", toolFailure.Message)
        Assert.Equal(CircuitFailureCode.Tool, unknown.Code)
        Assert.Equal("Tool execution failed.", unknown.Message)
        Assert.Equal(ValueSome "operation-1", unknown.OperationId)

module StreamingHelperMatrixCoverageTests =
    open Helpers

    [<Fact>]
    let ``streaming helpers normalize approval payloads and classify wrapped outputs`` () =
        let primitiveSignature = createSignature<int> ()
        let objectSignature = createSignature<TestOutput> ()

        let primitiveFormat, primitiveWrapped =
            MafStreaming.createWrappedResponseFormat primitiveSignature

        let _, objectWrapped = MafStreaming.createWrappedResponseFormat objectSignature
        Assert.NotNull(primitiveFormat)
        Assert.False(primitiveWrapped)
        Assert.False(objectWrapped)
        Assert.Equal(42, MafStreaming.deserializeOutput primitiveSignature true "{\"data\":42}")

        let missingData =
            Assert.Throws<JsonException>(fun () ->
                MafStreaming.deserializeOutput primitiveSignature true "{\"other\":42}"
                |> ignore)

        Assert.Contains("did not contain", missingData.Message)

        Assert.Throws<JsonException>(fun () -> MafStreaming.deserializeOutput objectSignature false "null" |> ignore)
        |> ignore

        let options = CircuitJson.createOptions ()
        let arguments = Dictionary<string, obj>()
        arguments["OuterValue"] <- box [| dict [ "InnerValue", box 1 ]; dict [ "trace_runtime", box 2 ] |]

        let serialized =
            MafStreaming.trySerializeApprovalArguments options arguments |> ValueOption.get

        use payload = JsonDocument.Parse(serialized)
        let outer = payload.RootElement.GetProperty("OuterValue")
        Assert.Equal(2, outer.GetArrayLength())
        Assert.Equal(1, outer[0].GetProperty("innerValue").GetInt32())
        Assert.Equal(2, outer[1].GetProperty("trace_runtime").GetInt32())
        Assert.Equal(ValueNone, MafStreaming.trySerializeApprovalArguments options null)

        let cyclic = ResizeArray<obj>()
        cyclic.Add(cyclic)
        let invalidArguments = Dictionary<string, obj>()
        invalidArguments["cycle"] <- cyclic
        Assert.Equal(ValueNone, MafStreaming.trySerializeApprovalArguments options invalidArguments)

        let blankCall = FunctionCallContent(" ", " ", null)
        let approval = ToolApprovalRequestContent("approval-blank", blankCall)
        let request = MafStreaming.createApprovalRequest options approval
        Assert.Equal("unknown-tool-call", request.ToolName)
        Assert.Equal(ValueNone, MafStreaming.tryGetOperationId blankCall)
        Assert.Equal(ValueNone, MafStreaming.tryGetOperationId null)

        let unsupported =
            MafStreaming.createApprovalRequest
                options
                (ToolApprovalRequestContent("approval-unknown", UnknownToolCallContent("call-x")))

        Assert.Equal("unknown-tool-call", unsupported.ToolName)
        Assert.Equal(ValueNone, unsupported.ArgumentsJson)

        let runId = RunId.New()

        let missingResult =
            MafStreaming.decodeFinalOutput runId CancellationToken.None primitiveSignature true "{\"wrong\":1}"

        match missingResult with
        | Error failure -> Assert.Equal(CircuitFailureCode.Decode, failure.Code)
        | Ok _ -> failwith "Expected wrapped decode failure."

        let validResult =
            MafStreaming.decodeFinalOutput runId CancellationToken.None primitiveSignature true "{\"data\":7}"

        match validResult with
        | Ok value -> Assert.Equal(7, value)
        | Error failure -> failwith failure.Message

    [<Fact>]
    let ``stream mapping distinguishes tool lifecycle approval and terminal suppression`` () =
        let options = CircuitJson.createOptions ()
        let started = FunctionCallContent("call-start", "tool.one", null)
        let completed = FunctionResultContent("call-done", box "result")
        let text = TextContent("plain")

        match MafStreaming.StreamingMappedEvent.tryMapContent options started with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolStarted operationId) ->
            Assert.Equal(ValueSome "call-start", operationId)
        | _ -> failwith "Expected tool-start mapping."

        match MafStreaming.StreamingMappedEvent.tryMapContent options completed with
        | ValueSome(MafStreaming.StreamingMappedEvent.ToolCompleted operationId) ->
            Assert.Equal(ValueSome "call-done", operationId)
        | _ -> failwith "Expected tool-completion mapping."

        Assert.Equal(ValueNone, MafStreaming.StreamingMappedEvent.tryMapContent options text)
        Assert.False(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.OutputDelta)
        Assert.True(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.RunCompleted)
        Assert.True(MafStreaming.StreamingMappedEvent.isTerminal RunEventKind.RunFailed)
        Assert.False(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal false RunEventKind.RunCompleted)
        Assert.False(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal true RunEventKind.OutputDelta)
        Assert.True(MafStreaming.StreamingMappedEvent.shouldSuppressTerminal true RunEventKind.RunFailed)

module PreparedFileSessionBindingCoverageTests =
    open Helpers

    [<Fact>]
    let ``session binding includes prepared file snapshot manifests`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "circuit-maf-binding-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        File.WriteAllText(
            Path.Combine(root, "SKILL.md"),
            "---\nname: binding-skill\ndescription: Binding\n---\nUse resources."
        )

        File.WriteAllText(Path.Combine(root, "guide.txt"), "first")

        try
            let reference =
                SkillReference.Create("skill.binding.file", "1.0.0", "Binding file skill", SkillSource.CreateFile(root))

            let agent = createAgent "Use a prepared file skill."

            let runContext =
                RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

            let signature = createSignature<TestOutput> ()

            let prepare () =
                MafSkillAdapter.prepareResolvedSkill (ResolvedSkill.Create(reference))

            let first = prepare ()

            let firstPrepared =
                first.TryGetProperty<MafSkillAdapter.MafPreparedFileSkills>(MafSkillAdapterProperties.FileSkills)
                |> ValueOption.get

            Assert.Single(firstPrepared.Snapshots) |> ignore
            let firstSnapshot = Assert.Single(firstPrepared.Snapshots.Values)
            Assert.False(String.IsNullOrWhiteSpace firstSnapshot.ManifestFingerprint)

            let firstBinding =
                MafSessionContracts.createSessionBinding runContext signature [||] [| first |]

            File.WriteAllText(Path.Combine(root, "guide.txt"), "second")
            let second = prepare ()

            let secondPrepared =
                second.TryGetProperty<MafSkillAdapter.MafPreparedFileSkills>(MafSkillAdapterProperties.FileSkills)
                |> ValueOption.get

            let secondBinding =
                MafSessionContracts.createSessionBinding runContext signature [||] [| second |]

            Assert.NotEqual<string>(firstBinding, secondBinding)

            Assert.NotEqual<string>(
                firstSnapshot.ManifestFingerprint,
                Assert.Single(secondPrepared.Snapshots.Values).ManifestFingerprint
            )

            (firstPrepared :> IDisposable).Dispose()
            (secondPrepared :> IDisposable).Dispose()
        finally
            Directory.Delete(root, true)

module SnapshotCaptureMutationCoverageTests =
    [<Fact>]
    let ``snapshot capture rejects directory length and timestamp mutations`` () =
        let root =
            Path.Combine(Path.GetTempPath(), "circuit-maf-capture-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "SKILL.md"), "---\nname: capture\ndescription: Capture\n---\nBody")
        let directoryPath = Path.Combine(root, "folder")
        Directory.CreateDirectory(directoryPath) |> ignore
        let filePath = Path.Combine(root, "guide.txt")
        File.WriteAllText(filePath, "stable")
        let canonicalRoot = SkillPathSecurity.validateSkillRootPath root

        let read hooks entry relative =
            MafSkillAdapter.SnapshotCapture.readTextFileWithHooks canonicalRoot entry relative hooks

        try
            let directoryFailure =
                Assert.Throws<InvalidOperationException>(fun () ->
                    read { BeforeOpen = ValueNone } directoryPath "folder" |> ignore)

            Assert.Contains("regular files", directoryFailure.Message)

            let lengthFailure =
                Assert.Throws<InvalidOperationException>(fun () ->
                    read
                        { BeforeOpen =
                            ValueSome(
                                Action<MafSkillAdapter.SnapshotCaptureEntryState>(fun _ ->
                                    File.AppendAllText(filePath, " changed"))
                            ) }
                        filePath
                        "guide.txt"
                    |> ignore)

            Assert.Contains("length change", lengthFailure.Message)

            File.WriteAllText(filePath, "stable")

            let timestampFailure =
                Assert.Throws<InvalidOperationException>(fun () ->
                    read
                        { BeforeOpen =
                            ValueSome(
                                Action<MafSkillAdapter.SnapshotCaptureEntryState>(fun state ->
                                    File.SetLastWriteTimeUtc(filePath, state.LastWriteTimeUtc.AddMinutes(2.0)))
                            ) }
                        filePath
                        "guide.txt"
                    |> ignore)

            Assert.Contains("last-write change", timestampFailure.Message)
        finally
            Directory.Delete(root, true)

module RuntimeObservabilityDecisionCoverageTests =
    open Helpers
    open AdapterCoverageHelpers

    let private createRuntime () =
        let client =
            new FakeChatClient(
                (fun _ _ _ -> Task.FromResult(jsonResponse "unused")),
                (fun _ _ _ -> ArrayAsyncEnumerable(Array.empty))
            )

        MafRuntime(client, MafRuntimeOptions())

    [<Fact>]
    let ``runtime diagnostic metadata honors sensitive data mode for repaired responses`` () =
        let runtime = createRuntime ()

        let original =
            AgentResponse(ChatMessage(ChatRole.Assistant, "original sensitive text"))

        let structured =
            MafStructuredOutputAgentResponse(jsonResponse "{\"text\":\"ok\"}", original)

        let response = AgentResponse()
        response.RawRepresentation <- structured
        let standard = createRunOptionsWithSensitiveData SensitiveDataMode.Standard
        let redacted = createRunOptionsWithSensitiveData SensitiveDataMode.Redact
        let standardMetadata = runtime.CreateDiagnosticMetadata(standard, response)
        let redactedMetadata = runtime.CreateDiagnosticMetadata(redacted, response)

        Assert.Equal("true", standardMetadata["circuit.repaired"])
        Assert.Equal("original sensitive text", standardMetadata["circuit.repair.originalResponse"])
        Assert.Equal("true", redactedMetadata["circuit.repaired"])
        Assert.False(redactedMetadata.ContainsKey("circuit.repair.originalResponse"))
        Assert.Empty(runtime.CreateDiagnosticMetadata(standard, AgentResponse()))

    [<Fact>]
    let ``scheduler observer session suppresses duplicate agent root registration`` () =
        let observer = PublicRecordingObserver() :> Circuit.IRunObserver
        let observers = [| observer |] :> IReadOnlyList<Circuit.IRunObserver>
        let runId = RunId.New()
        let definitionId = DefinitionId.Create("observed.circuit")
        let version = SemanticVersion.Parse("1.0.0")
        let services = NullServiceProvider() :> IServiceProvider

        let circuitSession =
            MafObserver.createCircuitRunSession observers runId definitionId version services

        Assert.True(circuitSession.IsSome)

        let duplicateAgent =
            MafObserver.createAgentRunSession observers runId "Agent" definitionId version ValueNone services

        Assert.True(duplicateAgent.IsNone)
        Assert.True(MafObserver.tryGetSession runId |> ValueOption.isSome)
        MafObserver.unregisterSession circuitSession
        Assert.True(MafObserver.tryGetSession runId |> ValueOption.isNone)
        MafObserver.unregisterSession ValueNone

module ResolvedToolInputFailureCoverageTests =
    open Helpers

    [<Fact>]
    let ``resolved tool sanitizes cyclic model arguments before invocation`` () =
        let client =
            new FakeChatClient(
                (fun _ _ _ -> Task.FromResult(jsonResponse "unused")),
                (fun _ _ _ -> ArrayAsyncEnumerable(Array.empty))
            )

        let runtime =
            createMafRuntimeWith ignore client None Array.empty<Circuit.IRunObserver>

        let agent = createAgent "Use tools."
        let signature = createSignature<TestOutput> ()
        let runOptions = createRunOptions None StructuredOutputPolicy.NativeOnly
        let runContext = runtime.CreateRunContext(RunId.New(), agent, signature, runOptions)

        let tool =
            createResolvedTool
                (createTestTool "tool.input.failure" ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(TestOutput(Text = input.Token))))
                Seq.empty

        let toolFunction =
            CircuitResolvedToolFunction(tool, "tool_input_failure_v1", CircuitJson.createOptions (), runContext)

        let deserialize =
            typeof<CircuitResolvedToolFunction>
                .GetMethod("DeserializeInput", BindingFlags.Instance ||| BindingFlags.NonPublic)

        let cycle = ResizeArray<obj>()
        cycle.Add(cycle)
        let arguments = Dictionary<string, obj>()
        arguments["cycle"] <- cycle

        let exceptionValue =
            Assert.Throws<TargetInvocationException>(fun () ->
                deserialize.Invoke(
                    toolFunction,
                    [| box (AIFunctionArguments(arguments)); box CancellationToken.None |]
                )
                |> ignore)

        let sanitized = Assert.IsType<SanitizedToolException>(exceptionValue.InnerException)
        Assert.Equal("Tool input could not be parsed.", sanitized.Message)
        Assert.NotNull(sanitized.InnerException)

module AmbiguousToolSelectionCoverageTests =
    open Helpers

    [<Fact>]
    let ``tag selection rejects tools with colliding model-facing identities`` () =
        task {
            let first =
                createResolvedTool
                    (createTestTool "tool.alpha" ApprovalMode.Never ValueNone (fun _ input ->
                        Task.FromResult(TestOutput(Text = input.Token))))
                    [| "selected" |]

            let second =
                createResolvedTool
                    (createTestTool "tool-alpha" ApprovalMode.Never ValueNone (fun _ input ->
                        Task.FromResult(TestOutput(Text = input.Token))))
                    [| "selected" |]

            let options = MafRuntimeOptions()
            options.ToolResolvers <- [| StaticToolResolver([| first; second |]) :> IToolResolver |]

            let agent =
                AgentDefinition.Create(
                    "agent.ambiguous-tools",
                    "1.0.0",
                    "Ambiguous tools",
                    "Use selected tools.",
                    ValueNone,
                    [| "selected" |],
                    Seq.empty,
                    Seq.empty
                )

            let context =
                RunContext(RunId.New(), agent, agent.Id, agent.Version, RunOptions.Default)

            let mutable captured: ToolCapabilityFailureException option = None

            try
                let! _ = MafAgentFactory.resolveToolsAsync options context agent CancellationToken.None
                ()
            with :? ToolCapabilityFailureException as ex ->
                captured <- Some ex

            Assert.True(captured.IsSome)
            Assert.Equal(CircuitFailureCode.Tool, captured.Value.Failure.Code)
            Assert.Contains("same model-facing identity", captured.Value.Failure.Message)
            Assert.Contains("tool.alpha@1.0.0", captured.Value.Failure.Message)
            Assert.Contains("tool-alpha@1.0.0", captured.Value.Failure.Message)
        }
