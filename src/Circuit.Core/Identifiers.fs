namespace Circuit.Core

open System
open System.Globalization
open System.Text.RegularExpressions

module private IdentifierRules =
    let runIdPattern = Regex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)

    let definitionIdPattern =
        Regex("^[a-z][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)

    let semanticVersionPattern =
        Regex("^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)

[<Struct; CustomEquality; CustomComparison>]
type RunId =
    private
    | RunId of string

    member this.Value =
        let (RunId value) = this
        value

    static member New() = RunId(Guid.NewGuid().ToString("N"))

    static member Parse(value: string) =
        if isNull value || not (IdentifierRules.runIdPattern.IsMatch value) then
            invalidArg "value" "Run IDs must be 32 lowercase hexadecimal characters."

        RunId value

    static member TryParse(value: string, result: byref<RunId>) =
        if isNull value || not (IdentifierRules.runIdPattern.IsMatch value) then
            result <- Unchecked.defaultof<RunId>
            false
        else
            result <- RunId value
            true

    override this.Equals(other: obj) =
        match other with
        | :? RunId as otherRunId -> StringComparer.Ordinal.Equals(this.Value, otherRunId.Value)
        | _ -> false

    override this.GetHashCode() =
        StringComparer.Ordinal.GetHashCode this.Value

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? RunId as otherRunId -> StringComparer.Ordinal.Compare(this.Value, otherRunId.Value)
            | null -> 1
            | _ -> invalidArg "other" "Object must be a RunId."

    interface IComparable<RunId> with
        member this.CompareTo(other: RunId) =
            StringComparer.Ordinal.Compare(this.Value, other.Value)

[<Struct; CustomEquality; CustomComparison>]
type DefinitionId =
    private
    | DefinitionId of string

    member this.Value =
        let (DefinitionId value) = this
        value

    static member Create(value: string) =
        if isNull value || not (IdentifierRules.definitionIdPattern.IsMatch value) then
            invalidArg
                "value"
                "Definition IDs must be 1-128 characters, start with a lowercase ASCII letter, and contain only lowercase letters, digits, '.', '_', or '-'."

        DefinitionId value

    static member TryCreate(value: string, result: byref<DefinitionId>) =
        if isNull value || not (IdentifierRules.definitionIdPattern.IsMatch value) then
            result <- Unchecked.defaultof<DefinitionId>
            false
        else
            result <- DefinitionId value
            true

    override this.Equals(other: obj) =
        match other with
        | :? DefinitionId as otherDefinitionId -> StringComparer.Ordinal.Equals(this.Value, otherDefinitionId.Value)
        | _ -> false

    override this.GetHashCode() =
        StringComparer.Ordinal.GetHashCode this.Value

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? DefinitionId as otherDefinitionId ->
                StringComparer.Ordinal.Compare(this.Value, otherDefinitionId.Value)
            | null -> 1
            | _ -> invalidArg "other" "Object must be a DefinitionId."

    interface IComparable<DefinitionId> with
        member this.CompareTo(other: DefinitionId) =
            StringComparer.Ordinal.Compare(this.Value, other.Value)

[<Struct; CustomEquality; CustomComparison>]
type SemanticVersion =
    private
    | SemanticVersion of Version

    member this.Value =
        let (SemanticVersion value) = this
        value

    override this.ToString() =
        let version = this.Value
        $"{version.Major}.{version.Minor}.{version.Build}"

    static member private TryParseParts(value: string) =
        if isNull value || not (IdentifierRules.semanticVersionPattern.IsMatch value) then
            ValueNone
        else
            let parts = value.Split('.')

            if parts.Length <> 3 then
                ValueNone
            else
                let mutable major = 0
                let mutable minor = 0
                let mutable patch = 0

                if
                    Int32.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, &major)
                    && Int32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, &minor)
                    && Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, &patch)
                then
                    ValueSome(major, minor, patch)
                else
                    ValueNone

    static member Parse(value: string) =
        match SemanticVersion.TryParseParts value with
        | ValueSome(major, minor, patch) -> SemanticVersion(Version(major, minor, patch))
        | ValueNone -> invalidArg "value" "Semantic versions must use the exact major.minor.patch format."

    static member TryParse(value: string, result: byref<SemanticVersion>) =
        match SemanticVersion.TryParseParts value with
        | ValueSome(major, minor, patch) ->
            result <- SemanticVersion(Version(major, minor, patch))
            true
        | ValueNone ->
            result <- Unchecked.defaultof<SemanticVersion>
            false

    override this.Equals(other: obj) =
        match other with
        | :? SemanticVersion as otherVersion -> this.Value.Equals otherVersion.Value
        | _ -> false

    override this.GetHashCode() = this.Value.GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? SemanticVersion as otherVersion -> this.Value.CompareTo otherVersion.Value
            | null -> 1
            | _ -> invalidArg "other" "Object must be a SemanticVersion."

    interface IComparable<SemanticVersion> with
        member this.CompareTo(other: SemanticVersion) = this.Value.CompareTo other.Value
