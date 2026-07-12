using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circuit;
using Circuit.MicrosoftAgentFramework;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Circuit.ProviderContract;

internal enum CapabilityStatus
{
    Passed,
    Failed,
    Unsupported,
}

internal sealed class ProviderPackageMetadata
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}

internal sealed class ProviderMetadata
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required decimal InputCostUsdPer1KTokens { get; init; }
    public required decimal OutputCostUsdPer1KTokens { get; init; }
    public required IReadOnlyList<ProviderPackageMetadata> Packages { get; init; }
}

internal sealed class CapabilityBudget
{
    public required string Name { get; init; }
    public required int EstimatedInputTokens { get; init; }
    public required int MaxOutputTokens { get; init; }
}

internal sealed class TokenTotals
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens => InputTokens + OutputTokens;

    public static TokenTotals Zero { get; } = new();

    public static TokenTotals operator +(TokenTotals left, TokenTotals right) => new()
    {
        InputTokens = left.InputTokens + right.InputTokens,
        OutputTokens = left.OutputTokens + right.OutputTokens,
    };
}

internal sealed class CapabilityResult
{
    public required string Name { get; init; }
    public required CapabilityStatus Status { get; init; }
    public string? FailureCode { get; init; }
    public required TokenTotals Tokens { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
    public required IReadOnlyList<string> RequestIds { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
}

internal sealed class TraceSummary
{
    public required IReadOnlyList<TraceSpanRecord> Spans { get; init; }
    public required IReadOnlyList<TraceMetricRecord> Metrics { get; init; }
}

internal sealed class TraceSpanRecord
{
    public required string Source { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
}

internal sealed class TraceMetricRecord
{
    public required string Name { get; init; }
    public required string MetricType { get; init; }
    public required IReadOnlyList<TraceMetricPointRecord> Points { get; init; }
}

internal sealed class TraceMetricPointRecord
{
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
    public long? SumLong { get; init; }
    public long? HistogramCount { get; init; }
}

internal sealed class ContractSummary
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string DateUtc { get; init; }
    public required decimal PerProviderMaxCostUsd { get; init; }
    public decimal WorstCaseEstimatedCostUsd { get; init; }
    public decimal? ActualEstimatedCostUsd { get; init; }
    public required TokenTotals TotalTokens { get; init; }
    public required IReadOnlyList<ProviderPackageMetadata> Packages { get; init; }
    public required IReadOnlyList<CapabilityResult> Capabilities { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
    public string TracePath { get; init; } = string.Empty;
}

internal sealed class CapturedProviderCall
{
    public required IReadOnlyList<string> ResponseIds { get; init; }
    public string? ConversationId { get; init; }
    public string? ModelId { get; init; }
}

internal sealed class ProviderCallRecorder(IChatClient inner) : IChatClient, IDisposable
{
    private sealed class ScenarioState
    {
        public required string Name { get; init; }
        public required int MaxOutputTokens { get; init; }
    }

    private static readonly AsyncLocal<ScenarioState?> CurrentScenario = new();
    private readonly ConcurrentQueue<CapturedProviderCall> _calls = new();

    public IReadOnlyList<CapturedProviderCall> Snapshot() => _calls.ToArray();

    public IDisposable BeginScenario(string name, int maxOutputTokens)
    {
        CurrentScenario.Value = new ScenarioState { Name = name, MaxOutputTokens = maxOutputTokens };
        return new Scope(() => CurrentScenario.Value = null);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ApplyBudget(options ??= new ChatOptions());
        return CaptureResponseAsync(inner.GetResponseAsync(messages, options, cancellationToken));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyBudget(options ??= new ChatOptions());

        var responseIds = new HashSet<string>(StringComparer.Ordinal);
        string? conversationId = null;
        string? modelId = null;

        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(update.ResponseId))
            {
                responseIds.Add(update.ResponseId);
            }

            conversationId ??= update.ConversationId;
            modelId ??= update.ModelId;
            yield return update;
        }

        _calls.Enqueue(new CapturedProviderCall
        {
            ResponseIds = responseIds.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            ConversationId = conversationId,
            ModelId = modelId,
        });
    }

    public object? GetService(Type serviceType, object? serviceKey) => inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        if (inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ApplyBudget(ChatOptions options)
    {
        if (CurrentScenario.Value is not { } scenario)
        {
            return;
        }

        if (!options.MaxOutputTokens.HasValue || options.MaxOutputTokens.Value > scenario.MaxOutputTokens)
        {
            options.MaxOutputTokens = scenario.MaxOutputTokens;
        }
    }

    private async Task<ChatResponse> CaptureResponseAsync(Task<ChatResponse> pending)
    {
        var response = await pending.ConfigureAwait(false);
        _calls.Enqueue(new CapturedProviderCall
        {
            ResponseIds = string.IsNullOrWhiteSpace(response.ResponseId) ? [] : [response.ResponseId],
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
        });

        return response;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal sealed class EnvelopeRunObserver : IRunObserver
{
    private readonly ConcurrentQueue<RunEventEnvelope> _events = new();

    public IReadOnlyList<RunEventEnvelope> Events => _events.ToArray();

    public ValueTask OnEventAsync(RunEventEnvelope @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        _events.Enqueue(@event);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScenarioHost : IDisposable
{
    private readonly ProviderCallRecorder _recorder;
    private readonly EnvelopeRunObserver _observer;
    private readonly TelemetryArtifacts? _telemetry;

    public ScenarioHost(ProviderMetadata metadata, ProviderCallRecorder recorder, EnvelopeRunObserver observer, TelemetryArtifacts? telemetry)
    {
        Metadata = metadata;
        _recorder = recorder;
        _observer = observer;
        _telemetry = telemetry;
    }

    public ProviderMetadata Metadata { get; }
    public IChatClient ChatClient => _recorder;
    public ProviderCallRecorder Recorder => _recorder;
    public EnvelopeRunObserver Observer => _observer;
    public TelemetryArtifacts? Telemetry => _telemetry;

    public void Dispose()
    {
        _telemetry?.Dispose();
        _recorder.Dispose();
    }
}

internal sealed class TelemetryArtifacts : IDisposable
{
    private static readonly HashSet<string> AllowedMetricTags =
    [
        "circuit.definition.id",
        "circuit.definition.version",
        "circuit.operation.kind",
        "circuit.status",
    ];

    private readonly List<Activity> _spans = [];
    private readonly List<Metric> _metrics = [];
    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;

    public TelemetryArtifacts(string providerSourceName)
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("CircuitDotNet")
            .AddSource(providerSourceName)
            .AddInMemoryExporter(_spans)
            .Build();

        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("CircuitDotNet")
            .AddInMemoryExporter(_metrics)
            .Build();
    }

    public OpenTelemetryRunObserver Observer { get; } = new(null);

    public TraceSummary Capture()
    {
        _tracerProvider.ForceFlush();
        _meterProvider.ForceFlush();

        var spans = _spans.Select(static span => new TraceSpanRecord
        {
            Source = span.Source.Name,
            Name = span.OperationName,
            Status = span.Status.ToString(),
            Tags = span.TagObjects
                .Where(static pair => pair.Value is not null && !pair.Key.StartsWith("circuit.prompt", StringComparison.Ordinal))
                .Where(static pair => !pair.Key.StartsWith("circuit.input", StringComparison.Ordinal))
                .Where(static pair => !pair.Key.StartsWith("circuit.output", StringComparison.Ordinal))
                .Where(static pair => !pair.Key.StartsWith("circuit.tool.arguments", StringComparison.Ordinal))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal),
        }).ToArray();

        var metrics = _metrics.Select(metric =>
        {
            var points = new List<TraceMetricPointRecord>();
            foreach (var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in point.Tags)
                {
                    if (AllowedMetricTags.Contains(pair.Key))
                    {
                        tags[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                    }
                }

                points.Add(new TraceMetricPointRecord
                {
                    Tags = tags,
                    SumLong = metric.MetricType.IsSum() || metric.MetricType.IsGauge() ? point.GetSumLong() : null,
                    HistogramCount = metric.MetricType.IsHistogram() ? point.GetHistogramCount() : null,
                });
            }

            return new TraceMetricRecord
            {
                Name = metric.Name,
                MetricType = metric.MetricType.ToString(),
                Points = points,
            };
        }).ToArray();

        return new TraceSummary { Spans = spans, Metrics = metrics };
    }

    public void Dispose()
    {
        _meterProvider.Dispose();
        _tracerProvider.Dispose();
    }
}

internal static class Costing
{
    public static decimal EstimateWorstCaseUsd(CapabilityBudget budget, ProviderMetadata metadata)
        => RoundUsd((budget.EstimatedInputTokens / 1000m * metadata.InputCostUsdPer1KTokens)
            + (budget.MaxOutputTokens / 1000m * metadata.OutputCostUsdPer1KTokens));

    public static decimal EstimateActualUsd(TokenTotals tokens, ProviderMetadata metadata)
        => RoundUsd((tokens.InputTokens / 1000m * metadata.InputCostUsdPer1KTokens)
            + (tokens.OutputTokens / 1000m * metadata.OutputCostUsdPer1KTokens));

    private static decimal RoundUsd(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
