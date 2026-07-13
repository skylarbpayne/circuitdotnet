using Circuit.Testing;
using System.Reflection;
using Xunit;

namespace Circuit.Interop.Tests;

public sealed class UnifiedInteropTests
{
    private sealed record Summary(int Total, int Count);

    [Fact]
    public void CSharp_graph_facade_exposes_dynamic_routing_and_approval()
    {
        var echo = CircuitDefinition<string, string>.FromCode(
            "echo",
            "1.0.0",
            (_, value, _) => Task.FromResult(value));
        var branches = new Dictionary<string, CircuitDefinition<string, string>>
        {
            ["known"] = echo.Named("known")
        };
        var routed = CircuitDefinition<string, string>.Branch(
            "route",
            "1.0.0",
            _ => "known",
            branches,
            echo.Named("fallback"));
        var merged = CircuitDefinition<string, string>.Merge("merge", "1.0.0", 2, [routed, echo]);
        var approval = CircuitDefinition<string, ApprovalResponse>.Approval(
            "review",
            "1.0.0",
            value => new ApprovalPrompt("Review", value));

        Assert.NotEmpty(merged.Fingerprint);
        Assert.NotEmpty(merged.Attempt().Fingerprint);
        Assert.NotEmpty(approval.Fingerprint);
    }

    [Fact]
    public async Task CSharpFacadeRunsAgentCircuitThroughUnifiedScheduler()
    {
        var runtime = new ScriptedRuntime(new[] { ScriptedResponses.OutputValue("done") });
        var agent = new AgentDefinition("echo-agent", "1.0.0", "Echo", "Echo input");
        var signature = new AgentSignature<string, string>("echo", "1.0.0", "Echo", "Return output");
        var circuit = CircuitDefinition<string, string>.FromAgent(agent, signature).Define("echo-circuit", "1.0.0");
        var client = new CircuitClientBuilder().UseRuntime(runtime).Build();

        var response = await client.RunAsync(circuit, "go");

        Assert.True(response.IsSuccess);
        Assert.Equal("done", response.Value);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public void Public_signatures_do_not_expose_Core_or_FSharp_types()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var type in typeof(ICircuitClient).Assembly.GetExportedTypes())
        {
            InspectType(type, $"T:{type.FullName}", forbidden, visited);
        }

