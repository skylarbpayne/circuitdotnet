using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circuit;
using Circuit.MicrosoftAgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Xunit;

namespace Circuit.Interop.Tests;

public sealed class SmokeTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task construction_and_execution_work_without_referencing_Circuit_Core()
    {
        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);

        var client = new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(chatClient)
            .ConfigureMicrosoftAgentFramework(options => options.DefaultModelId = "test-model")
            .Build();

        var agent = new AgentDefinition("ping.agent", "1.0.0", "Ping", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("ping.signature", "1.0.0", "Ping", "Return pong.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.RunAsync(
            agent: agent,
            signature: signature,
            input: new PingInput { Message = "ping" },
            cancellationToken: cts.Token);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("pong", result.Result.Value!.Message);
        Assert.NotNull(result.Session);
    }

    [Fact]
    public async Task named_optional_parameters_and_cancellation_are_idiomatic()
    {
        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);

        var client = new CircuitClientBuilder().UseMicrosoftAgentFramework(chatClient).Build();
        var agent = new AgentDefinition("named.agent", "1.0.0", "Named", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("named.signature", "1.0.0", "Named", "Return pong.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new AgentRunOptions { Tags = new Dictionary<string, string> { ["scenario"] = "named" } };

        var result = await client.RunAsync(
            agent: agent,
            signature: signature,
            input: new PingInput { Message = "ping" },
            options: options,
            cancellationToken: cts.Token);

        Assert.True(result.Result.IsSuccess);

        var runAsync = typeof(IAgentClient).GetMethods().Single(method => method.Name == nameof(IAgentClient.RunAsync));
        var parameters = runAsync.GetParameters();
        Assert.Equal("options", parameters[^2].Name);
        Assert.True(parameters[^2].HasDefaultValue);
        Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
        Assert.Equal("cancellationToken", parameters[^1].Name);
        Assert.True(parameters[^1].HasDefaultValue);
    }

    [Fact]
    public void tools_use_a_vanilla_delegate_shape()
    {
        var constructor = typeof(ToolDefinition<ToolInput, ToolOutput>).GetConstructors().Single();
        var delegateParameter = constructor.GetParameters()[3];

        Assert.Equal(
            typeof(Func<ToolContext, ToolInput, CancellationToken, Task<ToolOutput>>),
            delegateParameter.ParameterType);

        _ = new ToolDefinition<ToolInput, ToolOutput>(
            "tool.echo",
            "1.0.0",
            "Echo",
            (context, input, cancellationToken) => Task.FromResult(new ToolOutput { Message = input.Message }));
    }

    [Fact]
    public void agent_run_options_validate_tag_constraints_before_crossing_the_core_boundary()
    {
        var toCore = typeof(AgentRunOptions).GetMethod("ToCore", BindingFlags.Instance | BindingFlags.NonPublic)!;

        static Exception InvokeToCore(MethodInfo toCoreMethod, AgentRunOptions options)
            => Assert.Throws<TargetInvocationException>(() => toCoreMethod.Invoke(options, null)).InnerException!;

        var tooMany = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary(Enumerable.Range(0, 33).Select(index => new KeyValuePair<string, string>($"tag-{index}", "value")))
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, tooMany)).ParamName);

        var reserved = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new("circuit.trace", "value")])
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, reserved)).ParamName);

        var longKey = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new(new string('k', 65), "value")])
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, longKey)).ParamName);

        var longValue = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new("team", new string('v', 257))])
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, longValue)).ParamName);

        var blankKey = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new(" ", "value")])
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, blankKey)).ParamName);

        var nullValue = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new KeyValuePair<string, string>("team", null!)])
        };
        Assert.Equal("tags", ((ArgumentNullException)InvokeToCore(toCore, nullValue)).ParamName);

        var duplicate = new AgentRunOptions
        {
            Tags = new EnumeratedTagDictionary([new("team", "one"), new("team", "two")])
        };
        Assert.Equal("tags", ((ArgumentException)InvokeToCore(toCore, duplicate)).ParamName);
    }

    [Fact]
    public void add_tool_validator_preserves_the_original_contract_serializer_options()
    {
        var options = Circuit.Core.CircuitJson.createOptions();
        options.PropertyNamingPolicy = new PrefixNamingPolicy("x_");
        options.Converters.Clear();
        options.Converters.Add(new JsonStringEnumConverter(new PrefixNamingPolicy("enum_")));
        options.MakeReadOnly();

        var input = Circuit.Core.Contract<ToolSchemaCarrier>.Create(options, []);
        var output = Circuit.Core.Contract<ToolSchemaCarrier>.Create(options, []);
        var coreTool = Circuit.Core.ToolDefinition<ToolSchemaCarrier, ToolSchemaCarrier>.Create(
            "tool.schema",
            "1.0.0",
            "Schema",
            input,
            output,
            new Func<Circuit.Core.ToolContext, ToolSchemaCarrier, Task<ToolSchemaCarrier>>((_, value) => Task.FromResult(value)));

        var constructor = typeof(ToolDefinition<ToolSchemaCarrier, ToolSchemaCarrier>)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info => info.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(Circuit.Core.ToolDefinition<ToolSchemaCarrier, ToolSchemaCarrier>));

        var wrapper = (ToolDefinition<ToolSchemaCarrier, ToolSchemaCarrier>)constructor.Invoke([coreTool]);
        var withInputValidator = wrapper.AddInputValidator(new NoOpValidator<ToolSchemaCarrier>());
        var withOutputValidator = wrapper.AddOutputValidator(new NoOpValidator<ToolSchemaCarrier>());

        Assert.Equal(wrapper.InputJsonSchema, withInputValidator.InputJsonSchema);
        Assert.Equal(wrapper.OutputJsonSchema, withOutputValidator.OutputJsonSchema);
        Assert.Contains("x_mode", withInputValidator.InputJsonSchema, StringComparison.Ordinal);
        Assert.Contains("enum_ready", withInputValidator.InputJsonSchema, StringComparison.Ordinal);
        Assert.Contains("x_mode", withOutputValidator.OutputJsonSchema, StringComparison.Ordinal);
        Assert.Contains("enum_ready", withOutputValidator.OutputJsonSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task streaming_events_expose_IAsyncEnumerable()
    {
        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);

        var client = new CircuitClientBuilder().UseMicrosoftAgentFramework(chatClient).Build();
        var agent = new AgentDefinition("stream.agent", "1.0.0", "Stream", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("stream.signature", "1.0.0", "Stream", "Return pong.");

        var stream = client.RunStreamingAsync(agent, signature, new PingInput { Message = "ping" });
        Assert.IsAssignableFrom<IAsyncEnumerable<AgentRunEvent<PongOutput>>>(stream);

        var events = new List<AgentRunEvent<PongOutput>>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        Assert.Contains(events, item => item.Kind == AgentRunEventKind.RunStarted);
        Assert.Contains(events, item => item.Kind == AgentRunEventKind.OutputDelta);
        Assert.Contains(events, item => item.Kind == AgentRunEventKind.RunCompleted && item.Value!.Message == "pong");
    }

    [Fact]
    public async Task public_workflow_checkpoint_round_trips_and_preserves_metadata()
    {
        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);
        var client = new CircuitClientBuilder().UseMicrosoftAgentFramework(chatClient).Build();

        var workflow = WorkflowDefinition<int, int>
            .Start("interop.checkpoint", "1.0.0", "start", (_, input, _) => Task.FromResult(input))
            .RequestApproval("approve", input => new ApprovalPrompt($"Approve {input}", "Continue?"))
            .Build();

        await using var run = await client.StartWorkflowAsync(workflow, 7);

        await foreach (var @event in run.Events)
        {
            if (@event.Kind == AgentRunEventKind.ApprovalRequested)
            {
                break;
            }
        }

        var checkpoint = await run.CreateCheckpointAsync();
        var serialized = checkpoint.Serialize().GetRawText();

        WorkflowCheckpoint<ApprovalResponse> restored;
        using (var freshDocument = JsonDocument.Parse(serialized))
        {
            restored = WorkflowCheckpoint<ApprovalResponse>.Deserialize(freshDocument.RootElement);
        }

        Assert.Equal("interop.checkpoint", restored.DefinitionId);
        Assert.Equal("1.0.0", restored.DefinitionVersion);
        Assert.Equal(checkpoint.CreatedAt, restored.CreatedAt);
        Assert.Equal(serialized, restored.Serialize().GetRawText());
    }

    [Fact]
    public async Task workflow_run_events_cancel_the_inner_stream_and_dispose_it()
    {
        var source = new BlockingRunEvents<int>();
        var run = CreateWorkflowRun(source);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumeTask = ConsumeAsync(run, cts.Token);

        await source.MoveNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await source.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(cts.Token, source.ObservedCancellationToken);
        Assert.True(source.ObservedCancellationToken.CanBeCanceled);
        Assert.True(source.ObservedCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void public_signatures_do_not_expose_core_or_fsharp_specific_types()
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal);
        var assembly = typeof(ICircuitClient).Assembly;
        var visited = new HashSet<Type>();

        foreach (var type in assembly.GetExportedTypes())
        {
            InspectType(type, $"T:{type.FullName}", forbidden, visited);
        }

        Assert.True(forbidden.Count == 0, string.Join(Environment.NewLine, forbidden.OrderBy(static entry => entry, StringComparer.Ordinal)));
    }

    [Fact]
    public async Task workflow_parallel_aggregate_receives_the_active_cancellation_token()
    {
        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);

        var client = new CircuitClientBuilder().UseMicrosoftAgentFramework(chatClient).Build();

        var left = WorkflowDefinition<int, int>
            .Start("parallel.left", "1.0.0", "left.step", (context, input, cancellationToken) => Task.FromResult(input + 1))
            .Build();

        var right = WorkflowDefinition<int, int>
            .Start("parallel.right", "1.0.0", "right.step", (context, input, cancellationToken) => Task.FromResult(input + 2))
            .Build();

        var aggregateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aggregateCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aggregateToken = CancellationToken.None;

        var workflow = WorkflowDefinition<int, int>
            .Start("parallel.root", "1.0.0", "seed", (context, input, cancellationToken) => Task.FromResult(input))
            .Parallel("parallel.step", 2, [left, right], async (values, cancellationToken) =>
            {
                aggregateToken = cancellationToken;
                aggregateStarted.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return values.Sum();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    aggregateCancelled.TrySetResult();
                    throw;
                }
            })
            .Build();

        using var cts = new CancellationTokenSource();
        var runTask = client.RunWorkflowAsync(workflow, 1, cancellationToken: cts.Token);

        await aggregateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(aggregateToken.CanBeCanceled);
        Assert.NotEqual(CancellationToken.None, aggregateToken);

        cts.Cancel();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Result.IsSuccess);
        Assert.Equal(AgentFailureCode.Cancelled, result.Result.Failure!.Code);
        await aggregateCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task add_circuit_snapshots_options_at_registration_time()
    {
        string? observedModelId = null;

        using var chatClient = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"],
            onRequest: options => observedModelId = options?.ModelId);

        CircuitOptions? captured = null;
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(chatClient);
        services.AddCircuit(options =>
        {
            captured = options;
            options.MicrosoftAgentFramework.DefaultModelId = "initial-model";
        });

        captured!.MicrosoftAgentFramework.DefaultModelId = "mutated-model";

        await using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<CircuitOptions>();
        Assert.Equal("initial-model", registered.MicrosoftAgentFramework.DefaultModelId);

        var client = provider.GetRequiredService<ICircuitClient>();
        var agent = new AgentDefinition("snapshot.agent", "1.0.0", "Snapshot", "Return pong.");
        var signature = new AgentSignature<PingInput, PongOutput>("snapshot.signature", "1.0.0", "Snapshot", "Return pong.");

        var result = await client.RunAsync(agent, signature, new PingInput { Message = "ping" });

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("initial-model", observedModelId);
    }

    [Fact]
    public async Task add_circuit_default_registrations_share_the_same_runtime_and_options_snapshot()
    {
        string? observedModelId = null;

        using var chatClient = CreateNamedChatClient("pong", modelId => observedModelId = modelId);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(chatClient);
        services.AddCircuit(options => options.MicrosoftAgentFramework.DefaultModelId = "shared-model");

        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<Circuit.Core.ICircuitRuntime>();
        var workflowRuntime = provider.GetRequiredService<Circuit.Core.IWorkflowRuntime>();
        var client = provider.GetRequiredService<ICircuitClient>();

        Assert.Same(runtime, workflowRuntime);
        Assert.Same(runtime, GetClientRuntime(client));

        var result = await client.RunAsync(CreatePingAgent("shared.agent"), CreatePingSignature("shared.signature"), new PingInput { Message = "ping" });

        Assert.True(result.Result.IsSuccess);
        Assert.Equal("pong", result.Result.Value!.Message);
        Assert.Equal("shared-model", observedModelId);
    }

    [Theory]
    [MemberData(nameof(AddCircuitOverrideScenarios))]
    public async Task add_circuit_preserves_try_add_overrides_without_divergence_or_cast_failures(AddCircuitOverrideScenario scenario)
    {
        string? observedDefaultModelId = null;

        using var defaultChatClient = CreateNamedChatClient("default-chat", modelId => observedDefaultModelId = modelId);
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(defaultChatClient);

        CircuitOptions? existingOptions = null;
        if (scenario.OverrideOptions)
        {
            existingOptions = new CircuitOptions();
            existingOptions.MicrosoftAgentFramework.DefaultModelId = "override-model";
            services.AddSingleton(existingOptions);
        }

        Circuit.Core.ICircuitRuntime? runtimeOverride = null;
        if (scenario.RuntimeOverrideKind == RuntimeOverrideKind.AgentOnly)
        {
            runtimeOverride = new AgentOnlyRuntime(CreateMafRuntime("runtime-override"));
            services.AddSingleton(runtimeOverride);
        }
        else if (scenario.RuntimeOverrideKind == RuntimeOverrideKind.AgentAndWorkflow)
        {
            runtimeOverride = CreateMafRuntime("runtime-override");
            services.AddSingleton(runtimeOverride);
        }

        Circuit.Core.IWorkflowRuntime? workflowRuntimeOverride = null;
        if (scenario.OverrideWorkflowRuntime)
        {
            workflowRuntimeOverride = new WorkflowOnlyRuntime(CreateMafRuntime("workflow-override"));
            services.AddSingleton(workflowRuntimeOverride);
        }

        ICircuitClient? clientOverride = null;
        if (scenario.OverrideClient)
        {
            clientOverride = CreateNamedClient("client-override");
            services.AddSingleton(clientOverride);
        }

        services.AddCircuit(options => options.MicrosoftAgentFramework.DefaultModelId = "configured-model");

        await using var provider = services.BuildServiceProvider();

        var registeredOptions = provider.GetRequiredService<CircuitOptions>();
        if (existingOptions is null)
        {
            Assert.Equal("configured-model", registeredOptions.MicrosoftAgentFramework.DefaultModelId);
        }
        else
        {
            Assert.Same(existingOptions, registeredOptions);
        }

        var runtime = provider.GetRequiredService<Circuit.Core.ICircuitRuntime>();
        if (runtimeOverride is not null)
        {
            Assert.Same(runtimeOverride, runtime);
        }

        var workflowRuntime = provider.GetRequiredService<Circuit.Core.IWorkflowRuntime>();
        if (workflowRuntimeOverride is not null)
        {
            Assert.Same(workflowRuntimeOverride, workflowRuntime);
        }
        else if (scenario.RuntimeOverrideKind != RuntimeOverrideKind.AgentOnly)
        {
            Assert.Same(runtime, workflowRuntime);
        }

        var client = provider.GetRequiredService<ICircuitClient>();
        Assert.Same(client, provider.GetRequiredService<IAgentClient>());
        Assert.Same(client, provider.GetRequiredService<IWorkflowClient>());

        if (clientOverride is not null)
        {
            Assert.Same(clientOverride, client);
        }
        else
        {
            Assert.Same(runtime, GetClientRuntime(client));
        }

        var agentResult = await client.RunAsync(CreatePingAgent("matrix.agent"), CreatePingSignature("matrix.signature"), new PingInput { Message = "ping" });
        Assert.True(agentResult.Result.IsSuccess);
        Assert.Equal(GetExpectedAgentLabel(scenario), agentResult.Result.Value!.Message);

        if (!scenario.OverrideClient && scenario.RuntimeOverrideKind == RuntimeOverrideKind.AgentOnly && !scenario.OverrideWorkflowRuntime)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await client.RunWorkflowAsync(CreatePingWorkflow("matrix.workflow"), new PingInput { Message = "ping" }));

            Assert.Equal(
                "The registered Circuit runtime does not support workflows. Register an IWorkflowRuntime to enable workflow operations.",
                exception.Message);
        }
        else
        {
            var workflowResult = await client.RunWorkflowAsync(CreatePingWorkflow("matrix.workflow"), new PingInput { Message = "ping" });
            Assert.True(workflowResult.Result.IsSuccess);
            Assert.Equal(GetExpectedWorkflowLabel(scenario), workflowResult.Result.Value!.Message);
        }

        if (!scenario.OverrideClient && scenario.RuntimeOverrideKind == RuntimeOverrideKind.None)
        {
            Assert.Equal(existingOptions?.MicrosoftAgentFramework.DefaultModelId ?? "configured-model", observedDefaultModelId);
        }
    }

    [Fact]
    public async Task middleware_wrapped_runtime_does_not_throw_InvalidCastException()
    {
        using var inner = new FakeChatClient(
            onResponse: _ => "{\"message\":\"pong\"}",
            onStreamingResponse: _ => ["{\"message\":\"pong\"}"]);

        var services = new ServiceCollection().BuildServiceProvider();
        var wrapped = new ChatClientBuilder(inner)
            .Use(client => client)
            .Build(services);

        var circuitServices = new ServiceCollection();
        circuitServices.AddSingleton<IChatClient>(wrapped);
        circuitServices.AddCircuit(_ => { });

        await using var provider = circuitServices.BuildServiceProvider();
        var client = provider.GetRequiredService<ICircuitClient>();

        var result = await client.RunAsync(CreatePingAgent("wrapped.agent"), CreatePingSignature("wrapped.signature"), new PingInput { Message = "ping" });
        Assert.True(result.Result.IsSuccess);
        Assert.Equal("pong", result.Result.Value!.Message);
    }

    private static void InspectType(Type type, string location, ISet<string> forbidden, ISet<Type> visited)
    {
        Inspect(type, location, forbidden, visited);

        if (!visited.Add(type))
        {
            return;
        }

        if (type.BaseType is not null && type.BaseType != typeof(object))
        {
            Inspect(type.BaseType, $"{location}:base", forbidden, visited);
        }

        foreach (var implementedInterface in type.GetInterfaces())
        {
            Inspect(implementedInterface, $"{location}:interface", forbidden, visited);
        }

        foreach (var nestedType in type.GetNestedTypes(BindingFlags.Public))
        {
            InspectType(nestedType, $"{location}+{nestedType.Name}", forbidden, visited);
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Inspect(parameter.ParameterType, $"{type.FullName}.#ctor({parameter.Name})", forbidden, visited);
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName)
            {
                continue;
            }

            Inspect(method.ReturnType, $"{type.FullName}.{method.Name}:return", forbidden, visited);
            foreach (var parameter in method.GetParameters())
            {
                Inspect(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})", forbidden, visited);
            }

            foreach (var genericArgument in method.GetGenericArguments())
            {
                foreach (var constraint in genericArgument.GetGenericParameterConstraints())
                {
                    Inspect(constraint, $"{type.FullName}.{method.Name}<{genericArgument.Name}>", forbidden, visited);
                }
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Inspect(property.PropertyType, $"{type.FullName}.{property.Name}", forbidden, visited);
        }
    }

    private static void Inspect(Type type, string location, ISet<string> forbidden, ISet<Type> visited)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            Inspect(type.GetElementType()!, location, forbidden, visited);
            return;
        }

        if (type.IsGenericParameter)
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
            {
                Inspect(constraint, $"{location}:{type.Name}", forbidden, visited);
            }

            return;
        }

        var candidate = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var fullName = candidate.FullName ?? candidate.Name;
        if (fullName.StartsWith("Circuit.Core.", StringComparison.Ordinal)
            || fullName.StartsWith("Microsoft.FSharp.", StringComparison.Ordinal)
            || fullName.Contains("FSharp", StringComparison.Ordinal)
            || fullName.StartsWith("System.Tuple`", StringComparison.Ordinal)
            || fullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal)
            || string.Equals(fullName, "Microsoft.FSharp.Core.Unit", StringComparison.Ordinal))
        {
            forbidden.Add($"{location} -> {fullName}");
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Inspect(argument, location, forbidden, visited);
            }
        }
    }

    public static IEnumerable<object[]> AddCircuitOverrideScenarios()
    {
        foreach (var overrideOptions in new[] { false, true })
        {
            foreach (var runtimeOverrideKind in Enum.GetValues<RuntimeOverrideKind>())
            {
                foreach (var overrideWorkflowRuntime in new[] { false, true })
                {
                    foreach (var overrideClient in new[] { false, true })
                    {
                        yield return [new AddCircuitOverrideScenario(overrideOptions, runtimeOverrideKind, overrideWorkflowRuntime, overrideClient)];
                    }
                }
            }
        }
    }

    private static FakeChatClient CreateNamedChatClient(string message, Action<string?>? onRequest = null)
        => new(
            onResponse: _ => $"{{\"message\":\"{message}\"}}",
            onStreamingResponse: _ => [$"{{\"message\":\"{message}\"}}"],
            onRequest: options => onRequest?.Invoke(options?.ModelId));

    private static MafRuntime CreateMafRuntime(string message)
    {
        var options = new MafRuntimeOptions();
        options.DefaultModelId = FSharpValueOption<string>.Some($"{message}-model");
        return new MafRuntime(CreateNamedChatClient(message), options);
    }

    private static ICircuitClient CreateNamedClient(string message)
        => new CircuitClientBuilder()
            .UseMicrosoftAgentFramework(CreateNamedChatClient(message))
            .Build();

    private static AgentDefinition CreatePingAgent(string id)
        => new(id, "1.0.0", "Ping", "Return the configured message.");

    private static AgentSignature<PingInput, PongOutput> CreatePingSignature(string id)
        => new(id, "1.0.0", "Ping", "Return the configured message.");

    private static WorkflowDefinition<PingInput, PongOutput> CreatePingWorkflow(string id)
        => WorkflowDefinition<PingInput, PongOutput>
            .Start(id, "1.0.0", $"{id}.agent", CreatePingAgent($"{id}.agent"), CreatePingSignature($"{id}.signature"))
            .Build();

    private static Circuit.Core.ICircuitRuntime GetClientRuntime(ICircuitClient client)
    {
        var property = client.GetType().GetProperty("Runtime", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<Circuit.Core.ICircuitRuntime>(property!.GetValue(client));
    }

    private static string GetExpectedAgentLabel(AddCircuitOverrideScenario scenario)
        => scenario.OverrideClient
            ? "client-override"
            : scenario.RuntimeOverrideKind == RuntimeOverrideKind.None
                ? "default-chat"
                : "runtime-override";

    private static string GetExpectedWorkflowLabel(AddCircuitOverrideScenario scenario)
        => scenario.OverrideClient
            ? "client-override"
            : scenario.OverrideWorkflowRuntime
                ? "workflow-override"
                : scenario.RuntimeOverrideKind == RuntimeOverrideKind.None
                    ? "default-chat"
                    : "runtime-override";

    public enum RuntimeOverrideKind
    {
        None,
        AgentOnly,
        AgentAndWorkflow,
    }

    public sealed record AddCircuitOverrideScenario(
        bool OverrideOptions,
        RuntimeOverrideKind RuntimeOverrideKind,
        bool OverrideWorkflowRuntime,
        bool OverrideClient)
    {
        public override string ToString()
            => $"Options={OverrideOptions},Runtime={RuntimeOverrideKind},Workflow={OverrideWorkflowRuntime},Client={OverrideClient}";
    }

    private sealed class AgentOnlyRuntime(MafRuntime inner) : Circuit.Core.ICircuitRuntime
    {
        public Task<Circuit.Core.RunResult<TOutput>> RunAsync<TInput, TOutput>(
            Circuit.Core.AgentDefinition agent,
            Circuit.Core.Signature<TInput, TOutput> signature,
            TInput input,
            Circuit.Core.RunOptions options,
            CancellationToken cancellationToken)
            => ((Circuit.Core.ICircuitRuntime)inner).RunAsync(agent, signature, input, options, cancellationToken);

        public IAsyncEnumerable<Circuit.Core.RunEvent<TOutput>> RunStreamingAsync<TInput, TOutput>(
            Circuit.Core.AgentDefinition agent,
            Circuit.Core.Signature<TInput, TOutput> signature,
            TInput input,
            Circuit.Core.RunOptions options,
            CancellationToken cancellationToken)
            => ((Circuit.Core.ICircuitRuntime)inner).RunStreamingAsync(agent, signature, input, options, cancellationToken);

        public ValueTask<JsonElement> SerializeSessionAsync(
            Circuit.Core.AgentDefinition agent,
            Circuit.Core.CircuitSession session,
            CancellationToken cancellationToken)
            => ((Circuit.Core.ICircuitRuntime)inner).SerializeSessionAsync(agent, session, cancellationToken);

        public ValueTask<Circuit.Core.CircuitSession> DeserializeSessionAsync(
            Circuit.Core.AgentDefinition agent,
            JsonElement state,
            CancellationToken cancellationToken)
            => ((Circuit.Core.ICircuitRuntime)inner).DeserializeSessionAsync(agent, state, cancellationToken);
    }

    private sealed class WorkflowOnlyRuntime(MafRuntime inner) : Circuit.Core.IWorkflowRuntime
    {
        public Task<Circuit.Core.RunResult<TOutput>> RunAsync<TInput, TOutput>(
            Circuit.Core.WorkflowDefinition<TInput, TOutput> definition,
            TInput input,
            Circuit.Core.WorkflowRunOptions options,
            CancellationToken cancellationToken)
            => ((Circuit.Core.IWorkflowRuntime)inner).RunAsync(definition, input, options, cancellationToken);

        public Task<Circuit.Core.WorkflowRun<TOutput>> StartAsync<TInput, TOutput>(
            Circuit.Core.WorkflowDefinition<TInput, TOutput> definition,
            TInput input,
            Circuit.Core.WorkflowRunOptions options,
            CancellationToken cancellationToken)
            => ((Circuit.Core.IWorkflowRuntime)inner).StartAsync(definition, input, options, cancellationToken);

        public Task<Circuit.Core.WorkflowRun<TOutput>> ResumeAsync<TInput, TOutput>(
            Circuit.Core.WorkflowDefinition<TInput, TOutput> definition,
            Circuit.Core.WorkflowCheckpoint<TOutput> checkpoint,
            CancellationToken cancellationToken)
            => ((Circuit.Core.IWorkflowRuntime)inner).ResumeAsync(definition, checkpoint, cancellationToken);
    }

    public sealed class PingInput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class PongOutput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class ToolInput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    public sealed class ToolOutput
    {
        [Required]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class EnumeratedTagDictionary(IEnumerable<KeyValuePair<string, string>> entries) : IReadOnlyDictionary<string, string>
    {
        private readonly KeyValuePair<string, string>[] _entries = entries.ToArray();

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var entry in _entries)
            {
                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => _entries.Length;

        public IEnumerable<string> Keys => _entries.Select(static entry => entry.Key);

        public IEnumerable<string> Values => _entries.Select(static entry => entry.Value);

        public bool ContainsKey(string key) => _entries.Any(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));

        public bool TryGetValue(string key, out string value)
        {
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        public string this[string key]
            => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    }

    private sealed class PrefixNamingPolicy(string prefix) : JsonNamingPolicy
    {
        public override string ConvertName(string name) => prefix + name.ToLowerInvariant();
    }

    private sealed class NoOpValidator<T> : IContractValidator<T>
    {
        public IReadOnlyList<ValidationIssue> Validate(T value) => [];
    }

    private enum ToolSchemaMode
    {
        Ready,
    }

    private sealed class ToolSchemaCarrier
    {
        [Required]
        public ToolSchemaMode Mode { get; set; } = ToolSchemaMode.Ready;
    }

    private static async Task ConsumeAsync<T>(WorkflowRun<T> run, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in run.Events.WithCancellation(cancellationToken))
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static WorkflowRun<T> CreateWorkflowRun<T>(IAsyncEnumerable<Circuit.Core.RunEvent<T>> events)
    {
        var coreRun = CreateCoreWorkflowRun(events);
        var constructor = typeof(WorkflowRun<T>).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (WorkflowRun<T>)constructor.Invoke([coreRun]);
    }

    private static object CreateCoreWorkflowRun<T>(IAsyncEnumerable<Circuit.Core.RunEvent<T>> events)
    {
        var constructor = typeof(Circuit.Core.WorkflowRun<T>).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return constructor.Invoke(
            [
                Circuit.Core.RunId.Parse("0123456789abcdef0123456789abcdef"),
                events,
                null,
                null,
                null
            ]);
    }

    private sealed class BlockingRunEvents<T> : IAsyncEnumerable<Circuit.Core.RunEvent<T>>
    {
        public CancellationToken ObservedCancellationToken { get; private set; }

        public TaskCompletionSource MoveNextStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerator<Circuit.Core.RunEvent<T>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ObservedCancellationToken = cancellationToken;
            return Enumerate(cancellationToken).GetAsyncEnumerator();
        }

        private async IAsyncEnumerable<Circuit.Core.RunEvent<T>> Enumerate(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MoveNextStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            finally
            {
                Disposed.TrySetResult();
            }

            yield break;
        }
    }

    private sealed class FakeChatClient(
        Func<IReadOnlyList<ChatMessage>, string> onResponse,
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<string>> onStreamingResponse,
        Action<ChatOptions?>? onRequest = null) : IChatClient, IDisposable
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.Select(message => message.Clone()).ToArray();
            onRequest?.Invoke(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, onResponse(snapshot))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.Select(message => message.Clone()).ToArray();
            onRequest?.Invoke(options);
            return Stream(onStreamingResponse(snapshot), cancellationToken);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Stream(
            IReadOnlyList<string> chunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
                await Task.Yield();
            }
        }
    }
}
