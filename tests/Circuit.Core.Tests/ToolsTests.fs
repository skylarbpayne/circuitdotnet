namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

type private ToolTestInput() =
    member val Value = 0 with get, set

type private ToolTestOutput() =
    member val Message = "" with get, set

type private BadExecutor
    (
        ?inputType: Type,
        ?outputType: Type,
        ?inputSchema: SchemaDocument,
        ?outputSchema: SchemaDocument,
        ?validateInput: obj -> IReadOnlyList<ValidationIssue>,
        ?validateOutput: obj -> IReadOnlyList<ValidationIssue>,
        ?invokeAsync: ToolContext * obj -> Task<obj>
    ) =
    let inputType = defaultArg inputType typeof<ToolTestInput>
    let outputType = defaultArg outputType typeof<ToolTestOutput>
    let inputSchema = defaultArg inputSchema (SchemaDocument(JsonObject()))
    let outputSchema = defaultArg outputSchema (SchemaDocument(JsonObject()))

    let validateInput =
        defaultArg validateInput (fun _ -> Array.empty<ValidationIssue> :> IReadOnlyList<ValidationIssue>)

    let validateOutput =
        defaultArg validateOutput (fun _ -> Array.empty<ValidationIssue> :> IReadOnlyList<ValidationIssue>)

    let invokeAsync =
        defaultArg invokeAsync (fun _ -> Task.FromResult(box (ToolTestOutput())))

    interface IResolvedToolExecutor with
        member _.InputType = inputType
        member _.OutputType = outputType
        member _.InputSchema = inputSchema
        member _.OutputSchema = outputSchema
        member _.ValidateInput(value) = validateInput value
        member _.ValidateOutput(value) = validateOutput value
        member _.InvokeAsync(context, value) = invokeAsync (context, value)

