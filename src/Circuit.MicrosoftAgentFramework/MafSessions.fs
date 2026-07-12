namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections
open System.Collections.Frozen
open System.Collections.Generic
open System.Globalization
open System.IO
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

    let private sortStringsOrdinal (values: seq<string>) =
        values
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private sortMetadataOrdinal (entries: seq<KeyValuePair<string, string>>) =
        entries
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Key, right.Key))

    let private sortPropertiesOrdinal (entries: seq<KeyValuePair<string, obj>>) =
        entries
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Key, right.Key))

    let private addString (target: JsonObject) (name: string) (value: string) =
        target[name] <-
            if isNull value then
                null
            else
                JsonValue.Create value :> JsonNode

    let private addInt64 (target: JsonObject) (name: string) (value: int64) =
        target[name] <- JsonValue.Create value :> JsonNode

    let private addNode (target: JsonObject) (name: string) (value: JsonNode) = target[name] <- value

    let rec private writeCanonicalJson (writer: Utf8JsonWriter) (node: JsonNode) =
        if isNull node then
            writer.WriteNullValue()
        else
            match node with
            | :? JsonObject as jsonObject ->
                writer.WriteStartObject()

                for entry in jsonObject |> Seq.sortBy _.Key do
                    writer.WritePropertyName(entry.Key)
                    writeCanonicalJson writer entry.Value

                writer.WriteEndObject()
            | :? JsonArray as jsonArray ->
                writer.WriteStartArray()

                for item in jsonArray do
                    writeCanonicalJson writer item

                writer.WriteEndArray()
            | _ -> node.WriteTo(writer)

    let private canonicalJsonBytes (node: JsonNode) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writeCanonicalJson writer node
        writer.Flush()
        stream.ToArray()

    let private canonicalizeJsonElement (element: JsonElement) =
        match JsonNode.Parse(element.GetRawText()) with
        | null -> null
        | node -> node

    let private hasGenericInterface (genericDefinition: Type) (valueType: Type) =
        valueType.GetInterfaces()
        |> Array.exists (fun interfaceType ->
            interfaceType.IsGenericType
            && interfaceType.GetGenericTypeDefinition() = genericDefinition)

    let private canonicalJsonText (node: JsonNode) =
        canonicalJsonBytes node |> Encoding.UTF8.GetString

    let private tryGetDictionaryEntry (item: obj) =
        if isNull item then
            ValueNone
        else
            match item with
            | :? DictionaryEntry as entry -> ValueSome(entry.Key, entry.Value)
            | _ ->
                let itemType = item.GetType()

                if
                    itemType.IsGenericType
                    && itemType.GetGenericTypeDefinition() = typedefof<KeyValuePair<_, _>>
                then
                    let key = itemType.GetProperty("Key").GetValue(item)
                    let value = itemType.GetProperty("Value").GetValue(item)
                    ValueSome(key, value)
                else
                    ValueNone

    let private isOrderedCollectionType (valueType: Type) =
        valueType.IsArray
        || typeof<IList>.IsAssignableFrom valueType
        || hasGenericInterface typedefof<IReadOnlyList<_>> valueType

    let private isDictionaryType (valueType: Type) =
        typeof<IDictionary>.IsAssignableFrom valueType
        || hasGenericInterface typedefof<IReadOnlyDictionary<_, _>> valueType

    let private isSetType (valueType: Type) =
        hasGenericInterface typedefof<ISet<_>> valueType
        || hasGenericInterface typedefof<IReadOnlySet<_>> valueType

    let private maxSkillPropertyDepth = 32
    let private maxSkillPropertyNodes = 4096

    type private SkillPropertyFingerprintContext =
        { Active: HashSet<obj>
          mutable RemainingNodes: int }

    let private createSkillPropertyFingerprintContext () =
        { Active = HashSet<obj>(ReferenceEqualityComparer.Instance)
          RemainingNodes = maxSkillPropertyNodes }

    let private tryConsumeSkillPropertyNode (context: SkillPropertyFingerprintContext) =
        context.RemainingNodes <- context.RemainingNodes - 1
        context.RemainingNodes >= 0

    let private withTrackedCollection
        (context: SkillPropertyFingerprintContext)
        (container: obj)
        (work: unit -> ValueOption<JsonNode>)
        =
        if not (context.Active.Add container) then
            ValueNone
        else
            try
                work ()
            finally
                context.Active.Remove container |> ignore

    let private computeFingerprintFromNode (node: JsonNode) =
        canonicalJsonBytes node |> SHA256.HashData |> Convert.ToHexStringLower

    let private freezeMetadata (entries: seq<KeyValuePair<string, string>>) =
        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for KeyValue(key, value) in entries do
            dictionary[key] <- value

        if dictionary.Count = 0 then
            emptyMetadata
        else
            dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

    let private bytesFingerprintNode (kind: string) (bytes: byte[]) =
        let fingerprint = JsonObject()
        addString fingerprint "kind" kind
        addInt64 fingerprint "length" bytes.LongLength
        addString fingerprint "sha256" (SHA256.HashData(bytes) |> Convert.ToHexStringLower)
        fingerprint :> JsonNode

    let private staticResourceFingerprintNode (resource: SkillResource) =
        if resource.IsDynamic then
            null
        else
            match resource.StaticValue with
            | :? string as text -> text |> Encoding.UTF8.GetBytes |> bytesFingerprintNode "text"
            | :? (byte[]) as bytes -> bytesFingerprintNode "bytes" bytes
            | :? JsonElement as element ->
                canonicalizeJsonElement element
                |> canonicalJsonBytes
                |> bytesFingerprintNode "json"
            | :? JsonDocument as document ->
                canonicalizeJsonElement document.RootElement
                |> canonicalJsonBytes
                |> bytesFingerprintNode "json"
            | value when not (isNull value) ->
                try
                    JsonSerializer.SerializeToUtf8Bytes(value, value.GetType())
                    |> bytesFingerprintNode $"json:{value.GetType().FullName}"
                with _ ->
                    let fallback = value.ToString()
                    let text = if isNull fallback then String.Empty else fallback

                    text
                    |> Encoding.UTF8.GetBytes
                    |> bytesFingerprintNode $"stringified:{value.GetType().FullName}"
            | _ -> null

    let rec private tryCreateSkillPropertyValueNode (context: SkillPropertyFingerprintContext) depth (value: obj) =
        if depth > maxSkillPropertyDepth || not (tryConsumeSkillPropertyNode context) then
            ValueNone
        elif isNull value then
            ValueSome(JsonValue.Create null :> JsonNode)
        else
            let valueType = value.GetType()

            if value :? string then
                let node = JsonObject()
                addString node "kind" "string"
                addString node "value" (value :?> string)
                ValueSome(node :> JsonNode)
            elif valueType = typeof<bool> then
                let node = JsonObject()
                addString node "kind" "boolean"
                addNode node "value" (JsonValue.Create(unbox<bool> value) :> JsonNode)
                ValueSome(node :> JsonNode)
            elif valueType = typeof<char> then
                let node = JsonObject()
                addString node "kind" "char"
                addString node "value" (string (unbox<char> value))
                ValueSome(node :> JsonNode)
            elif valueType.IsPrimitive then
                let node = JsonObject()
                addString node "kind" "primitive"
                addString node "type" valueType.AssemblyQualifiedName
                addString node "value" (Convert.ToString(value, CultureInfo.InvariantCulture))
                ValueSome(node :> JsonNode)
            elif valueType.IsEnum then
                let node = JsonObject()
                addString node "kind" "enum"
                addString node "type" valueType.AssemblyQualifiedName
                addString node "value" (value.ToString())
                ValueSome(node :> JsonNode)
            elif value :? JsonElement then
                let node = JsonObject()
                addString node "kind" "json"
                addNode node "value" (canonicalizeJsonElement (value :?> JsonElement))
                ValueSome(node :> JsonNode)
            elif value :? JsonDocument then
                let node = JsonObject()
                addString node "kind" "json"
                addNode node "value" (canonicalizeJsonElement ((value :?> JsonDocument).RootElement))
                ValueSome(node :> JsonNode)
            elif isDictionaryType valueType then
                tryCreateDictionaryValueNode context depth value valueType
            elif isSetType valueType then
                tryCreateSetValueNode context depth value valueType
            elif isOrderedCollectionType valueType then
                tryCreateOrderedCollectionValueNode context depth value valueType
            elif value :? System.Collections.IEnumerable then
                ValueNone
            else
                ValueNone

    and private tryCreateOrderedCollectionValueNode
        (context: SkillPropertyFingerprintContext)
        depth
        (value: obj)
        (valueType: Type)
        =
        withTrackedCollection context value (fun () ->
            let items = JsonArray()
            let mutable stable = true
            let enumerator = (value :?> System.Collections.IEnumerable).GetEnumerator()

            while stable && context.RemainingNodes >= 0 && enumerator.MoveNext() do
                match tryCreateSkillPropertyValueNode context (depth + 1) enumerator.Current with
                | ValueSome itemNode -> items.Add itemNode |> ignore
                | ValueNone -> stable <- false

                if context.RemainingNodes < 0 then
                    stable <- false

            if stable then
                let node = JsonObject()
                addString node "kind" "ordered-collection"
                addString node "type" valueType.AssemblyQualifiedName
                addNode node "items" (items :> JsonNode)
                ValueSome(node :> JsonNode)
            else
                ValueNone)

    and private tryCreateSetValueNode (context: SkillPropertyFingerprintContext) depth (value: obj) (valueType: Type) =
        withTrackedCollection context value (fun () ->
            let items = ResizeArray<struct (string * int * JsonNode)>()
            let mutable stable = true
            let mutable index = 0
            let enumerator = (value :?> System.Collections.IEnumerable).GetEnumerator()

            while stable && context.RemainingNodes >= 0 && enumerator.MoveNext() do
                match tryCreateSkillPropertyValueNode context (depth + 1) enumerator.Current with
                | ValueSome itemNode -> items.Add(struct (canonicalJsonText itemNode, index, itemNode))
                | ValueNone -> stable <- false

                index <- index + 1

                if context.RemainingNodes < 0 then
                    stable <- false

            if stable then
                let node = JsonObject()
                addString node "kind" "set"
                addString node "type" valueType.AssemblyQualifiedName

                let sortedItems = JsonArray()

                for struct (_, _, itemNode) in
                    items |> Seq.sortBy (fun struct (encoding, itemIndex, _) -> encoding, itemIndex) do
                    sortedItems.Add itemNode |> ignore

                addNode node "items" (sortedItems :> JsonNode)
                ValueSome(node :> JsonNode)
            else
                ValueNone)

    and private tryCreateDictionaryValueNode
        (context: SkillPropertyFingerprintContext)
        depth
        (value: obj)
        (valueType: Type)
        =
        withTrackedCollection context value (fun () ->
            let entries = ResizeArray<struct (string * int * JsonNode * JsonNode)>()
            let mutable stable = true
            let mutable index = 0
            let enumerator = (value :?> System.Collections.IEnumerable).GetEnumerator()

            while stable && context.RemainingNodes >= 0 && enumerator.MoveNext() do
                match tryGetDictionaryEntry enumerator.Current with
                | ValueSome(key, entryValue) ->
                    match
                        tryCreateSkillPropertyValueNode context (depth + 1) key,
                        tryCreateSkillPropertyValueNode context (depth + 1) entryValue
                    with
                    | ValueSome keyNode, ValueSome valueNode ->
                        entries.Add(struct (canonicalJsonText keyNode, index, keyNode, valueNode))
                    | _ -> stable <- false

                | ValueNone -> stable <- false

                index <- index + 1

                if context.RemainingNodes < 0 then
                    stable <- false

            if stable then
                let node = JsonObject()
                addString node "kind" "dictionary"
                addString node "type" valueType.AssemblyQualifiedName

                let entryNodes = JsonArray()

                for struct (_, _, keyNode, valueNode) in
                    entries
                    |> Seq.sortBy (fun struct (encoding, entryIndex, _, _) -> encoding, entryIndex) do
                    let entryNode = JsonObject()
                    addNode entryNode "key" keyNode
                    addNode entryNode "value" valueNode
                    entryNodes.Add(entryNode) |> ignore

                addNode node "entries" (entryNodes :> JsonNode)
                ValueSome(node :> JsonNode)
            else
                ValueNone)

    let private createNonResumableSkillPropertyNode
        (runContext: RunContext)
        (skill: ResolvedSkill)
        (entry: KeyValuePair<string, obj>)
        =
        let node = JsonObject()
        addString node "kind" "non-resumable"
        addString node "runId" (runContext.RunId.ToString())
        addString node "skillId" skill.Reference.Id.Value
        addString node "skillVersion" (skill.Reference.Version.ToString())
        addString node "propertyKey" entry.Key
        addString node "propertyType" (entry.Value.GetType().AssemblyQualifiedName)
        addString node "reason" "Unsupported skill property value cannot be deterministically fingerprinted."
        node :> JsonNode

    let private skillPropertyFingerprintNode
        (context: SkillPropertyFingerprintContext)
        (runContext: RunContext)
        (skill: ResolvedSkill)
        (entry: KeyValuePair<string, obj>)
        =
        let propertyNode = JsonObject()
        addString propertyNode "key" entry.Key

        match tryCreateSkillPropertyValueNode context 0 entry.Value with
        | ValueSome valueNode -> addNode propertyNode "value" valueNode
        | ValueNone -> addNode propertyNode "value" (createNonResumableSkillPropertyNode runContext skill entry)

        propertyNode :> JsonNode

    let private resourceNode (resource: SkillResource) =
        let node = JsonObject()
        addString node "name" resource.Name
        addString node "description" resource.Description
        addNode node "isDynamic" (JsonValue.Create resource.IsDynamic :> JsonNode)

        if not resource.IsDynamic then
            addNode node "staticFingerprint" (staticResourceFingerprintNode resource)

        node :> JsonNode

    let private scriptNode (script: SkillScriptDescriptor) =
        let node = JsonObject()
        addString node "name" script.Name
        addString node "description" script.Description

        let metadata = JsonArray()

        for entry in sortMetadataOrdinal script.Metadata do
            let metadataNode = JsonObject()
            addString metadataNode "key" entry.Key
            addString metadataNode "value" entry.Value
            metadata.Add(metadataNode) |> ignore

        addNode node "metadata" (metadata :> JsonNode)
        node :> JsonNode

    let private skillReferenceNode (skill: SkillReference) =
        let node = JsonObject()
        addString node "id" skill.Id.Value
        addString node "version" (skill.Version.ToString())
        addString node "description" skill.Description

        let source = JsonObject()
        addString source "kind" (skill.Source.Kind.ToString())

        let fileRoots = JsonArray()

        for fileRoot in sortStringsOrdinal skill.Source.FileRoots do
            fileRoots.Add(JsonValue.Create(fileRoot)) |> ignore

        addNode source "fileRoots" (fileRoots :> JsonNode)
        addString source "instructions" skill.Source.Instructions

        let resources = JsonArray()

        for resource in skill.Source.Resources do
            resources.Add(resourceNode resource) |> ignore

        addNode source "resources" (resources :> JsonNode)

        let scripts = JsonArray()

        for script in skill.Source.Scripts do
            scripts.Add(scriptNode script) |> ignore

        addNode source "scripts" (scripts :> JsonNode)
        addNode node "source" (source :> JsonNode)

        let metadata = JsonArray()

        for entry in sortMetadataOrdinal skill.Metadata do
            let metadataNode = JsonObject()
            addString metadataNode "key" entry.Key
            addString metadataNode "value" entry.Value
            metadata.Add(metadataNode) |> ignore

        addNode node "metadata" (metadata :> JsonNode)
        node :> JsonNode

    let createSessionMetadata (bindingFingerprint: string) =
        freezeMetadata [ KeyValuePair(SessionBindingMetadataKey, bindingFingerprint) ]

    let definitionFingerprint (agent: AgentDefinition) =
        let root = JsonObject()
        addString root "id" agent.Id.Value
        addString root "version" (agent.Version.ToString())
        addString root "name" agent.Name
        addString root "instructions" agent.Instructions
        addString root "modelHint" (agent.ModelHint |> ValueOption.defaultValue "")

        let toolTags = JsonArray()

        for toolTag in sortStringsOrdinal agent.ToolTags do
            toolTags.Add(JsonValue.Create(toolTag)) |> ignore

        addNode root "toolTags" (toolTags :> JsonNode)

        let skills = JsonArray()

        for skill in agent.Skills do
            skills.Add(skillReferenceNode skill) |> ignore

        addNode root "skills" (skills :> JsonNode)

        let metadata = JsonArray()

        for entry in sortMetadataOrdinal agent.Metadata do
            let metadataNode = JsonObject()
            addString metadataNode "key" entry.Key
            addString metadataNode "value" entry.Value
            metadata.Add(metadataNode) |> ignore

        addNode root "metadata" (metadata :> JsonNode)
        computeFingerprintFromNode (root :> JsonNode)

    let private signatureFingerprint<'Input, 'Output> (signature: Signature<'Input, 'Output>) =
        let root = JsonObject()
        addString root "id" signature.Id.Value
        addString root "version" (signature.Version.ToString())
        addString root "description" signature.Description
        addString root "instructions" signature.Instructions

        addNode
            root
            "inputSchema"
            (canonicalizeJsonElement (JsonDocument.Parse(signature.Input.Schema.ToJsonString()).RootElement))

        addNode
            root
            "outputSchema"
            (canonicalizeJsonElement (JsonDocument.Parse(signature.Output.Schema.ToJsonString()).RootElement))

        computeFingerprintFromNode (root :> JsonNode)

    let private capabilityFingerprint
        (context: SkillPropertyFingerprintContext)
        (runContext: RunContext)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        let root = JsonObject()
        let toolArray = JsonArray()

        for tool in
            tools
            |> Seq.sortBy (fun tool -> tool.ModelName, tool.Tool.Name.Value, tool.Tool.Version) do
            let toolNode = JsonObject()
            addString toolNode "modelName" tool.ModelName
            addString toolNode "name" tool.Tool.Name.Value
            addString toolNode "version" (tool.Tool.Version.ToString())
            addString toolNode "description" tool.Tool.Description
            addString toolNode "approval" (tool.Tool.Approval.ToString())
            addString toolNode "approvalPolicy" (tool.Tool.ApprovalPolicy |> ValueOption.defaultValue "")

            addNode
                toolNode
                "inputSchema"
                (canonicalizeJsonElement (JsonDocument.Parse(tool.Tool.InputSchema.ToJsonString()).RootElement))

            addNode
                toolNode
                "outputSchema"
                (canonicalizeJsonElement (JsonDocument.Parse(tool.Tool.OutputSchema.ToJsonString()).RootElement))

            let tags = JsonArray()

            for tag in sortStringsOrdinal tool.Tags do
                tags.Add(JsonValue.Create(tag)) |> ignore

            addNode toolNode "tags" (tags :> JsonNode)
            toolArray.Add(toolNode) |> ignore

        addNode root "tools" (toolArray :> JsonNode)

        let skillArray = JsonArray()

        for skill in
            skills
            |> Seq.sortBy (fun skill -> skill.Reference.Id.Value, skill.Reference.Version) do
            let skillNode = JsonObject()
            addNode skillNode "reference" (skillReferenceNode skill.Reference)

            match skill.TryGetProperty<MafSkillAdapter.MafPreparedFileSkills>(MafSkillAdapterProperties.FileSkills) with
            | ValueSome preparedFileSkills ->
                let snapshots = JsonArray()

                for snapshot in
                    preparedFileSkills.Snapshots.Values
                    |> Seq.sortWith (fun left right ->
                        StringComparer.Ordinal.Compare(left.CanonicalRoot, right.CanonicalRoot)) do
                    snapshots.Add(JsonValue.Create(snapshot.ManifestFingerprint)) |> ignore

                addNode skillNode "fileSnapshots" (snapshots :> JsonNode)
            | ValueNone -> ()

            let properties = JsonArray()

            for entry in sortPropertiesOrdinal skill.Properties do
                if not (StringComparer.Ordinal.Equals(entry.Key, MafSkillAdapterProperties.FileSkills)) then
                    properties.Add(skillPropertyFingerprintNode context runContext skill entry)
                    |> ignore

            addNode skillNode "properties" (properties :> JsonNode)
            skillArray.Add(skillNode) |> ignore

        addNode root "skills" (skillArray :> JsonNode)
        computeFingerprintFromNode (root :> JsonNode)

    let createSessionBinding<'Input, 'Output>
        (runContext: RunContext)
        (signature: Signature<'Input, 'Output>)
        (tools: IReadOnlyList<ResolvedMafTool>)
        (skills: IReadOnlyList<ResolvedSkill>)
        =
        let root = JsonObject()
        let context = createSkillPropertyFingerprintContext ()
        addString root "signature" (signatureFingerprint signature)
        addString root "tenantId" (runContext.Options.TenantId |> ValueOption.defaultValue "")
        addString root "userId" (runContext.Options.UserId |> ValueOption.defaultValue "")
        addString root "capabilities" (capabilityFingerprint context runContext tools skills)
        computeFingerprintFromNode (root :> JsonNode)

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
