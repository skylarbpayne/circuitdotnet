namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Circuit.Core
open Microsoft.Agents.AI

module internal MafSessionContracts =
    [<Literal>]
    let AdapterId = "circuit.microsoft-agent-framework"

    [<Literal>]
    let FormatVersion = 1

    [<Literal>]
    let private MetadataPropertyName = "metadata"

    [<Literal>]
    let private SessionBindingMetadataKey =
        "circuit.microsoft-agent-framework.session-binding"

    let private emptyMetadata =
        Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, string>

    let private appendPart (builder: StringBuilder) (name: string) (value: string) =
        builder.Append(name).Append('=').Append(value).Append('\n') |> ignore

    let private computeFingerprint (configure: StringBuilder -> unit) =
        let builder = StringBuilder()
        configure builder
        let bytes = Encoding.UTF8.GetBytes(builder.ToString())
        let hash = SHA256.HashData bytes
        Convert.ToHexStringLower hash

    let private freezeMetadata (entries: seq<KeyValuePair<string, string>>) =
        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for KeyValue(key, value) in entries do
            dictionary[key] <- value

        if dictionary.Count = 0 then
            emptyMetadata
        else
            dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

    let createSessionMetadata (bindingFingerprint: string) =
        freezeMetadata [ KeyValuePair(SessionBindingMetadataKey, bindingFingerprint) ]

    let definitionFingerprint (agent: AgentDefinition) =
        computeFingerprint (fun builder ->
            appendPart builder "id" agent.Id.Value
            appendPart builder "version" (agent.Version.ToString())
            appendPart builder "name" agent.Name
            appendPart builder "instructions" agent.Instructions

            appendPart
                builder
                "modelHint"
                (match agent.ModelHint with
                 | ValueSome value -> value
                 | ValueNone -> "")

            for toolTag in agent.ToolTags |> Seq.sort do
                appendPart builder "toolTag" toolTag

            for skill in agent.Skills do
                appendPart builder "skill" $"{skill.Id.Value}@{skill.Version}"

            for entry in agent.Metadata |> Seq.sortBy _.Key do
                appendPart builder "metadata" $"{entry.Key}={entry.Value}")

    let private signatureFingerprint<'Input, 'Output> (signature: Signature<'Input, 'Output>) =
        computeFingerprint (fun builder ->
            appendPart builder "id" signature.Id.Value
            appendPart builder "version" (signature.Version.ToString())
            appendPart builder "description" signature.Description
            appendPart builder "instructions" signature.Instructions

            appendPart
                builder
                "jsonOptions"
                (match SerializationPolicy.tryGetSemanticFingerprint signature.JsonSerializerOptions with
                 | ValueSome fingerprint -> fingerprint
                 | ValueNone -> "unstable")

            appendPart builder "inputSchema" (signature.Input.Schema.ToJsonString())
            appendPart builder "outputSchema" (signature.Output.Schema.ToJsonString()))

    let private capabilityFingerprint (tools: IReadOnlyList<ResolvedMafTool>) (skills: IReadOnlyList<ResolvedSkill>) =
        computeFingerprint (fun builder ->
            for tool in
                tools
                |> Seq.sortBy (fun tool -> tool.ModelName, tool.Tool.Name.Value, tool.Tool.Version) do
                appendPart builder "toolModelName" tool.ModelName
                appendPart builder "toolName" tool.Tool.Name.Value
                appendPart builder "toolVersion" (tool.Tool.Version.ToString())
                appendPart builder "toolDescription" tool.Tool.Description
                appendPart builder "toolApproval" (tool.Tool.Approval.ToString())

                appendPart builder "toolApprovalPolicy" (tool.Tool.ApprovalPolicy |> ValueOption.defaultValue "")

                appendPart builder "toolInputSchema" (tool.Tool.InputSchema.ToJsonString())
                appendPart builder "toolOutputSchema" (tool.Tool.OutputSchema.ToJsonString())

                for tag in tool.Tags |> Seq.sort do
                    appendPart builder "toolTag" tag

            for skill in
                skills
                |> Seq.sortBy (fun skill -> skill.Reference.Id.Value, skill.Reference.Version) do
                appendPart builder "skillId" skill.Reference.Id.Value
                appendPart builder "skillVersion" (skill.Reference.Version.ToString()))

    let createSessionBinding<'Input, 'Output>
        (runContext: RunContext)
        (signature: Signature<'Input, 'Output>)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        computeFingerprint (fun builder ->
            appendPart builder "signature" (signatureFingerprint signature)
            appendPart builder "tenantId" (runContext.Options.TenantId |> ValueOption.defaultValue "")
            appendPart builder "userId" (runContext.Options.UserId |> ValueOption.defaultValue "")
            appendPart builder "capabilities" (capabilityFingerprint tools skills))

    let private sessionIdFor (providerSession: AgentSession) =
        match providerSession with
        | :? ChatClientAgentSession as chatClientSession when
            not (String.IsNullOrWhiteSpace chatClientSession.ConversationId)
            ->
            chatClientSession.ConversationId
        | _ -> Guid.NewGuid().ToString("N")

    let createCircuitSession definition metadata providerSession =
        let sessionMetadata =
            if isNull metadata then
                emptyMetadata
            else
                freezeMetadata metadata

        CircuitSession(
            sessionIdFor providerSession,
            sessionMetadata,
            ValueSome AdapterId,
            ValueSome(definitionFingerprint definition),
            ValueSome(providerSession :> obj)
        )

    let getProviderSession (expectedFingerprint: string) (session: CircuitSession) =
        match session.AdapterId, session.DefinitionFingerprint, session.ProviderSession with
        | ValueSome adapterId, ValueSome fingerprint, ValueSome providerSession when
            StringComparer.Ordinal.Equals(adapterId, AdapterId)
            && StringComparer.Ordinal.Equals(fingerprint, expectedFingerprint)
            && (providerSession :? AgentSession)
            ->
            ValueSome(providerSession :?> AgentSession)
        | _ -> ValueNone

    let hasMatchingSessionBinding (expectedBindingFingerprint: string) (session: CircuitSession) =
        let mutable bindingFingerprint = null

        session.Metadata.TryGetValue(SessionBindingMetadataKey, &bindingFingerprint)
        && StringComparer.Ordinal.Equals(bindingFingerprint, expectedBindingFingerprint)

    let ensureMatchingSession definition (session: CircuitSession) =
        let fingerprint = definitionFingerprint definition

        match getProviderSession fingerprint session with
        | ValueSome providerSession -> providerSession
        | ValueNone ->
            invalidOp
                "The provided Circuit session does not belong to this Microsoft Agent Framework runtime or agent definition."

    let private serializeMetadata (metadata: IReadOnlyDictionary<string, string>) =
        let metadataObject = JsonObject()

        for entry in metadata |> Seq.sortBy _.Key do
            metadataObject[entry.Key] <- JsonValue.Create(entry.Value)

        metadataObject

    let serializeEnvelope
        (definitionFingerprint: string)
        (metadata: IReadOnlyDictionary<string, string>)
        (providerState: JsonElement)
        =
        let envelope = JsonObject()
        envelope["formatVersion"] <- JsonValue.Create FormatVersion
        envelope["adapter"] <- JsonValue.Create AdapterId
        envelope["definitionFingerprint"] <- JsonValue.Create definitionFingerprint

        if not (isNull metadata) && metadata.Count > 0 then
            envelope[MetadataPropertyName] <- serializeMetadata metadata

        envelope["providerState"] <- JsonNode.Parse(providerState.GetRawText())
        use document = JsonDocument.Parse(envelope.ToJsonString())
        document.RootElement.Clone()

    let private parseMetadata (state: JsonElement) =
        let mutable metadataElement = Unchecked.defaultof<JsonElement>

        if not (state.TryGetProperty(MetadataPropertyName, &metadataElement)) then
            emptyMetadata
        elif metadataElement.ValueKind <> JsonValueKind.Object then
            invalidArg "state" "The serialized Circuit session envelope metadata must be a JSON object."
        else
            let entries = ResizeArray<KeyValuePair<string, string>>()

            for property in metadataElement.EnumerateObject() do
                if property.Value.ValueKind <> JsonValueKind.String then
                    invalidArg "state" "The serialized Circuit session envelope metadata values must be strings."

                entries.Add(KeyValuePair(property.Name, property.Value.GetString()))

            freezeMetadata entries

    let parseEnvelope (expectedFingerprint: string) (state: JsonElement) =
        if state.ValueKind <> JsonValueKind.Object then
            invalidArg "state" "The serialized Circuit session envelope must be a JSON object."

        let getRequiredProperty (name: string) =
            let mutable value = Unchecked.defaultof<JsonElement>

            if state.TryGetProperty(name, &value) then
                value
            else
                invalidArg "state" $"The serialized Circuit session envelope is missing '{name}'."

        let formatVersionElement = getRequiredProperty "formatVersion"

        if
            formatVersionElement.ValueKind <> JsonValueKind.Number
            || formatVersionElement.GetInt32() <> FormatVersion
        then
            invalidArg "state" "The serialized Circuit session envelope uses an unsupported formatVersion."

        let adapterElement = getRequiredProperty "adapter"

        if adapterElement.ValueKind <> JsonValueKind.String then
            invalidArg "state" "The serialized Circuit session envelope adapter is not supported by this runtime."

        let adapter = adapterElement.GetString()

        if not (StringComparer.Ordinal.Equals(adapter, AdapterId)) then
            invalidArg "state" "The serialized Circuit session envelope adapter is not supported by this runtime."

        let fingerprintElement = getRequiredProperty "definitionFingerprint"

        if fingerprintElement.ValueKind <> JsonValueKind.String then
            invalidArg "state" "The serialized Circuit session envelope definitionFingerprint must be a string."

        let fingerprint = fingerprintElement.GetString()

        if not (StringComparer.Ordinal.Equals(fingerprint, expectedFingerprint)) then
            invalidArg "state" "The serialized Circuit session envelope does not match the supplied agent definition."

        let metadata = parseMetadata state
        let providerStateElement = getRequiredProperty "providerState"
        fingerprint, metadata, providerStateElement.Clone()
