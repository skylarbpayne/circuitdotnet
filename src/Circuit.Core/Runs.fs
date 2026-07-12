namespace Circuit.Core

open System
open System.Collections.Frozen
open System.Collections.Generic

type StructuredOutputPolicy =
    | NativeOnly = 0
    | AllowSecondaryModelRepair = 1

type SensitiveDataMode =
    | Standard = 0
    | Redact = 1

[<Sealed>]
type CircuitSession internal (id: string, metadata: IReadOnlyDictionary<string, string>) =
    member _.Id = id
    member _.Metadata = metadata

[<Sealed>]
type RunUsage internal (inputTokens: int, outputTokens: int) =
    member _.InputTokens = inputTokens
    member _.OutputTokens = outputTokens
    member _.TotalTokens = inputTokens + outputTokens

type private EmptyServiceProvider private () =
    interface IServiceProvider with
        member _.GetService(_serviceType) = null

    static member Instance = EmptyServiceProvider() :> IServiceProvider

module private RunValidation =
    let copyTags (tags: seq<KeyValuePair<string, string>>) =
        let entries = tags |> Seq.toArray

        if entries.Length > 32 then
            invalidArg "tags" "No more than 32 tags are allowed."

        let dictionary = Dictionary<string, string>(StringComparer.Ordinal)

        for entry in entries do
            if String.IsNullOrWhiteSpace entry.Key then
                invalidArg "tags" "Tag keys cannot be blank."

            if entry.Key.Length > 64 then
                invalidArg "tags" "Tag keys must be 64 characters or fewer."

            if entry.Key.StartsWith("circuit.", StringComparison.Ordinal) then
                invalidArg "tags" "Tag keys beginning with 'circuit.' are reserved."

            if isNull entry.Value then
                nullArg "tags"

            if entry.Value.Length > 256 then
                invalidArg "tags" "Tag values must be 256 characters or fewer."

            if dictionary.ContainsKey entry.Key then
                invalidArg "tags" "Duplicate tag keys are not allowed."

            dictionary.Add(entry.Key, entry.Value)

        dictionary.ToFrozenDictionary(StringComparer.Ordinal) :> IReadOnlyDictionary<string, string>

[<Sealed>]
type RunOptions
    internal
    (
        session: CircuitSession voption,
        tenantId: string voption,
        userId: string voption,
        tags: IReadOnlyDictionary<string, string>,
        structuredOutputPolicy: StructuredOutputPolicy,
        sensitiveDataMode: SensitiveDataMode,
        services: IServiceProvider
    ) =
    do
        if isNull services then
            nullArg "services"

        if isNull tags then
            nullArg "tags"

    member _.Session = session
    member _.TenantId = tenantId
    member _.UserId = userId
    member _.Tags = tags
    member _.StructuredOutputPolicy = structuredOutputPolicy
    member _.SensitiveDataMode = sensitiveDataMode
    member _.Services = services

    static member Default =
        RunOptions(
            ValueNone,
            ValueNone,
            ValueNone,
            RunValidation.copyTags Array.empty<KeyValuePair<string, string>>,
            StructuredOutputPolicy.NativeOnly,
            SensitiveDataMode.Standard,
            EmptyServiceProvider.Instance
        )

[<Sealed>]
type RunResult<'T>
    internal
    (
        runId: RunId,
        result: CircuitResult<'T>,
        usage: RunUsage,
        session: CircuitSession voption,
        startedAt: DateTimeOffset,
        completedAt: DateTimeOffset
    ) =
    member _.RunId = runId
    member _.Result = result
    member _.Usage = usage
    member _.Session = session
    member _.StartedAt = startedAt
    member _.CompletedAt = completedAt
