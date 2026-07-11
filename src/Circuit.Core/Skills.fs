namespace Circuit.Core

[<Sealed>]
type SkillReference internal (id: DefinitionId, version: SemanticVersion) =
    member _.Id = id
    member _.Version = version

    static member Create(id: string, version: string) =
        SkillReference(DefinitionId.Create id, SemanticVersion.Parse version)
