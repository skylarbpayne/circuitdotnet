#pragma warning disable CS1591

using System.Collections.ObjectModel;
using Circuit.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Circuit;

public sealed class WorkflowDefinition<TInput, TOutput>
{
    private readonly Circuit.Core.WorkflowDefinition<TInput, TOutput> _inner;

    internal WorkflowDefinition(Circuit.Core.WorkflowDefinition<TInput, TOutput> inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Id = inner.Id.Value;
        Version = inner.Version.ToString();
    }

    internal Circuit.Core.WorkflowDefinition<TInput, TOutput> Inner => _inner;

    public string Id { get; }

    public string Version { get; }

    public IReadOnlyList<WorkflowValidationIssue> Validate()
        => Array.AsReadOnly(Circuit.Core.Workflow.validate(_inner).Select(static issue => new WorkflowValidationIssue(issue)).ToArray());

    public static WorkflowBuilder<TInput, TOutput> Start(
        string id,
        string version,
        string stepId,
        Func<WorkflowContext, TInput, CancellationToken, Task<TOutput>> handler)
        => WorkflowBuilder<TInput, TOutput>.Start(id, version, stepId, handler);

    public static WorkflowBuilder<TInput, TOutput> Start(
        string id,
        string version,
        string stepId,
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature)
        => WorkflowBuilder<TInput, TOutput>.Start(id, version, stepId, agent, signature);
}

public sealed class WorkflowBuilder<TInput, TCurrent>
{
    private readonly string _id;
    private readonly string _version;
    private readonly Circuit.Core.WorkflowDefinition<TInput, TCurrent> _definition;

    private WorkflowBuilder(string id, string version, Circuit.Core.WorkflowDefinition<TInput, TCurrent> definition)
    {
        _id = id;
        _version = version;
        _definition = definition;
    }

    public static WorkflowBuilder<TInput, TOutput> Start<TOutput>(
        string id,
        string version,
        string stepId,
        Func<WorkflowContext, TInput, CancellationToken, Task<TOutput>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var step = Circuit.Core.Workflow.code<TInput, TOutput>(
            stepId,
            Curry<Circuit.Core.WorkflowContext, TInput, Task<TOutput>>(
                (context, input) => handler(new WorkflowContext(context), input, context.CancellationToken)));
        return new WorkflowBuilder<TInput, TOutput>(id, version, Circuit.Core.Workflow.define(id, version, step));
    }

