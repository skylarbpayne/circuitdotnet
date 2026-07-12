#nowarn "57"

namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Microsoft.Agents.AI
open Microsoft.Extensions.AI

[<AllowNullLiteral; Sealed>]
type internal MafStructuredOutputAgentOptions() =
    member val ChatClientSystemMessage: string = null with get, set
    member val ChatOptions: ChatOptions = null with get, set

[<Sealed>]
type internal MafStructuredOutputAgentResponse(response: ChatResponse, originalResponse: AgentResponse) =
    inherit AgentResponse(response)

    member _.OriginalResponse = originalResponse

[<Sealed>]
type internal MafStructuredOutputAgent
    (innerAgent: AIAgent, chatClient: IChatClient, agentOptions: MafStructuredOutputAgentOptions) as this =
    inherit DelegatingAIAgent(innerAgent)

    do
        if isNull chatClient then
            nullArg "chatClient"

    member private _.GetChatMessages(textResponseText: string) =
        let messages = ResizeArray<ChatMessage>()

        if
            not (isNull agentOptions)
            && not (String.IsNullOrWhiteSpace agentOptions.ChatClientSystemMessage)
        then
            messages.Add(ChatMessage(ChatRole.System, agentOptions.ChatClientSystemMessage))

        messages.Add(ChatMessage(ChatRole.User, textResponseText))
        messages :> IReadOnlyList<ChatMessage>

    member private _.GetChatOptions(options: AgentRunOptions) =
        let responseFormat =
            match options with
            | null when
                isNull agentOptions
                || isNull agentOptions.ChatOptions
                || isNull agentOptions.ChatOptions.ResponseFormat
                ->
                invalidOp
                    $"A response format of type '{nameof ChatResponseFormatJson}' must be specified, but none was specified."
            | null -> agentOptions.ChatOptions.ResponseFormat
            | options when not (isNull options.ResponseFormat) -> options.ResponseFormat
            | _ when
                isNull agentOptions
                || isNull agentOptions.ChatOptions
                || isNull agentOptions.ChatOptions.ResponseFormat
                ->
                invalidOp
                    $"A response format of type '{nameof ChatResponseFormatJson}' must be specified, but none was specified."
            | _ -> agentOptions.ChatOptions.ResponseFormat

        match responseFormat with
        | :? ChatResponseFormatJson as jsonResponseFormat ->
            let chatOptions =
                if isNull agentOptions || isNull agentOptions.ChatOptions then
                    ChatOptions()
                else
                    agentOptions.ChatOptions.Clone()

            chatOptions.ResponseFormat <- jsonResponseFormat
            chatOptions
        | null ->
            invalidOp
                $"A response format of type '{nameof ChatResponseFormatJson}' must be specified, but none was specified."
        | responseFormat ->
            raise (
                NotSupportedException(
                    $"A response format of type '{nameof ChatResponseFormatJson}' must be specified, but was '{responseFormat.GetType().Name}'."
                )
            )

    override _.RunCoreAsync(messages, session, options, cancellationToken) =
        task {
            let! textResponse = innerAgent.RunAsync(messages, session, options, cancellationToken)

            let! structuredOutputResponse =
                chatClient.GetResponseAsync(
                    this.GetChatMessages(textResponse.Text),
                    this.GetChatOptions(options),
                    cancellationToken
                )

            return MafStructuredOutputAgentResponse(structuredOutputResponse, textResponse) :> AgentResponse
        }

[<AbstractClass; Sealed; Extension>]
type internal MafStructuredOutputAIAgentBuilderExtensions =
    [<Extension>]
    static member UseStructuredOutput(builder: AIAgentBuilder, chatClient: IChatClient) =
        MafStructuredOutputAIAgentBuilderExtensions.UseStructuredOutput(builder, chatClient, null)

    [<Extension>]
    static member UseStructuredOutput
        (builder: AIAgentBuilder, chatClient: IChatClient, optionsFactory: Func<MafStructuredOutputAgentOptions>)
        =
        if isNull builder then
            nullArg "builder"

        builder.Use(fun innerAgent services ->
            let activeChatClient =
                if not (isNull chatClient) then
                    chatClient
                elif isNull services then
                    null
                else
                    services.GetService(typeof<IChatClient>) :?> IChatClient

            if isNull activeChatClient then
                invalidOp
                    $"No {nameof IChatClient} was provided and none could be resolved from the service provider. Either provide an {nameof IChatClient} explicitly or register one in the dependency injection container."

            let options =
                if isNull optionsFactory then
                    null
                else
                    optionsFactory.Invoke()

            MafStructuredOutputAgent(innerAgent, activeChatClient, options) :> AIAgent)

