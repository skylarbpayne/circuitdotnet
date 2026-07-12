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

module MoreOpenTelemetryCoverageTests =
    open Helpers

    [<Sealed>]
    type private TelemetryHarness(?sources: string array) =
        let spans = ResizeArray<Activity>()
        let metrics = ResizeArray<Metric>()
        let sources = defaultArg sources [| TelemetryContracts.ActivitySourceName |]

        let tracerProviderBuilder = Sdk.CreateTracerProviderBuilder()

        do
            for source in sources do
                tracerProviderBuilder.AddSource(source) |> ignore

        let tracerProvider = tracerProviderBuilder.AddInMemoryExporter(spans).Build()

        let meterProvider =
            Sdk
                .CreateMeterProviderBuilder()
                .AddMeter(TelemetryContracts.ActivitySourceName)
                .AddInMemoryExporter(metrics)
                .Build()

        member _.Spans = spans |> Seq.toArray
        member _.Metrics = metrics |> Seq.toArray

        member _.Flush() =
            tracerProvider.ForceFlush() |> ignore
            meterProvider.ForceFlush() |> ignore

        interface IDisposable with
            member _.Dispose() =
                meterProvider.Dispose()
                tracerProvider.Dispose()

    let private tryGetTag name (activity: Activity) =
        activity.TagObjects
        |> Seq.tryPick (fun pair -> if pair.Key = name then Some pair.Value else None)

    let private createFailure code message runId operationId =
        let ctor =
            typeof<Circuit.AgentFailure>
                .GetConstructor(
                    BindingFlags.Instance ||| BindingFlags.NonPublic,
                    null,
                    [| typeof<AgentFailureCode>
                       typeof<string>
                       typeof<string>
                       typeof<string>
                       typeof<string>
                       typeof<Exception> |],
                    null
                )

        ctor.Invoke([| box code; box message; box runId; box operationId; null; null |]) :?> Circuit.AgentFailure

    let private createEnvelopeWithFailure
        kind
        runId
        operationId
        operationName
        operationKind
        prompt
        input
        output
        toolArguments
        repaired
        failure
        =
        let createMethod =
            typeof<Circuit.RunEventEnvelope>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

        createMethod.Invoke(
            null,
            [| box runId
               box DateTimeOffset.UtcNow
               box kind
               box "definition.id"
               box "1.0.0"
               box "agent"
               box operationId
               box operationName
               box operationKind
               null
               null
               box prompt
               box input
               box output
               box toolArguments
               box failure
               null
               box (Nullable<DateTimeOffset>())
               box (Nullable<DateTimeOffset>())
               box repaired
               null
               null
               null |]
        )
        :?> Circuit.RunEventEnvelope

    let private createEnvelope
        kind
        runId
        operationId
        operationName
        operationKind
        prompt
        input
        output
        toolArguments
        repaired
        =
        createEnvelopeWithFailure
            kind
            runId
            operationId
            operationName
            operationKind
            prompt
            input
            output
            toolArguments
            repaired
            null

    let private metricPoints (metric: Metric) =
        [| for point in metric.GetMetricPoints() -> point |]

    let private metricTags (metric: Metric) =
        metricPoints metric
        |> Array.collect (fun (point: MetricPoint) -> [| for tag in point.Tags -> tag |])

    let private metricSum (metric: Metric) =
        metricPoints metric
        |> Array.sumBy (fun (point: MetricPoint) -> point.GetSumLong())

    let private histogramCount (metric: Metric) =
        metricPoints metric
        |> Array.sumBy (fun (point: MetricPoint) -> point.GetHistogramCount())

    let private metricByName name (metrics: Metric[]) =
        metrics |> Array.find (fun metric -> metric.Name = name)

    let private collectUntil<'T> (predicate: RunEvent<'T> -> bool) (run: WorkflowRun<'T>) =
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
    let ``open telemetry observer covers validation repairs and swallowed redactor failures`` () =
        use telemetry = new TelemetryHarness()

        let validationObserver = OpenTelemetryRunObserver()

        let validationRuntime =
            createRuntime
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":null}")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| validationObserver :> Circuit.IRunObserver |]

        let validationResult =
            validationRuntime
                .RunAsync(
                    createAgent "Validate output.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "validation"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(validationResult.Result.IsSuccess)

        let repairOptions = OpenTelemetryRunObserverOptions()
        repairOptions.CaptureOutput <- true
        repairOptions.Redactor <- Func<string, string>(fun _ -> String.Empty)

        let repairObserver = OpenTelemetryRunObserver(repairOptions)

        let repairRuntime =
            createRuntime
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "unstructured")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                (Some(
                    new FakeChatClient(
                        (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"repaired\"}")),
                        (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                    )
                ))
                [| repairObserver :> Circuit.IRunObserver |]

        let repairResult =
            repairRuntime
                .RunAsync(
                    createAgent "Repair output.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "repair"),
                    createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                    CancellationToken.None
                )
                .Result

        Assert.True(repairResult.Result.IsSuccess)

        let tool =
            createResolvedTool
                (createTestTool "tool.otel" ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(TestOutput(Text = input.Token))))
                Seq.empty

        let throwingRedactorOptions = OpenTelemetryRunObserverOptions()
        throwingRedactorOptions.CapturePrompt <- true

        throwingRedactorOptions.Redactor <-
            Func<string, string>(fun _ -> raise (InvalidOperationException("redactor boom")))

        let throwingObserver = OpenTelemetryRunObserver(throwingRedactorOptions)

        let throwingRuntime =
            createRuntimeWith
                (fun options -> options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                (new FakeChatClient(
                    (fun messages _options _ct ->
                        match tryGetFunctionResult messages with
                        | Some functionResult ->
                            let output = Assert.IsType<TestOutput>(functionResult.Result)
                            Task.FromResult(jsonResponse $"{{\"text\":\"{output.Text}\"}}")
                        | None ->
                            let arguments = Dictionary<string, obj>()
                            arguments["token"] <- "otel"
                            Task.FromResult(functionCallResponse "tool-call-otel" "tool_otel_v1" arguments)),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| throwingObserver :> Circuit.IRunObserver |]

        let throwingResult =
            throwingRuntime
                .RunAsync(
                    createAgent "Trigger tool run.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "otel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        telemetry.Flush()

        Assert.True(throwingResult.Result.IsSuccess)
        Assert.Contains(telemetry.Metrics, fun metric -> metric.Name = "circuit.validation.failures")
        Assert.Contains(telemetry.Metrics, fun metric -> metric.Name = "circuit.structured_output.repairs")

        let repairedRoot =
            telemetry.Spans
            |> Array.filter (fun span -> span.OperationName = "agent.run")
            |> Array.last

        Assert.Null(tryGetTag "circuit.output" repairedRoot)

    [<Fact>]
    let ``open telemetry observer handles orphan and null-activity events without crashing`` () =
        let options = OpenTelemetryRunObserverOptions()
        options.CapturePrompt <- true
        options.CaptureInput <- true
        options.CaptureOutput <- true
        options.CaptureToolArguments <- true

        let observer = OpenTelemetryRunObserver(options) :> Circuit.IRunObserver
        let runId = "otel-no-listener"

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.ToolStarted
                "missing-run"
                "tool-1"
                "tool.one"
                RunOperationKind.Tool
                null
                null
                null
                "{}"
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.StepCompleted
                "missing-run"
                "step-1"
                "step.one"
                RunOperationKind.WorkflowStep
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.RunStarted
                runId
                runId
                "agent.run"
                RunOperationKind.Run
                "prompt"
                "input"
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.StepStarted
                runId
                "step-1"
                "step.one"
                RunOperationKind.WorkflowStep
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.ToolStarted
                runId
                "tool-1"
                "tool.one"
                RunOperationKind.Tool
                null
                null
                null
                "{}"
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.ToolCompleted
                runId
                "tool-missing"
                "tool.one"
                RunOperationKind.Tool
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.OutputDelta
                runId
                runId
                "agent.run"
                RunOperationKind.Run
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.RunCompleted
                runId
                runId
                "agent.run"
                RunOperationKind.Run
                null
                null
                "output"
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.RunCompleted
                "missing-run"
                "missing-run"
                "agent.run"
                RunOperationKind.Run
                null
                null
                "output"
                null
                false,
            CancellationToken.None
        )
        |> ignore

    [<Fact>]
    let ``open telemetry observer force-closes abandoned tool and workflow-step operations with their original metric tags``
        ()
        =
        use telemetry = new TelemetryHarness()

        let observer = OpenTelemetryRunObserver() :> Circuit.IRunObserver
        let runId = "otel-force-close"

        let failure =
            createFailure AgentFailureCode.Cancelled "The run was cancelled." runId null

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.RunStarted
                runId
                runId
                "agent.run"
                RunOperationKind.Run
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.ToolStarted
                runId
                "tool-1"
                "tool.one"
                RunOperationKind.Tool
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelope
                AgentRunEventKind.StepStarted
                runId
                "step-1"
                "step.one"
                RunOperationKind.WorkflowStep
                null
                null
                null
                null
                false,
            CancellationToken.None
        )
        |> ignore

        observer.OnEventAsync(
            createEnvelopeWithFailure
                AgentRunEventKind.RunFailed
                runId
                runId
                "agent.run"
                RunOperationKind.Run
                null
                null
                null
                null
                false
                failure,
            CancellationToken.None
        )
        |> ignore

        telemetry.Flush()

        let toolSpan =
            Assert.Single(telemetry.Spans |> Array.filter (fun span -> span.OperationName = "tool.one"))

        let stepSpan =
            Assert.Single(telemetry.Spans |> Array.filter (fun span -> span.OperationName = "step.one"))

        Assert.Equal(ActivityStatusCode.Error, toolSpan.Status)
        Assert.Equal(ActivityStatusCode.Error, stepSpan.Status)
        Assert.Equal("Tool", string (tryGetTag "circuit.operation.kind" toolSpan).Value)
        Assert.Equal("WorkflowStep", string (tryGetTag "circuit.operation.kind" stepSpan).Value)

        let toolMetricKeys =
            metricTags (metricByName "circuit.tools" telemetry.Metrics)
            |> Array.map _.Key
            |> Set.ofArray

        let stepMetricKeys =
            metricTags (metricByName "circuit.workflow.steps" telemetry.Metrics)
            |> Array.map _.Key
            |> Set.ofArray

        let expectedOperationKeys =
            set
                [ "circuit.definition.id"
                  "circuit.definition.version"
                  "circuit.operation.kind"
                  "circuit.status" ]

        Assert.True((toolMetricKeys = expectedOperationKeys))
        Assert.True((stepMetricKeys = expectedOperationKeys))

        let toolMetricValues =
            metricTags (metricByName "circuit.tools" telemetry.Metrics)
            |> Array.filter (fun tag -> tag.Key = "circuit.operation.kind" || tag.Key = "circuit.status")
            |> Array.map (fun tag -> string tag.Value)
            |> Set.ofArray

        let stepMetricValues =
            metricTags (metricByName "circuit.workflow.steps" telemetry.Metrics)
            |> Array.filter (fun tag -> tag.Key = "circuit.operation.kind" || tag.Key = "circuit.status")
            |> Array.map (fun tag -> string tag.Value)
            |> Set.ofArray

        let expectedToolMetricValues = set [ "Tool"; "cancelled" ]
        let expectedStepMetricValues = set [ "WorkflowStep"; "cancelled" ]

        Assert.True((toolMetricValues = expectedToolMetricValues))
        Assert.True((stepMetricValues = expectedStepMetricValues))

    [<Fact>]
    let ``open telemetry observer exports the full metric set with low-cardinality dimensions`` () =
        use telemetry = new TelemetryHarness()

        let observer = OpenTelemetryRunObserver() :> Circuit.IRunObserver

        let tool =
            createResolvedTool
                (createTestTool "tool.metric" ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(TestOutput(Text = $"tool:{input.Token}"))))
                Seq.empty

        let toolRuntime =
            createRuntimeWith
                (fun options ->
                    options.DefaultModelId <- ValueSome "model.metric"
                    options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |])
                (new FakeChatClient(
                    (fun messages _options _ct ->
                        match tryGetFunctionResult messages with
                        | Some functionResult ->
                            let output = Assert.IsType<TestOutput>(functionResult.Result)
                            Task.FromResult(jsonResponse $"{{\"text\":\"{output.Text}\"}}")
                        | None ->
                            let arguments = Dictionary<string, obj>()
                            arguments["token"] <- "metric"
                            Task.FromResult(functionCallResponse "tool-call-metric" "tool_metric_v1" arguments)),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| observer |]

        let toolResult =
            toolRuntime
                .RunAsync(
                    createAgent "Run a tool.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "metric"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.True(toolResult.Result.IsSuccess)

        let validationRuntime =
            createRuntimeWith
                (fun options -> options.DefaultModelId <- ValueSome "model.metric")
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":null}")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| observer |]

        let validationResult =
            validationRuntime
                .RunAsync(
                    createAgent "Validate output.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "validation"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        Assert.False(validationResult.Result.IsSuccess)

        let repairRuntime =
            createRuntimeWith
                (fun options -> options.DefaultModelId <- ValueSome "model.metric")
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "plain text")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                (Some(
                    new FakeChatClient(
                        (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"repaired\"}")),
                        (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                    )
                ))
                [| observer |]

        let repairResult =
            repairRuntime
                .RunAsync(
                    createAgent "Repair output.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "repair"),
                    createRunOptions None StructuredOutputPolicy.AllowSecondaryModelRepair,
                    CancellationToken.None
                )
                .Result

        Assert.True(repairResult.Result.IsSuccess)

        let workflowRuntime =
            createMafRuntimeWith
                ignore
                (new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"unused\"}")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ))
                None
                [| observer |]

        let step =
            Workflow.code "workflow.metric.step" (fun _ value -> Task.FromResult(value + 1))

        let workflow = Workflow.define "workflow.metric" "1.0.0" step

        let workflowResult =
            Workflow.run
                (workflowRuntime :> IWorkflowRuntime)
                workflow
                1
                WorkflowRunOptions.Default
                CancellationToken.None
            |> _.Result

        Assert.True(workflowResult.Result.IsSuccess)

        let requestStep =
            Workflow.request "approval.metric" (fun value -> ApprovalPrompt.Create($"approve:{value}", "Need approval"))

        let approvalDecision =
            Workflow.code "approval.metric.result" (fun _ (response: ApprovalResponse) ->
                Task.FromResult(response.Approved))

        let approvalWorkflow =
            Workflow.define "workflow.approval.metric" "1.0.0" requestStep
            |> Workflow.thenStep approvalDecision

        let approvalRun =
            Workflow.start
                (workflowRuntime :> IWorkflowRuntime)
                approvalWorkflow
                42
                WorkflowRunOptions.Default
                CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) approvalRun
            |> _.Result

        let approvalEvent =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        approvalRun
            .RespondAsync(ApprovalResponse(approvalEvent.Approval.Value.RequestId, true, null), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()

        let secondPass =
            collectUntil
                (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
                approvalRun
            |> _.Result

        Assert.Contains(secondPass, fun event -> event.Kind = RunEventKind.RunCompleted && event.Value.Value)

        telemetry.Flush()

        let metrics = telemetry.Metrics

        let expectedNames =
            set
                [ "circuit.runs"
                  "circuit.run.duration"
                  "circuit.runs.active"
                  "circuit.tools"
                  "circuit.tool.duration"
                  "circuit.workflow.steps"
                  "circuit.workflow.step.duration"
                  "circuit.validation.failures"
                  "circuit.approvals.requested"
                  "circuit.structured_output.repairs" ]

        let actualNames = metrics |> Array.map _.Name |> Set.ofArray
        Assert.True((actualNames = expectedNames))

        Assert.Equal(1L, metricSum (metricByName "circuit.tools" metrics))
        Assert.Equal(1L, metricSum (metricByName "circuit.validation.failures" metrics))
        Assert.Equal(1L, metricSum (metricByName "circuit.approvals.requested" metrics))
        Assert.Equal(1L, metricSum (metricByName "circuit.structured_output.repairs" metrics))
        Assert.True(histogramCount (metricByName "circuit.run.duration" metrics) >= 4L)
        Assert.True(histogramCount (metricByName "circuit.tool.duration" metrics) >= 1L)
        Assert.True(histogramCount (metricByName "circuit.workflow.step.duration" metrics) >= 2L)
        Assert.Equal(0L, metricSum (metricByName "circuit.runs.active" metrics))

        let expectedTagKeys =
            Map.ofList
                [ ("circuit.runs",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.run.duration",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.runs.active",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.tools",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.tool.duration",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.workflow.steps",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.workflow.step.duration",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.validation.failures",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.approvals.requested",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ])
                  ("circuit.structured_output.repairs",
                   set
                       [ "circuit.definition.id"
                         "circuit.definition.version"
                         "circuit.operation.kind"
                         "circuit.status" ]) ]

        for KeyValue(metricName, expectedKeys) in expectedTagKeys do
            let actualKeys =
                metricTags (metricByName metricName metrics)
                |> Array.map (fun (tag: Collections.Generic.KeyValuePair<string, obj>) -> tag.Key)
                |> Set.ofArray

            Assert.True((actualKeys = expectedKeys))

        let disallowedMetricKeys =
            metrics
            |> Array.collect metricTags
            |> Array.map (fun (tag: Collections.Generic.KeyValuePair<string, obj>) -> tag.Key)
            |> Array.filter (fun key -> key = "gen_ai.agent.name" || key = "gen_ai.request.model")

        Assert.Empty disallowedMetricKeys

        let statuses =
            metrics
            |> Array.collect metricTags
            |> Array.choose (fun (tag: Collections.Generic.KeyValuePair<string, obj>) ->
                if tag.Key = "circuit.status" then
                    Some(string tag.Value)
                else
                    None)
            |> Set.ofArray

        Assert.True((statuses = (set [ "in_progress"; "success"; "failure"; "requested" ])))

    [<Fact>]
    let ``open telemetry chat instrumentation does not create duplicate Circuit root spans`` () =
        use telemetry =
            new TelemetryHarness([| TelemetryContracts.ActivitySourceName; "provider-contract.chat" |])

        use providerTelemetry =
            new OpenTelemetryChatClient(
                new FakeChatClient(
                    (fun _messages _options _ct -> Task.FromResult(jsonResponse "{\"text\":\"ok\"}")),
                    (fun _messages _options _ct -> ArrayAsyncEnumerable(Array.empty))
                ),
                NullLogger.Instance,
                "provider-contract.chat"
            )

        providerTelemetry.EnableSensitiveData <- false

        let runtime =
            createRuntime providerTelemetry None [| OpenTelemetryRunObserver() :> Circuit.IRunObserver |]

        let result =
            runtime
                .RunAsync(
                    createAgent "Use instrumented telemetry.",
                    createSignature<TestOutput> (),
                    TestInput(Token = "otel"),
                    createRunOptions None StructuredOutputPolicy.NativeOnly,
                    CancellationToken.None
                )
                .Result

        telemetry.Flush()

        Assert.True(result.Result.IsSuccess)

        let circuitRoots =
            telemetry.Spans
            |> Array.filter (fun (span: Activity) ->
                span.Source.Name = TelemetryContracts.ActivitySourceName
                && span.OperationName = "agent.run")

        Assert.Single(circuitRoots) |> ignore
        Assert.Contains(telemetry.Spans, fun (span: Activity) -> span.Source.Name = "provider-contract.chat")

module MoreWorkflowStateCoverageTests =
    open Circuit.MicrosoftAgentFramework.MafWorkflows

    [<Fact>]
    let ``workflow helper states cover malformed already-complete and checkpoint-store branches`` () =
        let aggregateState = ParallelAggregateState.create<int> 1

        match
            ParallelAggregateState.capture
                1
                ({ BranchIndex = 0; Value = 5 }: WorkflowGraph.ParallelBranchResult<int>)
                aggregateState
        with
        | ParallelAggregateCapture.Complete values -> Assert.Equal<int list>([ 5 ], values)
        | other -> Assert.True(false, $"Expected Complete, got {other}.")

        match
            ParallelAggregateState.capture
                1
                ({ BranchIndex = 0; Value = 7 }: WorkflowGraph.ParallelBranchResult<int>)
                aggregateState
        with
        | ParallelAggregateCapture.AlreadyCompleted -> ()
        | other -> Assert.True(false, $"Expected AlreadyCompleted, got {other}.")

        let malformedWave = ParallelWaveCollectorState<int, int>()
        malformedWave.Received <- [| true |]
        malformedWave.Values <- Array.empty<int>

        let malformedItem = ParallelWaveItem<int, int>()
        malformedItem.IsSeed <- false

        let malformedWaveEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                ParallelWaveCollectorState.capture [| 0 |] malformedItem malformedWave |> ignore)

        Assert.Contains("malformed", malformedWaveEx.Message)

        let readyAdapter =
            BindingAndResolverCoverageTests.invokeWorkflowBinding
                "ParallelWaveReadyAdapter"
                [| typeof<int>; typeof<int> |]
                [| box "wave.pending" |]

        let pendingDispatch = ParallelWaveDispatch<int, int>()
        pendingDispatch.IsReady <- false

        let pendingEx =
            Assert.Throws<InvalidOperationException>(fun () ->
                BindingAndResolverCoverageTests.executeBinding
                    readyAdapter
                    pendingDispatch
                    (BindingAndResolverCoverageTests.WorkflowContextStub()
                    :> Microsoft.Agents.AI.Workflows.IWorkflowContext)
                |> ignore)

        Assert.Contains("unexpected pending dispatch", pendingEx.Message)