        Assert.True(forbidden.Count == 0, string.Join(Environment.NewLine, forbidden.OrderBy(entry => entry, StringComparer.Ordinal)));
    }

    [Fact]
    public async Task CSharp_facade_executes_value_failure_loop_aggregate_source_and_ordered_projection()
    {
        var runtime = new ScriptedRuntime(Array.Empty<ScriptedResponse>());
        var client = new CircuitClientBuilder().UseRuntime(runtime).Build();

        var value = CircuitDefinition<Unit, int>.Value(7);
        var single = await client.RunAsync(value, new Unit());
        Assert.True(single.IsSuccess);
        Assert.Equal(7, single.Value);

        var failure = CircuitDefinition<int, int>.FromCodeResponse(
            "failure", "1.0.0",
            (context, _, _) => Task.FromResult(CircuitResponse<int>.Fail(context, CircuitFailure.Create(CircuitFailureCode.Provider, "controlled"))));
        var failed = await client.RunAsync(failure, 1);
        Assert.False(failed.IsSuccess);
        Assert.Equal(CircuitFailureCode.Provider, failed.Failure.Code);

        var increment = CircuitDefinition<int, int>.FromCode("increment", "1.0.0", (_, item, _) => Task.FromResult(item + 1));
        var loop = CircuitDefinition<int, int>.Loop("loop", "1.0.0", 4, item => item < 3, increment);
        Assert.Equal(3, (await client.RunAsync(loop, 0)).Value);

        var items = CircuitDefinition<Unit, int>.Items("items", "1.0.0", _ => new[] { 3, 1, 2 });
        var aggregated = items.Aggregate<Summary>("aggregate", "1.0.0", (_, responses, _) =>
            Task.FromResult(new Summary(responses.Sum(response => response.Value), responses.Count)));
        var summary = await client.RunAsync(aggregated, new Unit());
        Assert.True(summary.IsSuccess);
        Assert.Equal(6, summary.Value.Total);
        Assert.Equal(3, summary.Value.Count);

        var source = CircuitDefinition<Unit, int>.Source("source", "1.0.0", new OnePageSource());
        var ordered = await client.CollectSourceOrderAsync(source, new Unit());
        Assert.True(ordered.IsSuccess);
        Assert.Equal(new[] { 4, 5 }, ordered.Value.Select(response => response.Value));
        Assert.Empty(source.Validate());
    }

    [Fact]
    public async Task CSharp_facade_wraps_start_events_approval_checkpoint_and_resume()
    {
        var runtime = new ScriptedRuntime(Array.Empty<ScriptedResponse>());
        var client = new CircuitClientBuilder().UseRuntime(runtime).Build();
        var approval = CircuitDefinition<string, ApprovalResponse>.Approval(
            "approval", "1.0.0", value => new ApprovalPrompt("Review", value));
        await using var run = await client.StartAsync(approval, "approve");
        CircuitCheckpoint<ApprovalResponse>? checkpoint = null;
        var sawApproval = false;
        await foreach (var item in run.Events)
        {
            Assert.Equal(run.RunId, item.RunId);

            if (item.Kind == CircuitEventKind.ApprovalRequested)
            {
                sawApproval = true;
                var saved = await run.CreateCheckpointAsync();
                Assert.True(saved.IsSuccess);
                checkpoint = saved.Value;
                var accepted = await run.RespondAsync(new ApprovalResponse(item.Approval!.RequestId, true));
                Assert.True(accepted.IsSuccess);
            }
            if (item.Kind == CircuitEventKind.RunCompleted) break;
        }
        Assert.True(sawApproval);
        Assert.NotNull(checkpoint);
        var serialized = checkpoint!.Serialize();
        var roundTrip = CircuitCheckpoint<ApprovalResponse>.Deserialize(serialized);
        await using var resumed = await client.ResumeAsync(approval, roundTrip);
        Assert.Equal(checkpoint.Fingerprint, roundTrip.Fingerprint);
    }

    [Fact]
    public async Task CSharp_deserialized_resume_rebinds_services_for_code_nodes()
    {
        var runtime = new ScriptedRuntime(Array.Empty<ScriptedResponse>());
        var client = new CircuitClientBuilder().UseRuntime(runtime).Build();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var definition = CircuitDefinition<Unit, string>.FromCode(
            "service-code",
            "1.0.0",
            async (context, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
                return (string)context.Services.GetService(typeof(string))!;
            });
        var firstServices = new SingleServiceProvider("first");
        await using var first = await client.StartAsync(definition, new Unit(), new AgentRunOptions { Services = firstServices });
        await started.Task;
        var saved = await first.CreateCheckpointAsync();
        Assert.True(saved.IsSuccess);
        var checkpoint = CircuitCheckpoint<string>.Deserialize(saved.Value.Serialize());
        release.TrySetResult();

        var secondServices = new SingleServiceProvider("second");
        await using var resumed = await client.ResumeAsync(definition, checkpoint, new ResumeOptions { Services = secondServices });
        CircuitResponse<string>? output = null;
        await foreach (var item in resumed.Events)
            if (item.Kind == CircuitEventKind.OutputProduced) output = item.Output;
        Assert.NotNull(output);
        Assert.Equal("second", output!.Value);
    }

    [Fact]
    public async Task CSharp_full_protocol_preserves_run_node_delta_and_response_fields()
    {
        var runtime = new ScriptedRuntime(new[] { ScriptedResponses.Stream(new[] { "\"do", "ne\"" }) });
        var client = new CircuitClientBuilder().UseRuntime(runtime).Build();
        var agent = new AgentDefinition("protocol-agent", "1.0.0", "Protocol", "Return output");
        var signature = new AgentSignature<string, string>("protocol", "1.0.0", "Protocol", "Return output");
        var circuit = CircuitDefinition<string, string>.FromAgent(agent, signature).Define("protocol-circuit", "2.1.0");
        await using var run = await client.StartAsync(circuit, "input");
        var events = new List<CircuitEvent<string>>();
        await foreach (var item in run.Events) events.Add(item);

        Assert.All(events, item => Assert.Equal(run.RunId, item.RunId));

        var started = Assert.Single(events, item => item.Kind == CircuitEventKind.RunStarted);
        Assert.Equal(run.RunId, started.Run!.RunId);
        Assert.Equal("protocol-circuit", started.Run.DefinitionId);
        Assert.Equal("2.1.0", started.Run.DefinitionVersion);
        Assert.False(string.IsNullOrWhiteSpace(started.Run.LineageId));
        Assert.False(string.IsNullOrWhiteSpace(started.Run.Fingerprint));
        Assert.NotEqual(default, started.Run.StartedAt);

        var nodeStarted = Assert.Single(events, item => item.Kind == CircuitEventKind.NodeStarted);
        Assert.Equal("protocol-agent.protocol", nodeStarted.Node!.NodeId);
        Assert.Equal(1, nodeStarted.Node.Attempt);
        Assert.NotEqual(default, nodeStarted.Node.Timestamp);

        var deltas = events.Where(item => item.Kind == CircuitEventKind.OutputDelta).ToArray();
        Assert.Equal(2, deltas.Length);
        Assert.All(deltas, item =>
        {
            Assert.NotNull(item.Delta);
            Assert.Equal(item.NodePath, item.Delta!.NodePath);
            Assert.NotEqual(default, item.Delta.Timestamp);
        });

        var completed = Assert.Single(events, item => item.Kind == CircuitEventKind.NodeCompleted);
        Assert.True(completed.NodeResponse!.IsSuccess);
        Assert.Null(completed.NodeResponse.Failure);
        Assert.Equal(completed.Node!.NodePath, completed.NodeResponse.Metadata.NodePath);
        Assert.Equal(run.RunId, completed.NodeResponse.Metadata.RunId);
        Assert.False(string.IsNullOrWhiteSpace(completed.NodeResponse.Metadata.IdempotencyKey));
        Assert.NotEqual(default, completed.NodeResponse.Metadata.StartedAt);
        Assert.NotEqual(default, completed.NodeResponse.Metadata.CompletedAt);
        Assert.True(Assert.Single(events, item => item.Kind == CircuitEventKind.RunCompleted).Terminal!.IsSuccess);
    }

    [Fact]
    public void CSharp_failure_enum_maps_every_Core_failure_code()
    {
        Assert.Equal(Enumerable.Range(0, 16), Enum.GetValues<CircuitFailureCode>().Select(value => (int)value));
    }

    [Fact]
    public void CSharpDefinitionIsImmutableAndFingerprintChangesWithVersion()
    {
        var agent = new AgentDefinition("echo-agent", "1.0.0", "Echo", "Echo input");
        var signature = new AgentSignature<string, string>("echo", "1.0.0", "Echo", "Return output");
        var original = CircuitDefinition<string, string>.FromAgent(agent, signature).Define("echo-circuit", "1.0.0");
        var changed = original.Define("echo-circuit", "2.0.0");

        Assert.Equal("1.0.0", original.Version);
        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
    }

    private sealed class OnePageSource : IResumableCircuitSource<Unit, int>
    {
        public ValueTask<CircuitSourcePage<int>> ReadAsync(Unit input, string? continuationToken, CancellationToken cancellationToken)
            => ValueTask.FromResult(new CircuitSourcePage<int>(new[] { 4, 5 }, null, true));
    }

    private readonly record struct Unit;

    private sealed class SingleServiceProvider(object value) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == value.GetType() ? value : null;
    }

    private static void InspectType(Type type, string location, ISet<string> forbidden, ISet<Type> visited)
    {
        Inspect(type, location, forbidden, visited);
        if (!visited.Add(type)) return;
        if (type.BaseType is not null && type.BaseType != typeof(object)) Inspect(type.BaseType, $"{location}:base", forbidden, visited);
        foreach (var contract in type.GetInterfaces()) Inspect(contract, $"{location}:interface", forbidden, visited);
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var parameter in constructor.GetParameters()) Inspect(parameter.ParameterType, $"{type.FullName}.#ctor({parameter.Name})", forbidden, visited);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue;
            Inspect(method.ReturnType, $"{type.FullName}.{method.Name}:return", forbidden, visited);
            foreach (var parameter in method.GetParameters()) Inspect(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})", forbidden, visited);
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            Inspect(property.PropertyType, $"{type.FullName}.{property.Name}", forbidden, visited);
    }

    private static void Inspect(Type type, string location, ISet<string> forbidden, ISet<Type> visited)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer) { Inspect(type.GetElementType()!, location, forbidden, visited); return; }
        if (type.IsGenericParameter) return;
        var candidate = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var name = candidate.FullName ?? candidate.Name;
        if (name.StartsWith("Circuit.Core.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.FSharp.", StringComparison.Ordinal)
            || name.Contains("FSharp", StringComparison.Ordinal))
            forbidden.Add($"{location} -> {name}");
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) Inspect(argument, location, forbidden, visited);
    }

    [Fact]
    public async Task CSharp_graph_approval_metadata_survives_checkpoint_resume()
    {
        var client = new CircuitClientBuilder().UseRuntime(new ScriptedRuntime(Array.Empty<ScriptedResponse>())).Build();
        var metadata = new Dictionary<string, string> { ["route"] = "security", ["audit"] = "required" };
        var definition = CircuitDefinition<string, ApprovalResponse>.Approval(
            "metadata-approval", "1.0.0", value => new ApprovalPrompt("Review", value, metadata));

        var first = await client.StartAsync(definition, "inspect");
        CircuitCheckpoint<ApprovalResponse>? checkpoint = null;
        await foreach (var item in first.Events)
        {
            if (item.Kind != CircuitEventKind.ApprovalRequested) continue;
            Assert.NotNull(item.Approval!.Prompt);
            Assert.Equal("Review", item.Approval.Prompt!.Title);
            Assert.Equal("inspect", item.Approval.Prompt.Message);
            Assert.Equal("security", item.Approval.Prompt.Metadata["route"]);
            Assert.Equal("required", item.Approval.Prompt.Metadata["audit"]);
            var saved = await first.CreateCheckpointAsync();
            Assert.True(saved.IsSuccess);
            checkpoint = CircuitCheckpoint<ApprovalResponse>.Deserialize(saved.Value.Serialize());
            break;
        }
        await first.DisposeAsync();
        Assert.NotNull(checkpoint);

        await using var resumed = await client.ResumeAsync(definition, checkpoint!);
        var sawPrompt = false;
        await foreach (var item in resumed.Events)
        {
            if (item.Kind == CircuitEventKind.ApprovalRequested)
            {
                sawPrompt = true;
                Assert.Equal("security", item.Approval!.Prompt!.Metadata["route"]);
                Assert.Equal("required", item.Approval.Prompt.Metadata["audit"]);
                Assert.True((await resumed.RespondAsync(new ApprovalResponse(item.Approval.RequestId, true))).IsSuccess);
            }
        }
        Assert.True(sawPrompt);
    }

    [Fact]
    public void CSharp_graph_descriptor_and_durable_identifier_validation_have_parity()
    {
        var source = CircuitDefinition<Unit, int>.Items("graph-items", "1.0.0", _ => new[] { 1, 2 });
        var dynamic = source.ThenDynamic(
            "graph-dynamic", "2.0.0", value => value.ToString(), 3,
            value => CircuitDefinition<int, int>.Value(value));
        var definition = dynamic.Named("graph-root");

        Assert.True(definition.Graph.IsValid);
        Assert.Equal(definition.Fingerprint, definition.Graph.Fingerprint);
        Assert.Equal(CircuitCardinality.Many, definition.Graph.Cardinality);
        Assert.Contains(definition.Graph.Nodes, node => node.Kind == CircuitNodeKind.Dynamic && node.ConcurrencyLimit == 3);
        Assert.Contains(definition.Graph.Nodes, node => node.Kind == CircuitNodeKind.Items && node.Id == "graph-items");
        Assert.Throws<NotSupportedException>(() => ((IList<CircuitNodeDescriptor>)definition.Graph.Nodes).Add(definition.Graph.Nodes[0]));

        Assert.Throws<ArgumentException>(() => source.ThenDynamic(
            "bad/id|x", "1.0.0", value => value.ToString(), 1,
            value => CircuitDefinition<int, int>.Value(value)));
        Assert.Throws<ArgumentException>(() => source.ThenDynamic(
            "dynamic", "changed", value => value.ToString(), 1,
            value => CircuitDefinition<int, int>.Value(value)));
        Assert.Throws<ArgumentException>(() => source.Recover("bad/id", "1.0.0", _ => 0));
        Assert.Throws<ArgumentException>(() => source.Aggregate("aggregate", "v1", (_, _, _) => Task.FromResult(0)));
        Assert.Throws<ArgumentException>(() => source.Named("bad/name"));
    }

}