module internal MafStructuredOutput =
    [<Literal>]
    let RepairSystemMessage =
        "Convert the provided assistant output into JSON that exactly matches the requested schema. Return only valid JSON."

    let rec private tryGetStructuredOutputResponseFromRaw (rawRepresentation: obj) =
        match rawRepresentation with
        | null -> ValueNone
        | :? MafStructuredOutputAgentResponse as response -> ValueSome response
        | :? AgentResponse as response -> tryGetStructuredOutputResponseFromRaw response.RawRepresentation
        | _ -> ValueNone

    let tryGetStructuredOutputResponse (response: AgentResponse) =
        if isNull (box response) then
            ValueNone
        else
            tryGetStructuredOutputResponseFromRaw response.RawRepresentation

    let wasRepaired (response: AgentResponse) =
        tryGetStructuredOutputResponse response |> ValueOption.isSome

    let tryGetOriginalResponseText (response: AgentResponse) =
        match tryGetStructuredOutputResponse response with
        | ValueSome structuredOutputResponse when
            not (String.IsNullOrEmpty structuredOutputResponse.OriginalResponse.Text)
            ->
            ValueSome structuredOutputResponse.OriginalResponse.Text
        | _ -> ValueNone

    let tryGetOriginalUsage (response: AgentResponse) =
        match tryGetStructuredOutputResponse response with
        | ValueSome structuredOutputResponse when not (isNull structuredOutputResponse.OriginalResponse.Usage) ->
            ValueSome structuredOutputResponse.OriginalResponse.Usage
        | _ -> ValueNone

    let removeResponseFormat (options: AgentRunOptions) =
        match options with
        | null -> null
        | options ->
            let clone = options.Clone()
            clone.ResponseFormat <- null
            clone

type internal SanitizedToolException(message: string, innerException: exn) =
    inherit InvalidOperationException(message, innerException)

    new(message: string) = SanitizedToolException(message, null)

[<AllowNullLiteral; Sealed>]
type private PendingApprovalSnapshot() =
    member val RequestId: string = null with get, set
    member val CallId: string = null with get, set
    member val ToolName: string = null with get, set
    member val ArgumentsJson: string = null with get, set

