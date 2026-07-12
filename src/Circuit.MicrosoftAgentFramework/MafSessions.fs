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

    let private emptyMetadata =
        Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, string>

    let definitionFingerprint (agent: AgentDefinition) =
        let builder = StringBuilder()

        let appendPart (name: string) (value: string) =
            builder.Append(name).Append('=').Append(value).Append('\n') |> ignore

        appendPart "id" agent.Id.Value
        appendPart "version" (agent.Version.ToString())
        appendPart "name" agent.Name
        appendPart "instructions" agent.Instructions

        appendPart
            "modelHint"
            (match agent.ModelHint with
             | ValueSome value -> value
             | ValueNone -> "")

        for toolTag in agent.ToolTags |> Seq.sort do
            appendPart "toolTag" toolTag

        for skill in agent.Skills do
            appendPart "skill" $"{skill.Id.Value}@{skill.Version}"

        for entry in agent.Metadata |> Seq.sortBy _.Key do
            appendPart "metadata" $"{entry.Key}={entry.Value}"

        let bytes = Encoding.UTF8.GetBytes(builder.ToString())
        let hash = SHA256.HashData bytes
        Convert.ToHexStringLower hash

    let private sessionIdFor (providerSession: AgentSession) =
        match providerSession with
        | :? ChatClientAgentSession as chatClientSession when
            not (String.IsNullOrWhiteSpace chatClientSession.ConversationId)
            ->
            chatClientSession.ConversationId
        | _ -> Guid.NewGuid().ToString("N")

    let createCircuitSession definition providerSession =
        CircuitSession(
            sessionIdFor providerSession,
            emptyMetadata,
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

    let ensureMatchingSession definition (session: CircuitSession) =
        let fingerprint = definitionFingerprint definition

        match getProviderSession fingerprint session with
        | ValueSome providerSession -> providerSession
        | ValueNone ->
            invalidOp
                "The provided Circuit session does not belong to this Microsoft Agent Framework runtime or agent definition."

    let serializeEnvelope (definitionFingerprint: string) (providerState: JsonElement) =
        let envelope = JsonObject()
        envelope["formatVersion"] <- JsonValue.Create FormatVersion
        envelope["adapter"] <- JsonValue.Create AdapterId
        envelope["definitionFingerprint"] <- JsonValue.Create definitionFingerprint
        envelope["providerState"] <- JsonNode.Parse(providerState.GetRawText())
        use document = JsonDocument.Parse(envelope.ToJsonString())
        document.RootElement.Clone()

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

        let providerStateElement = getRequiredProperty "providerState"
        fingerprint, providerStateElement.Clone()
