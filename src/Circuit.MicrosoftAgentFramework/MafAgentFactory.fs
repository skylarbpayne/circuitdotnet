#nowarn "57"

namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
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

module internal MafAgentFactory =
    let private toReadOnlyList<'T> (items: 'T seq) =
        items |> Seq.toArray :> IReadOnlyList<'T>

    let private wrapApprovalIfRequired (tool: ResolvedTool) =
        if tool.RequiresApproval then
            ApprovalRequiredAIFunction(tool.Tool) :> AITool
        else
            tool.Tool :> AITool

    let private combineInstructions (agent: AgentDefinition) (signature: Signature<'Input, 'Output>) =
        if String.IsNullOrWhiteSpace signature.Instructions then
            agent.Instructions
        else
            agent.Instructions + "\n\n" + signature.Instructions

    let resolveToolsAsync
        (runtimeOptions: MafRuntimeOptions)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        =
        task {
            let! resolvedSets =
                runtimeOptions.ToolResolvers
                |> Seq.map (fun (resolver: IToolResolver) ->
                    resolver.ResolveToolsAsync(context, cancellationToken).AsTask())
                |> Task.WhenAll

            let allTools = resolvedSets |> Seq.collect id |> Seq.toArray

            if agent.ToolTags.Count = 0 then
                return allTools |> Array.distinctBy (fun tool -> tool.Name) |> toReadOnlyList
            else
                let selectedByName = Dictionary<string, ResolvedTool>(StringComparer.Ordinal)

                for requestedTag in agent.ToolTags |> Seq.sort do
                    let matches = allTools |> Array.filter (fun tool -> tool.Tags.Contains requestedTag)

                    match matches.Length with
                    | 0 -> invalidOp $"No tool was resolved for requested tag '{requestedTag}'."
                    | 1 -> selectedByName[matches[0].Name] <- matches[0]
                    | _ ->
                        let names = matches |> Array.map (fun tool -> tool.Name) |> String.concat ", "
                        invalidOp $"Multiple tools were resolved for requested tag '{requestedTag}': {names}."

                return selectedByName.Values |> Seq.toArray |> toReadOnlyList
        }

    let resolveSkillsAsync
        (runtimeOptions: MafRuntimeOptions)
        (context: RunContext)
        (agent: AgentDefinition)
        (cancellationToken: CancellationToken)
        =
        task {
            let! resolvedSets =
                runtimeOptions.SkillResolvers
                |> Seq.map (fun (resolver: ISkillResolver) ->
                    resolver.ResolveSkillsAsync(context, cancellationToken).AsTask())
                |> Task.WhenAll

            let allSkills = resolvedSets |> Seq.collect id |> Seq.toArray

            if agent.Skills.Count = 0 then
                return Array.empty<ResolvedSkill> |> toReadOnlyList
            else
                let selected = ResizeArray<ResolvedSkill>()

                for requestedSkill in agent.Skills do
                    let matches =
                        allSkills
                        |> Array.filter (fun skill ->
                            skill.Reference.Id = requestedSkill.Id
                            && skill.Reference.Version = requestedSkill.Version)

                    match matches.Length with
                    | 0 ->
                        invalidOp
                            $"No skill was resolved for requested skill '{requestedSkill.Id.Value}@{requestedSkill.Version}'."
                    | 1 -> selected.Add matches[0]
                    | _ ->
                        invalidOp
                            $"Multiple skills were resolved for requested skill '{requestedSkill.Id.Value}@{requestedSkill.Version}'."

                return selected |> Seq.toArray |> toReadOnlyList
        }

    let private createStructuredOutputRepairOptions () =
        let options = MafStructuredOutputAgentOptions()
        options.ChatClientSystemMessage <- MafStructuredOutput.RepairSystemMessage
        options

    let private createBaseAgent
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runOptions: RunOptions)
        (agent: AgentDefinition)
        (description: string)
        (instructions: string)
        (tools: IReadOnlyList<ResolvedTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        (enableSecondaryRepair: bool)
        =
        let chatOptions = ChatOptions()
        chatOptions.Instructions <- instructions

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

        agentOptions.AIContextProviders <-
            ResizeArray<AIContextProvider>(skills |> Seq.map (fun skill -> skill.Provider))

        agentOptions.EnableNonApprovalRequiredFunctionBypassing <-
            tools |> Seq.exists (fun tool -> tool.RequiresApproval)

        let innerAgent: AIAgent =
            ChatClientExtensions.AsAIAgent(chatClient, agentOptions, null, runOptions.Services) :> AIAgent

        let builder = innerAgent.AsBuilder()

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

        builder.UseOpenTelemetry() |> ignore
        builder.Build(runOptions.Services)

    let createAgent<'Input, 'Output>
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runOptions: RunOptions)
        (agent: AgentDefinition)
        (signature: Signature<'Input, 'Output>)
        (tools: IReadOnlyList<ResolvedTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        (enableSecondaryRepair: bool)
        =
        createBaseAgent
            chatClient
            runtimeOptions
            runOptions
            agent
            signature.Description
            (combineInstructions agent signature)
            tools
            skills
            enableSecondaryRepair

    let createSessionAgent
        (chatClient: IChatClient)
        (runtimeOptions: MafRuntimeOptions)
        (runOptions: RunOptions)
        (agent: AgentDefinition)
        (tools: IReadOnlyList<ResolvedTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        createBaseAgent chatClient runtimeOptions runOptions agent agent.Name agent.Instructions tools skills false