module private MafApprovalResponses =
    [<Literal>]
    let StateBagKey = "circuit.pending-tool-approval-requests"

    let private trySerializeArguments (arguments: IDictionary<string, obj>) (jsonOptions: JsonSerializerOptions) =
        if isNull arguments then
            ValueNone
        else
            try
                ValueSome(JsonSerializer.Serialize(arguments, jsonOptions))
            with _ ->
                ValueNone

    let private tryCreateSnapshot (approvalRequest: ToolApprovalRequestContent) (jsonOptions: JsonSerializerOptions) =
        match approvalRequest.ToolCall with
        | :? FunctionCallContent as functionCall ->
            match trySerializeArguments functionCall.Arguments jsonOptions with
            | ValueSome argumentsJson ->
                ValueSome(
                    PendingApprovalSnapshot(
                        RequestId = approvalRequest.RequestId,
                        CallId = functionCall.CallId,
                        ToolName = functionCall.Name,
                        ArgumentsJson = argumentsJson
                    )
                )
            | ValueNone when isNull functionCall.Arguments ->
                ValueSome(
                    PendingApprovalSnapshot(
                        RequestId = approvalRequest.RequestId,
                        CallId = functionCall.CallId,
                        ToolName = functionCall.Name,
                        ArgumentsJson = null
                    )
                )
            | ValueNone -> ValueNone
        | _ -> ValueNone

    let private tryDeserializeArguments (snapshot: PendingApprovalSnapshot) (jsonOptions: JsonSerializerOptions) =
        if isNull snapshot || isNull snapshot.ArgumentsJson then
            ValueSome null
        else
            try
                let arguments =
                    JsonSerializer.Deserialize<Dictionary<string, obj>>(snapshot.ArgumentsJson, jsonOptions)

                ValueSome(arguments :> IDictionary<string, obj>)
            with _ ->
                ValueNone

    let private tryCreateStoredToolCall (snapshot: PendingApprovalSnapshot) (jsonOptions: JsonSerializerOptions) =
        match tryDeserializeArguments snapshot jsonOptions with
        | ValueSome arguments -> ValueSome(FunctionCallContent(snapshot.CallId, snapshot.ToolName, arguments))
        | ValueNone -> ValueNone

    let private matchesStoredSnapshot
        (snapshot: PendingApprovalSnapshot)
        (response: ToolApprovalResponseContent)
        (jsonOptions: JsonSerializerOptions)
        =
        match response.ToolCall with
        | :? FunctionCallContent as functionCall ->
            let expectedArguments =
                if isNull snapshot then ValueNone
                elif isNull snapshot.ArgumentsJson then ValueSome null
                else ValueSome snapshot.ArgumentsJson

            let actualArguments =
                trySerializeArguments functionCall.Arguments jsonOptions |> ValueOption.toObj

            let expectedArguments = expectedArguments |> ValueOption.toObj

            StringComparer.Ordinal.Equals(response.RequestId, snapshot.RequestId)
            && StringComparer.Ordinal.Equals(functionCall.CallId, snapshot.CallId)
            && StringComparer.Ordinal.Equals(functionCall.Name, snapshot.ToolName)
            && StringComparer.Ordinal.Equals(actualArguments, expectedArguments)
        | _ -> false

    type private PendingApprovalStore(session: AgentSession) as this =
        let gate = obj ()

        let pendingApprovals =
            Dictionary<string, PendingApprovalSnapshot>(StringComparer.Ordinal)

        let mutable loaded = false

        member private _.Persist(jsonOptions: JsonSerializerOptions) =
            session.StateBag.SetValue(
                StateBagKey,
                pendingApprovals.Values |> Seq.sortBy _.RequestId |> Seq.toArray,
                jsonOptions
            )

        member private _.EnsureLoaded(jsonOptions: JsonSerializerOptions) =
            if not loaded then
                let mutable needsCleanup = false
                let mutable stored = Array.empty<PendingApprovalSnapshot>

                if session.StateBag.TryGetValue(StateBagKey, &stored, jsonOptions) then
                    for snapshot in stored do
                        if isNull snapshot || String.IsNullOrWhiteSpace snapshot.RequestId then
                            needsCleanup <- true
                        else
                            if pendingApprovals.ContainsKey snapshot.RequestId then
                                needsCleanup <- true

                            pendingApprovals[snapshot.RequestId] <- snapshot

                loaded <- true

                if needsCleanup then
                    this.Persist(jsonOptions)

        member this.Filter(messages: IEnumerable<ChatMessage>, jsonOptions: JsonSerializerOptions) =
            lock gate (fun () ->
                this.EnsureLoaded(jsonOptions)

                let filteredMessages = ResizeArray<ChatMessage>()
                let mutable changed = false
                let mutable droppedInvalidResponses = false

                for message in messages do
                    if isNull message.Contents then
                        filteredMessages.Add message
                    else
                        let filteredContents = ResizeArray<AIContent>()
                        let mutable messageChanged = false

                        for content in message.Contents do
                            match content with
                            | :? ToolApprovalResponseContent as approvalResponse ->
                                match pendingApprovals.TryGetValue approvalResponse.RequestId with
                                | true, snapshot when matchesStoredSnapshot snapshot approvalResponse jsonOptions ->
                                    match tryCreateStoredToolCall snapshot jsonOptions with
                                    | ValueSome storedToolCall ->
                                        filteredContents.Add(
                                            ToolApprovalResponseContent(
                                                snapshot.RequestId,
                                                approvalResponse.Approved,
                                                storedToolCall
                                            )
                                            :> AIContent
                                        )

                                        pendingApprovals.Remove snapshot.RequestId |> ignore
                                        changed <- true
                                        messageChanged <- true
                                    | ValueNone ->
                                        pendingApprovals.Remove snapshot.RequestId |> ignore
                                        changed <- true
                                        messageChanged <- true
                                        droppedInvalidResponses <- true
                                | _ ->
                                    changed <- true
                                    messageChanged <- true
                                    droppedInvalidResponses <- true
                            | _ -> filteredContents.Add content

                        if filteredContents.Count > 0 then
                            if messageChanged || filteredContents.Count <> message.Contents.Count then
                                let clone = message.Clone()
                                clone.Contents <- filteredContents :> IList<AIContent>
                                filteredMessages.Add clone
                            else
                                filteredMessages.Add message

                if changed then
                    this.Persist(jsonOptions)

                (filteredMessages :> IReadOnlyList<ChatMessage>), droppedInvalidResponses)

        member this.Capture(response: AgentResponse, jsonOptions: JsonSerializerOptions) =
            lock gate (fun () ->
                this.EnsureLoaded(jsonOptions)

                let mutable changed = false

                for message in response.Messages do
                    if not (isNull message.Contents) then
                        for content in message.Contents do
                            match content with
                            | :? ToolApprovalRequestContent as approvalRequest ->
                                match tryCreateSnapshot approvalRequest jsonOptions with
                                | ValueSome snapshot ->
                                    pendingApprovals[snapshot.RequestId] <- snapshot
                                    changed <- true
                                | ValueNone -> ()
                            | _ -> ()

                if changed then
                    this.Persist(jsonOptions))

    let private stores = ConditionalWeakTable<AgentSession, PendingApprovalStore>()

    let private createStore =
        ConditionalWeakTable<AgentSession, PendingApprovalStore>.CreateValueCallback(fun session ->
            PendingApprovalStore(session))

    let private getStore (session: AgentSession) = stores.GetValue(session, createStore)

    let filterInboundApprovalResponses
        (messages: IEnumerable<ChatMessage>)
        (session: AgentSession)
        (jsonOptions: JsonSerializerOptions)
        =
        if isNull session then
            (messages |> Seq.toArray :> IReadOnlyList<ChatMessage>), false
        else
            (getStore session).Filter(messages, jsonOptions)

    let captureOutboundApprovalRequests
        (response: AgentResponse)
        (session: AgentSession)
        (jsonOptions: JsonSerializerOptions)
        =
        if not (isNull session) && not (isNull response) && not (isNull response.Messages) then
            (getStore session).Capture(response, jsonOptions)

