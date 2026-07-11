namespace Circuit.Core

open System
open System.Collections.Frozen
open System.Collections.Generic

module private AgentValidation =
    let private toolTagCharacters =
        Set.ofList ([ 'a' .. 'z' ] @ [ '0' .. '9' ] @ [ '.'; '_'; '-' ])

    let requireNonBlank name (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg name $"{name} cannot be blank."

        value

    let validateToolTag (value: string) =
        let normalized = requireNonBlank "toolTags" value

        if
            normalized
            |> Seq.exists (fun character -> not (toolTagCharacters.Contains character))
        then
            invalidArg "toolTags" "Tool tags must contain only lowercase letters, digits, '.', '_', or '-'."

        normalized

    let validateMetadataEntry (entry: KeyValuePair<string, string>) =
        if String.IsNullOrWhiteSpace entry.Key then
            invalidArg "metadata" "Metadata keys cannot be blank."

        if entry.Key.Length > 64 then
            invalidArg "metadata" "Metadata keys must be 64 characters or fewer."

        if isNull entry.Value then
            nullArg "metadata"

        if entry.Value.Length > 256 then
            invalidArg "metadata" "Metadata values must be 256 characters or fewer."

        entry

[<Sealed>]
type AgentDefinition
    internal
    (
        id: DefinitionId,
        version: SemanticVersion,
        name: string,
        instructions: string,
        modelHint: string voption,
        toolTags: IReadOnlySet<string>,
        skills: IReadOnlyList<SkillReference>,
        metadata: IReadOnlyDictionary<string, string>
    ) =
    member _.Id = id
    member _.Version = version
    member _.Name = name
    member _.Instructions = instructions
    member _.ModelHint = modelHint
    member _.ToolTags = toolTags
    member _.Skills = skills
    member _.Metadata = metadata

    static member Create
        (
            id: string,
            version: string,
            name: string,
            instructions: string,
            modelHint: string voption,
            toolTags: IEnumerable<string>,
            skills: IEnumerable<SkillReference>,
            metadata: IEnumerable<KeyValuePair<string, string>>
        ) =
        if isNull toolTags then
            nullArg "toolTags"

        if isNull skills then
            nullArg "skills"

        if isNull metadata then
            nullArg "metadata"

        let normalizedName = AgentValidation.requireNonBlank "name" name

        let normalizedInstructions =
            AgentValidation.requireNonBlank "instructions" instructions

        match modelHint with
        | ValueSome value when String.IsNullOrWhiteSpace value ->
            invalidArg "modelHint" "modelHint cannot be blank when provided."
        | _ -> ()

        let tags = toolTags |> Seq.map AgentValidation.validateToolTag |> Seq.toArray

        let tagSet = HashSet<string>(StringComparer.Ordinal)

        for tag in tags do
            if not (tagSet.Add tag) then
                invalidArg "toolTags" "Duplicate tool tags are not allowed."

        let skillArray = skills |> Seq.toArray
        let skillIds = HashSet<string>(StringComparer.Ordinal)

        for skill in skillArray do
            if isNull (box skill) then
                invalidArg "skills" "Skill references cannot contain null entries."

            let identity = $"{skill.Id.Value}@{skill.Version}"

            if not (skillIds.Add identity) then
                invalidArg "skills" "Duplicate skill references are not allowed."

        let metadataEntries =
            metadata |> Seq.map AgentValidation.validateMetadataEntry |> Seq.toArray

        let metadataDictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for entry in metadataEntries do
            if metadataDictionary.ContainsKey entry.Key then
                invalidArg "metadata" "Duplicate metadata keys are not allowed."

            metadataDictionary.Add(entry.Key, entry.Value)

        AgentDefinition(
            DefinitionId.Create id,
            SemanticVersion.Parse version,
            normalizedName,
            normalizedInstructions,
            modelHint,
            tagSet.ToFrozenSet(StringComparer.Ordinal) :> IReadOnlySet<string>,
            Array.AsReadOnly skillArray,
            metadataDictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>
        )
