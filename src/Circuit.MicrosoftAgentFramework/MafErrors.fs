#nowarn "57"

namespace Circuit.MicrosoftAgentFramework

open System
open System.Collections.Frozen
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open Circuit.Core
open Microsoft.Extensions.AI

module internal MafErrors =
    let private emptyDictionary =
        Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal)
        :> IReadOnlyDictionary<string, string>

    let emptyDiagnosticMetadata = emptyDictionary

    let private createFailure code runId operationId message requestId innerException =
        CircuitFailure(code, message, ValueSome runId, operationId, requestId, innerException)

    let formatValidationIssues (issues: IReadOnlyList<ValidationIssue>) =
        if isNull issues || issues.Count = 0 then
            "Validation failed."
        else
            issues
            |> Seq.map (fun issue -> $"{issue.Path}: {issue.Message}")
            |> String.concat "; "
            |> fun details -> $"Validation failed: {details}"

    let validationFailure runId message =
        createFailure CircuitFailureCode.Validation runId ValueNone message ValueNone ValueNone

    let structuredOutputUnsupportedFailure runId message requestId innerException =
        createFailure CircuitFailureCode.StructuredOutputUnsupported runId ValueNone message requestId innerException

    let decodeFailure runId message requestId innerException =
        createFailure CircuitFailureCode.Decode runId ValueNone message requestId innerException

    let providerFailure runId message requestId innerException =
        createFailure CircuitFailureCode.Provider runId ValueNone message requestId innerException

    let toolFailure runId message innerException =
        createFailure CircuitFailureCode.Tool runId ValueNone message ValueNone innerException

    let skillFailure runId message innerException =
        createFailure CircuitFailureCode.Skill runId ValueNone message ValueNone innerException

    let sanitizeModelVisibleMessage (defaultMessage: string) (message: string) =
        if String.IsNullOrWhiteSpace message then
            defaultMessage
        elif message.IndexOfAny([| '\r'; '\n' |]) >= 0 then
            defaultMessage
        elif Regex.IsMatch(message, "(^|[\\s(])([A-Za-z]:\\\\|/)") then
            defaultMessage
        else
            message

    let checkpointMismatchFailure runId message =
        createFailure CircuitFailureCode.CheckpointMismatch runId ValueNone message ValueNone ValueNone

    let cancelledFailure runId message innerException =
        createFailure CircuitFailureCode.Cancelled runId ValueNone message ValueNone innerException

    let private toTokenCount (value: Nullable<int64>) =
        if not value.HasValue then 0
        elif value.Value <= 0L then 0
        elif value.Value >= int64 Int32.MaxValue then Int32.MaxValue
        else int value.Value

    let private normalizeUsageCount (value: Nullable<int64>) =
        if not value.HasValue then Nullable()
        elif value.Value <= 0L then Nullable 0L
        else value

    let private saturatingAdd (left: int64) (right: int64) =
        if Int64.MaxValue - left < right then
            Int64.MaxValue
        else
            left + right

    let private combineUsageCount (left: Nullable<int64>) (right: Nullable<int64>) =
        let left = normalizeUsageCount left
        let right = normalizeUsageCount right

        if not left.HasValue then right
        elif not right.HasValue then left
        else Nullable(saturatingAdd left.Value right.Value)

    let private getUsageCount (selector: UsageDetails -> Nullable<int64>) (usage: UsageDetails) =
        if isNull usage then Nullable() else selector usage

    let private combineAdditionalCounts
        (left: AdditionalPropertiesDictionary<int64>)
        (right: AdditionalPropertiesDictionary<int64>)
        =
        let combined = Dictionary<string, int64>(StringComparer.Ordinal)

        let append (counts: AdditionalPropertiesDictionary<int64>) =
            if not (isNull counts) then
                for KeyValue(key, value) in counts do
                    match combined.TryGetValue key with
                    | true, existing -> combined[key] <- saturatingAdd existing value
                    | false, _ -> combined[key] <- value

        append left
        append right

        if combined.Count = 0 then
            null
        else
            AdditionalPropertiesDictionary<int64>(combined)

    let combineUsageDetails (left: UsageDetails) (right: UsageDetails) =
        match left, right with
        | null, null -> null
        | _ ->
            let combined = UsageDetails()

            combined.InputTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.InputTokenCount) left)
                    (getUsageCount (fun usage -> usage.InputTokenCount) right)

            combined.OutputTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.OutputTokenCount) left)
                    (getUsageCount (fun usage -> usage.OutputTokenCount) right)

            combined.TotalTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.TotalTokenCount) left)
                    (getUsageCount (fun usage -> usage.TotalTokenCount) right)

            combined.CachedInputTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.CachedInputTokenCount) left)
                    (getUsageCount (fun usage -> usage.CachedInputTokenCount) right)

            combined.ReasoningTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.ReasoningTokenCount) left)
                    (getUsageCount (fun usage -> usage.ReasoningTokenCount) right)

            combined.InputAudioTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.InputAudioTokenCount) left)
                    (getUsageCount (fun usage -> usage.InputAudioTokenCount) right)

            combined.InputTextTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.InputTextTokenCount) left)
                    (getUsageCount (fun usage -> usage.InputTextTokenCount) right)

            combined.OutputAudioTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.OutputAudioTokenCount) left)
                    (getUsageCount (fun usage -> usage.OutputAudioTokenCount) right)

            combined.OutputTextTokenCount <-
                combineUsageCount
                    (getUsageCount (fun usage -> usage.OutputTextTokenCount) left)
                    (getUsageCount (fun usage -> usage.OutputTextTokenCount) right)

            let additionalCounts =
                combineAdditionalCounts
                    (if isNull left then null else left.AdditionalCounts)
                    (if isNull right then null else right.AdditionalCounts)

            if not (isNull additionalCounts) then
                combined.AdditionalCounts <- additionalCounts

            combined

    let createUsage (usage: UsageDetails) =
        match usage with
        | null -> RunUsage(0, 0)
        | usage -> RunUsage(toTokenCount usage.InputTokenCount, toTokenCount usage.OutputTokenCount)

    let rec isCancellationRequested (cancellationToken: CancellationToken) (ex: exn) =
        cancellationToken.IsCancellationRequested
        || match ex with
           | :? OperationCanceledException -> true
           | :? AggregateException as aggregate ->
               aggregate.InnerExceptions
               |> Seq.exists (isCancellationRequested cancellationToken)
           | _ -> false

    let isStructuredOutputUnsupported (ex: exn) =
        match ex with
        | :? NotSupportedException -> true
        | :? InvalidOperationException as invalidOperationException ->
            invalidOperationException.Message.Contains("response format", StringComparison.OrdinalIgnoreCase)
            || invalidOperationException.Message.Contains("structured output", StringComparison.OrdinalIgnoreCase)
        | _ -> false

    let isDecodeFailure (ex: exn) =
        match ex with
        | :? JsonException -> true
        | :? InvalidOperationException as invalidOperationException ->
            invalidOperationException.Message.Contains("did not contain json", StringComparison.OrdinalIgnoreCase)
            || invalidOperationException.Message.Contains(
                "deserialized response is null",
                StringComparison.OrdinalIgnoreCase
            )
            || invalidOperationException.Message.Contains("data property", StringComparison.OrdinalIgnoreCase)
            || invalidOperationException.Message.Contains(
                "structured output wrapper",
                StringComparison.OrdinalIgnoreCase
            )
        | _ -> false