type internal CircuitResolvedToolFunction
    (
        tool: Circuit.Core.ResolvedTool,
        modelName: string,
        serializerOptions: JsonSerializerOptions,
        runContext: RunContext
    ) =
    inherit AIFunction()

    let jsonOptions = JsonSerializerOptions(serializerOptions)

    let emptyAdditionalProperties =
        Dictionary<string, obj>(StringComparer.Ordinal) :> IReadOnlyDictionary<string, obj>

    do jsonOptions.MakeReadOnly()

    member private _.CreateToolContext(cancellationToken: CancellationToken) =
        ToolContext(
            runContext.RunId,
            runContext.Options.TenantId,
            runContext.Options.UserId,
            runContext.Options.Services,
            cancellationToken
        )

    member private _.DeserializeInput(arguments: AIFunctionArguments, cancellationToken: CancellationToken) =
        try
            let payload = JsonSerializer.Serialize(arguments, jsonOptions)
            JsonSerializer.Deserialize(payload, tool.InputType, jsonOptions)
        with
        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
            raise (OperationCanceledException(cancellationToken))
        | :? OperationCanceledException -> reraise ()
        | ex -> raise (SanitizedToolException("Tool input could not be parsed.", ex))

    member private _.FormatValidationIssues(issues: IReadOnlyList<ValidationIssue>) =
        if isNull issues || issues.Count = 0 then
            "Validation failed."
        else
            issues
            |> Seq.map (fun issue -> $"{issue.Path}: {issue.Message}")
            |> String.concat "; "
            |> fun details -> $"Validation failed: {details}"

    member private _.TryGetOperationId(arguments: AIFunctionArguments) =
        let tryGetFromPair (pair: KeyValuePair<obj, obj>) =
            match pair.Key with
            | :? string as keyName when StringComparer.OrdinalIgnoreCase.Equals(keyName, "callId") ->
                match pair.Value with
                | :? string as callId when not (String.IsNullOrWhiteSpace callId) -> ValueSome callId
                | _ -> ValueNone
            | _ ->
                match pair.Value with
                | :? FunctionCallContent as functionCall when not (String.IsNullOrWhiteSpace functionCall.CallId) ->
                    ValueSome functionCall.CallId
                | _ -> ValueNone

        if isNull arguments then
            ValueNone
        elif not (isNull arguments.Context) then
            arguments.Context
            |> Seq.tryPick (fun pair -> tryGetFromPair pair |> ValueOption.toOption)
            |> Option.toValueOption
        else
            ValueNone

    member private _.SerializeArguments(arguments: AIFunctionArguments) =
        try
            ValueSome(JsonSerializer.Serialize(arguments, jsonOptions))
        with _ ->
            ValueNone

    member private _.CreateObserverFailure (operationId: string) (ex: exn) =
        match ex with
        | :? OperationCanceledException as cancelled ->
            CircuitFailure(
                CircuitFailureCode.Cancelled,
                "The tool was cancelled.",
                ValueSome runContext.RunId,
                ValueSome operationId,
                ValueNone,
                ValueSome cancelled
            )
        | :? SanitizedToolException as sanitized when
            sanitized.Message.StartsWith("Validation failed", StringComparison.Ordinal)
            ->
            CircuitFailure(
                CircuitFailureCode.Validation,
                sanitized.Message,
                ValueSome runContext.RunId,
                ValueSome operationId,
                ValueNone,
                ValueSome sanitized
            )
        | :? SanitizedToolException as sanitized when
            StringComparer.Ordinal.Equals(sanitized.Message, "Tool input could not be parsed.")
            ->
            CircuitFailure(
                CircuitFailureCode.Decode,
                sanitized.Message,
                ValueSome runContext.RunId,
                ValueSome operationId,
                ValueNone,
                ValueSome sanitized
            )
        | :? SanitizedToolException as sanitized ->
            CircuitFailure(
                CircuitFailureCode.Tool,
                sanitized.Message,
                ValueSome runContext.RunId,
                ValueSome operationId,
                ValueNone,
                ValueSome sanitized
            )
        | other ->
            CircuitFailure(
                CircuitFailureCode.Tool,
                "Tool execution failed.",
                ValueSome runContext.RunId,
                ValueSome operationId,
                ValueNone,
                ValueSome other
            )

    override _.Name = modelName
    override _.Description = tool.Description
    override _.AdditionalProperties = emptyAdditionalProperties
    override _.JsonSchema = tool.InputSchema.RootElement
    override _.ReturnJsonSchema = Nullable tool.OutputSchema.RootElement
    override _.UnderlyingMethod = null
    override _.JsonSerializerOptions = jsonOptions

    override this.InvokeCoreAsync(arguments: AIFunctionArguments, cancellationToken: CancellationToken) =
        task {
            let operationId =
                this.TryGetOperationId(arguments)
                |> ValueOption.defaultWith (fun () -> $"{tool.Name.Value}:{Guid.NewGuid():N}")

            do!
                MafObserver.notifyToolStartedAsync
                    runContext.RunId
                    operationId
                    tool.Name.Value
                    (this.SerializeArguments(arguments))
                    cancellationToken

            try
                cancellationToken.ThrowIfCancellationRequested()

                let input = this.DeserializeInput(arguments, cancellationToken)
                let inputIssues = tool.ValidateInput input

                if inputIssues.Count > 0 then
                    raise (SanitizedToolException(this.FormatValidationIssues(inputIssues)))

                cancellationToken.ThrowIfCancellationRequested()

                let! output =
                    task {
                        try
                            return! tool.InvokeAsync(this.CreateToolContext(cancellationToken), input)
                        with
                        | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                            return raise (OperationCanceledException(cancellationToken))
                        | ex -> return raise (SanitizedToolException("Tool execution failed.", ex))
                    }

                let outputIssues = tool.ValidateOutput output

                if outputIssues.Count > 0 then
                    raise (SanitizedToolException(this.FormatValidationIssues(outputIssues)))

                do!
                    MafObserver.notifyToolCompletedAsync
                        runContext.RunId
                        operationId
                        tool.Name.Value
                        ValueNone
                        cancellationToken

                return output
            with ex ->
                do!
                    MafObserver.notifyToolCompletedAsync
                        runContext.RunId
                        operationId
                        tool.Name.Value
                        (ValueSome(this.CreateObserverFailure (operationId) ex))
                        cancellationToken

                return raise ex
        }
        |> ValueTask<obj>