module ToolsTests =
    let private services =
        { new IServiceProvider with
            member _.GetService(_serviceType) = null }

    let private inputContract =
        Contract<ToolTestInput>.Create(CircuitJson.createOptions (), Seq.empty)

    let private outputContract =
        Contract<ToolTestOutput>.Create(CircuitJson.createOptions (), Seq.empty)

    let private createDefinition approval approvalPolicy invokeAsync =
        ToolDefinition<ToolTestInput, ToolTestOutput>
            .Create(
                "tool.read",
                "1.0.0",
                "Read a file.",
                inputContract,
                outputContract,
                approval,
                approvalPolicy,
                Func<ToolContext, ToolTestInput, Task<ToolTestOutput>>(invokeAsync)
            )

    [<Fact>]
    let ``tool contexts require services`` () =
        let runId = RunId.New()

        let context =
            ToolContext(runId, ValueSome("tenant-1"), ValueSome("user-1"), services, CancellationToken.None)

        Assert.Equal(runId, context.RunId)
        Assert.Equal(ValueSome("tenant-1"), context.TenantId)
        Assert.Equal(ValueSome("user-1"), context.UserId)
        Assert.Same(services, context.Services)

        let nullToolServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                ToolContext(runId, ValueNone, ValueNone, null, CancellationToken.None) |> ignore)

        Assert.Equal("services", nullToolServices.ParamName)

        let resolutionContext = ToolResolutionContext(runId, ValueNone, ValueNone, services)
        Assert.Equal(runId, resolutionContext.RunId)

        let nullResolutionServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                ToolResolutionContext(runId, ValueNone, ValueNone, null) |> ignore)

        Assert.Equal("services", nullResolutionServices.ParamName)

    [<Fact>]
    let ``tool definitions validate descriptions approval policies and default approval mode`` () =
        let byPolicy =
            createDefinition ApprovalMode.ByPolicy (ValueSome("host-approval")) (fun _ input ->
                Task.FromResult(ToolTestOutput(Message = $"{input.Value}")))

        Assert.Equal(ApprovalMode.ByPolicy, byPolicy.Approval)
        Assert.Equal(ValueSome("host-approval"), byPolicy.ApprovalPolicy)

        let defaultApproval =
            ToolDefinition<ToolTestInput, ToolTestOutput>
                .Create(
                    "tool.default",
                    "1.0.0",
                    "Default approval.",
                    inputContract,
                    outputContract,
                    Func<ToolContext, ToolTestInput, Task<ToolTestOutput>>(fun _ _ -> Task.FromResult(ToolTestOutput()))
                )

        Assert.Equal(ApprovalMode.Always, defaultApproval.Approval)
        Assert.Equal(ValueNone, defaultApproval.ApprovalPolicy)

        let blankDescription =
            Assert.Throws<ArgumentException>(fun () ->
                ToolDefinition<ToolTestInput, ToolTestOutput>
                    .Create(
                        "tool.blank",
                        "1.0.0",
                        " ",
                        inputContract,
                        outputContract,
                        ApprovalMode.Never,
                        ValueNone,
                        Func<ToolContext, ToolTestInput, Task<ToolTestOutput>>(fun _ _ ->
                            Task.FromResult(ToolTestOutput()))
                    )
                |> ignore)

        Assert.Equal("description", blankDescription.ParamName)

        let blankPolicy =
            Assert.Throws<ArgumentException>(fun () ->
                createDefinition ApprovalMode.ByPolicy (ValueSome(" ")) (fun _ _ -> Task.FromResult(ToolTestOutput()))
                |> ignore)

        Assert.Equal("approvalPolicy", blankPolicy.ParamName)

        let misplacedPolicy =
            Assert.Throws<ArgumentException>(fun () ->
                createDefinition ApprovalMode.Always (ValueSome("host-approval")) (fun _ _ ->
                    Task.FromResult(ToolTestOutput()))
                |> ignore)

        Assert.Equal("approvalPolicy", misplacedPolicy.ParamName)

    [<Fact>]
    let ``resolved tools validate tag inputs type mismatches and invocation types`` () =
        let definition =
            createDefinition ApprovalMode.Never ValueNone (fun _ input ->
                Task.FromResult(ToolTestOutput(Message = $"value:{input.Value}")))

        let tool =
            ResolvedTool.Create(
                definition,
                seq {
                    "io.read"
                    "safe"
                }
            )

        let context =
            ToolContext(RunId.New(), ValueNone, ValueNone, services, CancellationToken.None)

        Assert.Contains("io.read", tool.Tags)
        Assert.Contains("safe", tool.Tags)

        let inputIssues = tool.ValidateInput("wrong")
        Assert.Single(inputIssues) |> ignore
        Assert.Equal("type", inputIssues[0].Code)
        Assert.Equal("$", inputIssues[0].Path)

        let outputIssues = tool.ValidateOutput(5)
        Assert.Single(outputIssues) |> ignore
        Assert.Equal("type", outputIssues[0].Code)

        let output =
            tool.InvokeAsync(context, ToolTestInput(Value = 7)).Result :?> ToolTestOutput

        Assert.Equal("value:7", output.Message)

        let invalidInvocation =
            Assert.Throws<AggregateException>(fun () -> tool.InvokeAsync(context, "wrong").Result |> ignore)

        Assert.Contains("invalid input type", invalidInvocation.InnerException.Message)

        let duplicateTag =
            Assert.Throws<ArgumentException>(fun () ->
                ResolvedTool.Create(
                    definition,
                    seq {
                        "safe"
                        "safe"
                    }
                )
                |> ignore)

        Assert.Equal("tags", duplicateTag.ParamName)

        let invalidTag =
            Assert.Throws<ArgumentException>(fun () -> ResolvedTool.Create(definition, seq { "Safe" }) |> ignore)

        Assert.Equal("tags", invalidTag.ParamName)

    [<Fact>]
    let ``resolved tools surface executor task failures`` () =
        let definition =
            createDefinition ApprovalMode.Never ValueNone (fun _ _ ->
                Task.FromException<ToolTestOutput>(InvalidOperationException("tool failed")))

        let tool = ResolvedTool.Create(definition)

        let context =
            ToolContext(RunId.New(), ValueNone, ValueNone, services, CancellationToken.None)

        let ex =
            Assert.Throws<AggregateException>(fun () ->
                tool.InvokeAsync(context, ToolTestInput(Value = 1)).Result |> ignore)

        Assert.Equal("tool failed", ex.InnerException.Message)

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let canceledDefinition =
            createDefinition ApprovalMode.Never ValueNone (fun _ _ ->
                Task.FromCanceled<ToolTestOutput>(cancelled.Token))

        let canceledTool = ResolvedTool.Create(canceledDefinition)

        let canceled =
            Assert.Throws<AggregateException>(fun () ->
                canceledTool.InvokeAsync(context, ToolTestInput(Value = 2)).Result |> ignore)

        Assert.IsType<TaskCanceledException>(canceled.InnerException) |> ignore

    [<Fact>]
    let ``tool resolution handles canceled and faulting resolver tasks`` () =
        let context = ToolResolutionContext(RunId.New(), ValueNone, ValueNone, services)
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let canceledResolver =
            DelegateToolResolver(
                Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedTool>>(
                        Task.FromCanceled<IReadOnlyList<ResolvedTool>>(cancelled.Token)
                    ))
            )
            :> IToolResolver

        let canceled =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| canceledResolver |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(canceled.InnerException) |> ignore

        let faultingResolver =
            DelegateToolResolver(
                Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>(fun _ _ ->
                    raise (InvalidOperationException("resolver blew up")))
            )
            :> IToolResolver

        let faulted =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| faultingResolver |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("resolver blew up", faulted.InnerException.Message)

    [<Fact>]
    let ``delegate tool resolvers validate constructor arguments and invoke the callback`` () =
        let definition =
            createDefinition ApprovalMode.Never ValueNone (fun _ _ -> Task.FromResult(ToolTestOutput()))

        let tool = ResolvedTool.Create(definition)
        let runId = RunId.New()

        let context =
            ToolResolutionContext(runId, ValueSome("tenant-1"), ValueSome("user-1"), services)

        let mutable invoked = false

        let resolver =
            DelegateToolResolver(
                Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>
                    (fun innerContext ct ->
                        invoked <- true
                        Assert.Equal(runId, innerContext.RunId)
                        Assert.False(ct.IsCancellationRequested)
                        ValueTask<IReadOnlyList<ResolvedTool>>([| tool |] :> IReadOnlyList<ResolvedTool>))
            )
            :> IToolResolver

        let resolved = resolver.ResolveAsync(context, CancellationToken.None).Result

        Assert.True(invoked)
        Assert.Single(resolved) |> ignore

        let nullResolver =
            Assert.Throws<ArgumentNullException>(fun () -> DelegateToolResolver(null) |> ignore)

        Assert.Equal("resolver", nullResolver.ParamName)

        let nullTools =
            Assert.Throws<ArgumentNullException>(fun () -> StaticToolResolver(null) |> ignore)

        Assert.Equal("tools", nullTools.ParamName)

    [<Fact>]
    let ``tool resolution handles empty duplicate and invalid resolver outputs`` () =
        let definition =
            createDefinition ApprovalMode.Never ValueNone (fun _ _ -> Task.FromResult(ToolTestOutput()))

        let tool = ResolvedTool.Create(definition)
        let context = ToolResolutionContext(RunId.New(), ValueNone, ValueNone, services)

        let empty =
            ToolResolution.resolveAllAsync
                (Array.empty<IToolResolver> :> IReadOnlyList<IToolResolver>)
                context
                CancellationToken.None
            |> _.Result

        Assert.Empty(empty)

        let nullResolvers =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync null context CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("resolvers", (nullResolvers.InnerException :?> ArgumentNullException).ParamName)

        let nullContext =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| StaticToolResolver([| tool |]) :> IToolResolver |] :> IReadOnlyList<IToolResolver>)
                    Unchecked.defaultof<ToolResolutionContext>
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("context", (nullContext.InnerException :?> ArgumentNullException).ParamName)

        let nullResolverEntry =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| Unchecked.defaultof<IToolResolver> |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot contain null entries", nullResolverEntry.InnerException.Message)

        let nullToolListResolver =
            DelegateToolResolver(
                Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedTool>>(Unchecked.defaultof<IReadOnlyList<ResolvedTool>>))
            )
            :> IToolResolver

        let nullToolList =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| nullToolListResolver |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot return null tool lists", nullToolList.InnerException.Message)

        let nullToolEntryResolver =
            DelegateToolResolver(
                Func<ToolResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedTool>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedTool>>(
                        [| Unchecked.defaultof<ResolvedTool> |] :> IReadOnlyList<ResolvedTool>
                    ))
            )
            :> IToolResolver

        let nullToolEntry =
            Assert.Throws<AggregateException>(fun () ->
                ToolResolution.resolveAllAsync
                    ([| nullToolEntryResolver |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot return null tool entries", nullToolEntry.InnerException.Message)

        let distinctDefinition =
            ToolDefinition<ToolTestInput, ToolTestOutput>
                .Create(
                    "tool.read",
                    "2.0.0",
                    "Read a file again.",
                    inputContract,
                    outputContract,
                    ApprovalMode.Never,
                    ValueNone,
                    Func<ToolContext, ToolTestInput, Task<ToolTestOutput>>(fun _ _ -> Task.FromResult(ToolTestOutput()))
                )

        let resolved =
            ToolResolution.resolveAllAsync
                ([| StaticToolResolver([| tool; ResolvedTool.Create(distinctDefinition) |]) :> IToolResolver |]
                :> IReadOnlyList<IToolResolver>)
                context
                CancellationToken.None
            |> _.Result

        Assert.Equal(2, resolved.Count)

    [<Fact>]
    let ``tool constructors reject null dependencies and valid inputs round trip cleanly`` () =
        let invokeAsync =
            Func<ToolContext, ToolTestInput, Task<ToolTestOutput>>(fun _ input ->
                Task.FromResult(ToolTestOutput(Message = $"{input.Value}")))

        let nullInput =
            Assert.Throws<ArgumentNullException>(fun () ->
                ToolDefinition<ToolTestInput, ToolTestOutput>(
                    DefinitionId.Create("tool.null.input"),
                    SemanticVersion.Parse("1.0.0"),
                    "desc",
                    Unchecked.defaultof<Contract<ToolTestInput>>,
                    outputContract,
                    ApprovalMode.Never,
                    ValueNone,
                    invokeAsync
                )
                |> ignore)

        Assert.Equal("input", nullInput.ParamName)

        let nullOutput =
            Assert.Throws<ArgumentNullException>(fun () ->
                ToolDefinition<ToolTestInput, ToolTestOutput>(
                    DefinitionId.Create("tool.null.output"),
                    SemanticVersion.Parse("1.0.0"),
                    "desc",
                    inputContract,
                    Unchecked.defaultof<Contract<ToolTestOutput>>,
                    ApprovalMode.Never,
                    ValueNone,
                    invokeAsync
                )
                |> ignore)

        Assert.Equal("output", nullOutput.ParamName)

        let nullInvoke =
            Assert.Throws<ArgumentNullException>(fun () ->
                ToolDefinition<ToolTestInput, ToolTestOutput>(
                    DefinitionId.Create("tool.null.invoke"),
                    SemanticVersion.Parse("1.0.0"),
                    "desc",
                    inputContract,
                    outputContract,
                    ApprovalMode.Never,
                    ValueNone,
                    null
                )
                |> ignore)

        Assert.Equal("invokeAsync", nullInvoke.ParamName)

        let nullDefinition =
            Assert.Throws<ArgumentNullException>(fun () ->
                ResolvedTool.Create(Unchecked.defaultof<ToolDefinition<ToolTestInput, ToolTestOutput>>)
                |> ignore)

        Assert.Equal("definition", nullDefinition.ParamName)

        let nullTags =
            Assert.Throws<ArgumentNullException>(fun () ->
                ResolvedTool.Create(
                    createDefinition ApprovalMode.Never ValueNone (fun _ _ -> Task.FromResult(ToolTestOutput())),
                    null
                )
                |> ignore)

        Assert.Equal("tags", nullTags.ParamName)

        let tool =
            ResolvedTool.Create(
                createDefinition ApprovalMode.Never ValueNone (fun _ input ->
                    Task.FromResult(ToolTestOutput(Message = string input.Value)))
            )

        Assert.Empty(tool.ValidateInput(ToolTestInput(Value = 5)))
        Assert.Empty(tool.ValidateOutput(ToolTestOutput(Message = "5")))

    [<Fact>]
    let ``tool resolution rejects malformed resolved tool metadata`` () =
        let context = ToolResolutionContext(RunId.New(), ValueNone, ValueNone, services)

        let emptyTags = HashSet<string>(StringComparer.Ordinal) :> IReadOnlySet<string>

        let invalidCases =
            [ "tool.Description cannot be blank.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.description"),
                  SemanticVersion.Parse("1.0.0"),
                  null,
                  ApprovalMode.Never,
                  ValueNone,
                  emptyTags,
                  BadExecutor()
              )
              "Resolved tool tags cannot be null.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.tags"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueNone,
                  Unchecked.defaultof<IReadOnlySet<string>>,
                  BadExecutor()
              )
              "approvalPolicy can only be set when approval is ByPolicy.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.approval"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueSome("host-policy"),
                  emptyTags,
                  BadExecutor()
              )
              "approvalPolicy cannot be blank when provided.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.approval.blank"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.ByPolicy,
                  ValueSome(" "),
                  emptyTags,
                  BadExecutor()
              )
              "Resolved tool input type cannot be null.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.input-type"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueNone,
                  emptyTags,
                  BadExecutor(inputType = Unchecked.defaultof<Type>)
              )
              "Resolved tool output type cannot be null.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.output-type"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueNone,
                  emptyTags,
                  BadExecutor(outputType = Unchecked.defaultof<Type>)
              )
              "Resolved tool input schema cannot be null.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.input-schema"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueNone,
                  emptyTags,
                  BadExecutor(inputSchema = Unchecked.defaultof<SchemaDocument>)
              )
              "Resolved tool output schema cannot be null.",
              ResolvedTool(
                  DefinitionId.Create("tool.bad.output-schema"),
                  SemanticVersion.Parse("1.0.0"),
                  "desc",
                  ApprovalMode.Never,
                  ValueNone,
                  emptyTags,
                  BadExecutor(outputSchema = Unchecked.defaultof<SchemaDocument>)
              ) ]

        for expectedMessage, tool in invalidCases do
            let resolver = StaticToolResolver([| tool |]) :> IToolResolver

            let ex =
                Assert.Throws<AggregateException>(fun () ->
                    ToolResolution.resolveAllAsync
                        ([| resolver |] :> IReadOnlyList<IToolResolver>)
                        context
                        CancellationToken.None
                    |> _.Result
                    |> ignore)

            Assert.Contains(expectedMessage, ex.InnerException.Message)