    public static WorkflowBuilder<TInput, TOutput> Start<TOutput>(
        string id,
        string version,
        string stepId,
        AgentDefinition agent,
        AgentSignature<TInput, TOutput> signature)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(signature);
        var step = Circuit.Core.Workflow.agent<TInput, TOutput>(stepId, agent.Inner, signature.Inner);
        return new WorkflowBuilder<TInput, TOutput>(id, version, Circuit.Core.Workflow.define(id, version, step));
    }

    public WorkflowBuilder<TInput, TNext> Then<TNext>(
        string stepId,
        Func<WorkflowContext, TCurrent, CancellationToken, Task<TNext>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var step = Circuit.Core.Workflow.code<TCurrent, TNext>(
            stepId,
            Curry<Circuit.Core.WorkflowContext, TCurrent, Task<TNext>>(
                (context, input) => handler(new WorkflowContext(context), input, context.CancellationToken)));
        return new WorkflowBuilder<TInput, TNext>(_id, _version, Circuit.Core.Workflow.thenStep<TCurrent, TNext, TInput>(step, _definition));
    }

    public WorkflowBuilder<TInput, TNext> Then<TNext>(
        string stepId,
        AgentDefinition agent,
        AgentSignature<TCurrent, TNext> signature)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(signature);
        var step = Circuit.Core.Workflow.agent<TCurrent, TNext>(stepId, agent.Inner, signature.Inner);
        return new WorkflowBuilder<TInput, TNext>(_id, _version, Circuit.Core.Workflow.thenStep<TCurrent, TNext, TInput>(step, _definition));
    }

    public WorkflowBuilder<TInput, TNext> Choose<TNext>(
        string stepId,
        Func<TCurrent, string> selector,
        IReadOnlyDictionary<string, WorkflowDefinition<TCurrent, TNext>> cases,
        WorkflowDefinition<TCurrent, TNext>? defaultCase = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(cases);
        var branches = new FSharpMap<string, Circuit.Core.WorkflowDefinition<TCurrent, TNext>>(
            cases.Select(static pair => Tuple.Create(pair.Key, pair.Value.Inner)));
        var step = Circuit.Core.Workflow.choose(
            stepId,
            ToFSharp(selector),
            branches,
            defaultCase?.Inner);
        return new WorkflowBuilder<TInput, TNext>(_id, _version, Circuit.Core.Workflow.thenStep<TCurrent, TNext, TInput>(step, _definition));
    }

    public WorkflowBuilder<TInput, TNext> Parallel<TBranch, TNext>(
        string stepId,
        int maxConcurrency,
        IReadOnlyList<WorkflowDefinition<TCurrent, TBranch>> branches,
        Func<IReadOnlyList<TBranch>, CancellationToken, Task<TNext>> aggregate)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(aggregate);
        var step = Circuit.Core.Workflow.parallelWithCancellation<TCurrent, TBranch, TNext>(
            stepId,
            maxConcurrency,
            ListModule.OfSeq(branches.Select(static branch => branch.Inner)),
            Curry<FSharpList<TBranch>, CancellationToken, Task<TNext>>((values, cancellationToken) =>
                aggregate(Array.AsReadOnly(values.ToArray()), cancellationToken)));
        return new WorkflowBuilder<TInput, TNext>(_id, _version, Circuit.Core.Workflow.thenStep<TCurrent, TNext, TInput>(step, _definition));
    }

    public WorkflowBuilder<TInput, ApprovalResponse> RequestApproval(
        string stepId,
        Func<TCurrent, ApprovalPrompt> prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var requestStep = Circuit.Core.Workflow.request<TCurrent>(
            stepId,
            ToFSharp<TCurrent, Circuit.Core.ApprovalPrompt>(value => prompt(value).ToCore()));
        var requestDefinition = Circuit.Core.Workflow.thenStep<TCurrent, Circuit.Core.ApprovalResponse, TInput>(requestStep, _definition);
        var mapStep = Circuit.Core.Workflow.code<Circuit.Core.ApprovalResponse, ApprovalResponse>(
            $"{stepId}.map",
            Curry<Circuit.Core.WorkflowContext, Circuit.Core.ApprovalResponse, Task<ApprovalResponse>>(
                (_context, response) => Task.FromResult(ApprovalResponse.FromCore(response))));
        return new WorkflowBuilder<TInput, ApprovalResponse>(_id, _version, Circuit.Core.Workflow.thenStep<Circuit.Core.ApprovalResponse, ApprovalResponse, TInput>(mapStep, requestDefinition));
    }

    public WorkflowBuilder<TInput, TCurrent> Loop(
        string stepId,
        int maxIterations,
        Func<TCurrent, bool> predicate,
        WorkflowDefinition<TCurrent, TCurrent> body)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(body);
        var step = Circuit.Core.Workflow.loop(stepId, maxIterations, ToFSharp(predicate), body.Inner);
        return new WorkflowBuilder<TInput, TCurrent>(_id, _version, Circuit.Core.Workflow.thenStep<TCurrent, TCurrent, TInput>(step, _definition));
    }

    public WorkflowDefinition<TInput, TCurrent> Build() => new(_definition);

    private static FSharpFunc<TArg, TResult> ToFSharp<TArg, TResult>(Func<TArg, TResult> func)
        => FSharpFunc<TArg, TResult>.FromConverter(new Converter<TArg, TResult>(func));

    private static FSharpFunc<T1, FSharpFunc<T2, TResult>> Curry<T1, T2, TResult>(Func<T1, T2, TResult> func)
        => FSharpFunc<T1, FSharpFunc<T2, TResult>>.FromConverter(
            new Converter<T1, FSharpFunc<T2, TResult>>(arg1 =>
                FSharpFunc<T2, TResult>.FromConverter(new Converter<T2, TResult>(arg2 => func(arg1, arg2)))));
}