type internal ToolResolverFailureException(innerException: exn) =
    inherit InvalidOperationException("Tool resolver failed.", innerException)

type internal ToolCapabilityFailureException(failure: CircuitFailure) =
    inherit InvalidOperationException(failure.Message, failure.Exception |> ValueOption.defaultValue null)

    member _.Failure = failure

module internal MafAgentFactory =
    let private createToolCapabilityFailure runId message =
        CircuitFailure(CircuitFailureCode.Tool, message, ValueSome runId, ValueNone, ValueNone, ValueNone)

    let private toReadOnlyList<'T> (items: 'T seq) =
        items |> Seq.toArray :> IReadOnlyList<'T>

    let private toModelFacingToolName (tool: Circuit.Core.ResolvedTool) =
        let baseName = tool.Name.Value.Replace('.', '_').Replace('-', '_')

        $"{baseName}_v{tool.Version.Value.Major}"

    let private wrapApprovalIfRequired (tool: ResolvedMafTool) =
        if tool.RequiresApproval then
            ApprovalRequiredAIFunction(tool.MafFunction) :> AITool
        else
            tool.MafFunction :> AITool

    let private combineInstructions (agent: AgentDefinition) (signature: Signature<'Input, 'Output>) =
        if String.IsNullOrWhiteSpace signature.Instructions then
            agent.Instructions
        else
            agent.Instructions + "\n\n" + signature.Instructions

    let private createToolFunction
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (tool: Circuit.Core.ResolvedTool)
        =
        let modelName = toModelFacingToolName tool

        let mafFunction =
            CircuitResolvedToolFunction(tool, modelName, runtimeOptions.JsonSerializerOptions, runContext)

        ResolvedMafTool(tool, modelName, mafFunction)

    let private createToolResolutionContext (context: RunContext) =
        ToolResolutionContext(context.RunId, context.Options.TenantId, context.Options.UserId, context.Options.Services)

    let private ensureDistinctModelNames (runId: RunId) (tools: ResolvedMafTool[]) =
        let modelNames = Dictionary<string, string>(StringComparer.Ordinal)

        for tool in tools do
            match modelNames.TryGetValue tool.ModelName with
            | true, existingIdentity ->
                raise (
                    ToolCapabilityFailureException(
                        createToolCapabilityFailure
                            runId
                            $"Multiple tools map to the same model-facing identity '{tool.ModelName}': {existingIdentity}, {tool.Tool.Name.Value}@{tool.Tool.Version}."
                    )
                )
            | false, _ -> modelNames[tool.ModelName] <- $"{tool.Tool.Name.Value}@{tool.Tool.Version}"

    let resolveToolsAsync
        (runtimeOptions: MafRuntimeOptions)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        =
        task {
            let! resolvedTools =
                task {
                    try
                        return!
                            ToolResolution.resolveAllAsync
                                runtimeOptions.ToolResolvers
                                (createToolResolutionContext context)
                                cancellationToken
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException(cancellationToken))
                    | ex -> return raise (ToolResolverFailureException(ex))
                }

            let selectedTools =
                if agent.ToolTags.Count = 0 then
                    resolvedTools |> Seq.toArray
                else
                    let selectedByName =
                        Dictionary<string, Circuit.Core.ResolvedTool>(StringComparer.Ordinal)

                    for requestedTag in agent.ToolTags |> Seq.sort do
                        let matches =
                            resolvedTools
                            |> Seq.filter (fun tool -> tool.Tags.Contains requestedTag)
                            |> Seq.toArray

                        match matches.Length with
                        | 0 ->
                            raise (
                                ToolCapabilityFailureException(
                                    createToolCapabilityFailure
                                        context.RunId
                                        $"No tool was resolved for requested tag '{requestedTag}'."
                                )
                            )
                        | 1 -> selectedByName[matches[0].Name.Value] <- matches[0]
                        | _ ->
                            let names = matches |> Array.map (fun tool -> tool.Name.Value) |> String.concat ", "

                            raise (
                                ToolCapabilityFailureException(
                                    createToolCapabilityFailure
                                        context.RunId
                                        $"Multiple tools were resolved for requested tag '{requestedTag}': {names}."
                                )
                            )

                    selectedByName.Values |> Seq.toArray

            let mappedTools =
                selectedTools |> Array.map (createToolFunction runtimeOptions context)

            ensureDistinctModelNames context.RunId mappedTools
            return mappedTools :> IReadOnlyList<ResolvedMafTool>
        }

    let resolveSkillsAsync
        (runtimeOptions: MafRuntimeOptions)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        =
        task {
            let skillContext =
                SkillResolutionContext(
                    context.RunId,
                    context.Options.TenantId,
                    context.Options.UserId,
                    context.Options.Services
                )

            let! allSkills =
                SkillResolution.resolveAllAsync runtimeOptions.SkillResolvers skillContext cancellationToken

            if agent.Skills.Count = 0 then
                return Array.empty<ResolvedSkill> |> toReadOnlyList
            else
                let selected = ResizeArray<ResolvedSkill>()

                for requestedSkill in agent.Skills do
                    let matches =
                        allSkills
                        |> Seq.filter (fun skill ->
                            skill.Reference.Id = requestedSkill.Id
                            && skill.Reference.Version = requestedSkill.Version)
                        |> Seq.toArray

                    match matches.Length with
                    | 0 ->
                        invalidOp
                            $"No skill was resolved for requested skill '{requestedSkill.Id.Value}@{requestedSkill.Version}'."
                    | 1 -> selected.Add(MafSkillAdapter.prepareResolvedSkill matches[0])
                    | _ ->
                        invalidOp
                            $"Multiple skills were resolved for requested skill '{requestedSkill.Id.Value}@{requestedSkill.Version}'."

                return selected |> Seq.toArray |> toReadOnlyList
        }

    let private createStructuredOutputRepairOptions () =
        let options = MafStructuredOutputAgentOptions()
        options.ChatClientSystemMessage <- MafStructuredOutput.RepairSystemMessage
        options

    let private createToolApprovalOptions
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (tools: IReadOnlyList<ResolvedMafTool>)
        =
        let policyTools =
            tools
            |> Seq.choose (fun tool ->
                match tool.Tool.Approval, tool.Tool.ApprovalPolicy with
                | ApprovalMode.ByPolicy, ValueSome policyName -> Some(tool.ModelName, policyName, tool.Tool)
                | _ -> None)
            |> Seq.toArray

        if not (tools |> Seq.exists (fun tool -> tool.RequiresApproval)) then
            ValueNone
        else
            let toolApprovalOptions = ToolApprovalAgentOptions()
            toolApprovalOptions.JsonSerializerOptions <- runtimeOptions.JsonSerializerOptions

            match runtimeOptions.ToolApprovalPolicy, policyTools.Length with
            | ValueSome approvalPolicy, _ when policyTools.Length > 0 ->
                let policyToolsByModelName =
                    Dictionary<string, struct (string * Circuit.Core.ResolvedTool)>(StringComparer.Ordinal)

                for modelName, policyName, tool in policyTools do
                    policyToolsByModelName[modelName] <- struct (policyName, tool)

                toolApprovalOptions.AutoApprovalRules <-
                    [| Func<FunctionCallContent, ValueTask<bool>>(fun functionCall ->
                           task {
                               match policyToolsByModelName.TryGetValue functionCall.Name with
                               | true, struct (policyName, tool) ->
                                   let argumentsDictionary = Dictionary<string, obj>(StringComparer.Ordinal)

                                   if not (isNull functionCall.Arguments) then
                                       for KeyValue(key, value) in functionCall.Arguments do
                                           argumentsDictionary[key] <- value

                                   let arguments = argumentsDictionary :> IReadOnlyDictionary<string, obj>

                                   try
                                       let context = ToolApprovalContext(runContext, tool, arguments)
                                       let! approved = approvalPolicy.IsApprovedAsync(policyName, context).AsTask()
                                       return approved
                                   with _ ->
                                       return false
                               | false, _ -> return false
                           }
                           |> ValueTask<bool>) |]
            | _ -> ()

            ValueSome toolApprovalOptions

    type internal MafCompiledAgent(agent: AIAgent, skillAttachment: MafSkillProviderAttachment voption) =
        member _.Agent = agent
        member _.SkillAttachment = skillAttachment

        interface IDisposable with
            member _.Dispose() =
                match box agent with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ()

                MafSkillAdapter.dispose skillAttachment

    let private createBaseAgent
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (agent: AgentDefinition)
        (description: string)
        (instructions: string)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        (enableSecondaryRepair: bool)
        =
        let skillAttachment =
            MafSkillAdapter.createAttachment runtimeOptions runContext skills

        try
            let hasApprovalFlow =
                (tools |> Seq.exists (fun tool -> tool.RequiresApproval))
                || skillAttachment.IsSome

            let skillProviders = MafSkillAdapter.getProviders skillAttachment

            let effectiveChatClient =
                if skillProviders.Count = 0 then
                    chatClient
                else
                    let chatClientBuilder = ChatClientBuilder(chatClient)
                    chatClientBuilder.UseAIContextProviders(skillProviders |> Seq.toArray) |> ignore
                    chatClientBuilder.Build(runContext.Options.Services)

            let effectiveInstructions =
                if skillAttachment.IsSome then
                    $"{instructions}\n\n{{skills}}"
                else
                    instructions

            let chatOptions = ChatOptions()
            chatOptions.Instructions <- effectiveInstructions

            match agent.ModelHint, runtimeOptions.DefaultModelId with
            | ValueSome modelId, _ -> chatOptions.ModelId <- modelId
            | ValueNone, ValueSome modelId -> chatOptions.ModelId <- modelId
            | ValueNone, ValueNone -> ()

            if tools.Count > 0 then
                chatOptions.Tools <- ResizeArray<AITool>(tools |> Seq.map wrapApprovalIfRequired) :> IList<AITool>

            let agentOptions = ChatClientAgentOptions()
            agentOptions.Id <- agent.Id.Value
            agentOptions.Name <- agent.Name
            agentOptions.Description <- description
            agentOptions.ChatOptions <- chatOptions
            agentOptions.EnableNonApprovalRequiredFunctionBypassing <- hasApprovalFlow

            let innerAgent: AIAgent =
                ChatClientExtensions.AsAIAgent(effectiveChatClient, agentOptions, null, runContext.Options.Services)
                :> AIAgent

            let builder = innerAgent.AsBuilder()

            match createToolApprovalOptions runtimeOptions runContext tools, skillAttachment with
            | ValueSome toolApprovalOptions, _ -> builder.UseToolApproval(toolApprovalOptions) |> ignore
            | ValueNone, ValueSome _ ->
                let toolApprovalOptions = ToolApprovalAgentOptions()
                toolApprovalOptions.JsonSerializerOptions <- runtimeOptions.JsonSerializerOptions
                builder.UseToolApproval(toolApprovalOptions) |> ignore
            | ValueNone, ValueNone -> ()

            if enableSecondaryRepair then
                match runtimeOptions.SecondaryStructuredOutputClient with
                | ValueSome secondaryClient ->
                    builder.UseStructuredOutput(secondaryClient, Func<_>(createStructuredOutputRepairOptions))
                    |> ignore

                    builder.Use(
                        (fun messages session options nextAgent cancellationToken ->
                            nextAgent.RunAsync(
                                messages,
                                session,
                                MafStructuredOutput.removeResponseFormat options,
                                cancellationToken
                            )),
                        null
                    )
                    |> ignore
                | ValueNone -> invalidOp "Structured output repair requires a secondary structured output chat client."

            if hasApprovalFlow then
                builder.Use(
                    (fun messages session options nextAgent cancellationToken ->
                        task {
                            let filteredMessages, droppedInvalidResponses =
                                MafApprovalResponses.filterInboundApprovalResponses
                                    messages
                                    session
                                    runtimeOptions.JsonSerializerOptions

                            if droppedInvalidResponses && filteredMessages.Count = 0 then
                                return AgentResponse(Array.empty<ChatMessage> :> IList<ChatMessage>)
                            else
                                let! response =
                                    nextAgent.RunAsync(filteredMessages, session, options, cancellationToken)

                                MafApprovalResponses.captureOutboundApprovalRequests
                                    response
                                    session
                                    runtimeOptions.JsonSerializerOptions

                                return response
                        }),
                    null
                )
                |> ignore

            builder.UseOpenTelemetry() |> ignore
            new MafCompiledAgent(builder.Build(runContext.Options.Services), skillAttachment)
        with _ ->
            MafSkillAdapter.dispose skillAttachment
            reraise ()

    let createAgent<'Input, 'Output>
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        (enableSecondaryRepair: bool)
        =
        createBaseAgent
            chatClient
            runtimeOptions
            runContext
            agent
            signature.Description
            (combineInstructions agent signature)
            tools
            skills
            enableSecondaryRepair

    let createSessionAgent
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runContext: RunContext)
        (agent: AgentDefinition)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        createBaseAgent chatClient runtimeOptions runContext agent agent.Name agent.Instructions tools skills false
