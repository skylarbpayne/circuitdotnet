namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

type private EmptyAsyncEnumerable<'T>() =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            { new IAsyncEnumerator<'T> with
                member _.Current = Unchecked.defaultof<'T>
                member _.MoveNextAsync() = ValueTask<bool>(false)
                member _.DisposeAsync() = ValueTask() }

type private DelegateWorkflowRuntime
    (
        runAsync: obj -> obj -> WorkflowRunOptions -> CancellationToken -> obj,
        startAsync: obj -> obj -> WorkflowRunOptions -> CancellationToken -> obj,
        resumeAsync: obj -> obj -> CancellationToken -> obj
    ) =
    interface IWorkflowRuntime with
        member _.RunAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                input: 'Input,
                options: WorkflowRunOptions,
                ct: CancellationToken
            ) =
            Task.FromResult(runAsync (box definition) (box input) options ct :?> RunResult<'Output>)

        member _.StartAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                input: 'Input,
                options: WorkflowRunOptions,
                ct: CancellationToken
            ) =
            Task.FromResult(startAsync (box definition) (box input) options ct :?> WorkflowRun<'Output>)

        member _.ResumeAsync<'Input, 'Output>
            (
                definition: WorkflowDefinition<'Input, 'Output>,
                checkpoint: WorkflowCheckpoint<'Output>,
                ct: CancellationToken
            ) =
            Task.FromResult(resumeAsync (box definition) (box checkpoint) ct :?> WorkflowRun<'Output>)

module WorkflowsTests =
    let private createDefinition () =
        Workflow.define "workflow.test" "1.0.0" (Workflow.code "step.one" (fun _ value -> Task.FromResult(value + 1)))

    [<Fact>]
    let ``approval prompts and responses validate metadata and notes`` () =
        let prompt =
            ApprovalPrompt("Need approval", "Ship the change?", seq { KeyValuePair("severity", "high") })

        Assert.Equal("Need approval", prompt.Title)
        Assert.Equal("high", prompt.Metadata["severity"])

        let response = ApprovalResponse("request-1", true, "Looks good")
        Assert.True(response.Approved)
        Assert.Equal("Looks good", response.Note)

        let duplicateMetadata =
            Assert.Throws<ArgumentException>(fun () ->
                ApprovalPrompt(
                    "Need approval",
                    "Ship the change?",
                    seq {
                        KeyValuePair("severity", "high")
                        KeyValuePair("severity", "low")
                    }
                )
                |> ignore)

        Assert.Equal("metadata", duplicateMetadata.ParamName)

        let blankMetadataKey =
            Assert.Throws<ArgumentException>(fun () ->
                ApprovalPrompt("Need approval", "Ship the change?", seq { KeyValuePair(" ", "high") })
                |> ignore)

        Assert.Equal("metadata", blankMetadataKey.ParamName)

        let nullMetadataValue =
            Assert.Throws<ArgumentNullException>(fun () ->
                ApprovalPrompt("Need approval", "Ship the change?", seq { KeyValuePair("severity", null) })
                |> ignore)

        Assert.Equal("metadata", nullMetadataValue.ParamName)

        let blankNote =
            Assert.Throws<ArgumentException>(fun () -> ApprovalResponse("request-1", true, " ") |> ignore)

        Assert.Equal("note", blankNote.ParamName)

        let nullMessage =
            Assert.Throws<ArgumentNullException>(fun () -> ApprovalPrompt("Need approval", null, Seq.empty) |> ignore)

        Assert.Equal("message", nullMessage.ParamName)

    [<Fact>]
    let ``workflow builders validate arguments and expose executable handlers`` () =
        let codeStep = Workflow.code "step.code" (fun _ value -> Task.FromResult(value + 1))
        let codeNode = Assert.Single(codeStep.Fragment.Nodes)

        match codeNode.Kind with
        | WorkflowGraph.Code handler ->
            let context =
                WorkflowContext(
                    RunId.New(),
                    DefinitionId.Create("workflow.test"),
                    SemanticVersion.Parse("1.0.0"),
                    codeNode.Id,
                    CancellationToken.None
                )

            let output =
                handler.InvokeAsync(context, box 4, CancellationToken.None).Result :?> int

            Assert.Equal(5, output)
        | kind -> failwithf "Expected a code node but found %s" (WorkflowGraph.nodeKindName kind)

        let agent =
            AgentDefinition.Create(
                "agent.test",
                "1.0.0",
                "Agent",
                "Do the thing.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<int, int>
                .Create(
                    "signature.test",
                    "1.0.0",
                    "Signature",
                    "Return an integer.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let agentStep = Workflow.agent "step.agent" agent signature

        let requestStep =
            Workflow.request "step.request" (fun value -> ApprovalPrompt.Create($"Approve {value}?", "Continue"))

        Assert.Equal(1, agentStep.Fragment.Nodes.Length)
        Assert.Equal(2, requestStep.Fragment.Nodes.Length)

        let blankCodeId =
            Assert.Throws<ArgumentException>(fun () ->
                Workflow.code " " (fun _ value -> Task.FromResult value) |> ignore)

        Assert.Equal("id", blankCodeId.ParamName)

        let nullAgent =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.agent "step.agent" Unchecked.defaultof<AgentDefinition> signature
                |> ignore)

        Assert.Equal("agent", nullAgent.ParamName)

        let nullSignature =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.agent "step.agent" agent Unchecked.defaultof<Signature<int, int>>
                |> ignore)

        Assert.Equal("signature", nullSignature.ParamName)

        let nullPrompt =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.request "step.request" Unchecked.defaultof<int -> ApprovalPrompt>
                |> ignore)

        Assert.Equal("prompt", nullPrompt.ParamName)

    [<Fact>]
    let ``workflow code and aggregate handlers propagate task failures`` () =
        let codeHandler =
            WorkflowGraph.CodeHandler<int, int>(fun _ _ ->
                Task.FromException<int>(InvalidOperationException("code failed")))
            :> WorkflowGraph.ICodeHandler

        let codeContext =
            WorkflowContext(
                RunId.New(),
                DefinitionId.Create("workflow.handler"),
                SemanticVersion.Parse("1.0.0"),
                "code",
                CancellationToken.None
            )

        let codeFailure =
            Assert.Throws<AggregateException>(fun () ->
                codeHandler.InvokeAsync(codeContext, box 4, CancellationToken.None).Result
                |> ignore)

        Assert.Equal("code failed", codeFailure.InnerException.Message)

        let aggregateHandler =
            WorkflowGraph.AggregateHandler<int, int>(fun _ _ ->
                Task.FromException<int>(InvalidOperationException("aggregate failed")))
            :> WorkflowGraph.IAggregateHandler

        let aggregateFailure =
            Assert.Throws<AggregateException>(fun () ->
                aggregateHandler.InvokeAsync([ box 1; box 2 ], CancellationToken.None).Result
                |> ignore)

        Assert.Equal("aggregate failed", aggregateFailure.InnerException.Message)

    [<Fact>]
    let ``parallel and loop builders expose aggregate and completion branches`` () =
        let branchOne =
            Workflow.define
                "workflow.parallel.one"
                "1.0.0"
                (Workflow.code "branch.one" (fun _ value -> Task.FromResult(value + 1)))

        let branchTwo =
            Workflow.define
                "workflow.parallel.two"
                "1.0.0"
                (Workflow.code "branch.two" (fun _ value -> Task.FromResult(value * 2)))

        let step =
            Workflow.``parallel`` "parallel" 2 [ branchOne; branchTwo ] (fun values -> Task.FromResult(List.sum values))

        let aggregateNode =
            step.Fragment.Nodes
            |> List.find (fun node ->
                match node.Kind with
                | WorkflowGraph.ParallelAggregate _ -> true
                | _ -> false)

        match aggregateNode.Kind with
        | WorkflowGraph.ParallelAggregate(_, _, _, aggregate) ->
            let completed =
                aggregate.InvokeAsync([ box 2; box 6 ], CancellationToken.None).Result :?> int

            Assert.Equal(8, completed)
        | _ -> failwith "Expected a parallel aggregate node."

        let completeNode =
            step.Fragment.Nodes
            |> List.find (fun node -> StringComparer.Ordinal.Equals(node.Id, "parallel.complete"))

        match completeNode.Kind with
        | WorkflowGraph.Code handler ->
            let context =
                WorkflowContext(
                    RunId.New(),
                    DefinitionId.Create("workflow.parallel"),
                    SemanticVersion.Parse("1.0.0"),
                    completeNode.Id,
                    CancellationToken.None
                )

            let successValue =
                handler
                    .InvokeAsync(
                        context,
                        box ({ IsComplete = true; Value = 8 }: WorkflowGraph.ParallelAggregateDispatch<int>),
                        CancellationToken.None
                    )
                    .Result
                :?> int

            Assert.Equal(8, successValue)

            let incomplete =
                Assert.Throws<InvalidOperationException>(fun () ->
                    handler
                        .InvokeAsync(
                            context,
                            box ({ IsComplete = false; Value = 8 }: WorkflowGraph.ParallelAggregateDispatch<int>),
                            CancellationToken.None
                        )
                        .Result
                    |> ignore)

            Assert.Equal("Parallel aggregation did not complete.", incomplete.Message)
        | _ -> failwith "Expected a completion code node."

        let loopBody =
            Workflow.define
                "workflow.loop.body"
                "1.0.0"
                (Workflow.code "body" (fun _ value -> Task.FromResult(value + 1)))

        let loopStep = Workflow.loop "loop" 3 (fun value -> value < 5) loopBody

        Assert.Contains(loopStep.Fragment.Nodes, fun node -> StringComparer.Ordinal.Equals(node.Id, "loop.continue"))
        Assert.Contains(loopStep.Fragment.Nodes, fun node -> StringComparer.Ordinal.Equals(node.Id, "loop.exit"))

        let nullBranches =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.``parallel`` "parallel" 2 Unchecked.defaultof<WorkflowDefinition<int, int> list> (fun _ ->
                    Task.FromResult 0)
                |> ignore)

        Assert.Equal("branches", nullBranches.ParamName)

        let nullAggregate =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.parallelWithCancellation
                    "parallel"
                    2
                    [ branchOne ]
                    Unchecked.defaultof<int list -> CancellationToken -> Task<int>>
                |> ignore)

        Assert.Equal("aggregate", nullAggregate.ParamName)

        let nullPredicate =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.loop "loop" 2 Unchecked.defaultof<int -> bool> loopBody |> ignore)

        Assert.Equal("whileTrue", nullPredicate.ParamName)

        let nullBody =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.loop "loop" 2 (fun (_: int) -> true) Unchecked.defaultof<WorkflowDefinition<int, int>>
                |> ignore)

        Assert.Equal("body", nullBody.ParamName)

    [<Fact>]
    let ``workflow validation reports missing entry terminals and malformed graph structure`` () =
        let branchNode: WorkflowGraph.Node =
            { Id = "branch"
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind = WorkflowGraph.ParallelBranchAdapter(-1) }

        let collectorNode: WorkflowGraph.Node =
            { Id = "collector"
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind = WorkflowGraph.ParallelCollector("parallel", -1, 0) }

        let selectorNode: WorkflowGraph.Node =
            { Id = "selector"
              InputType = typeof<int>
              OutputType = typeof<WorkflowGraph.BranchSelection<int>>
              Kind =
                WorkflowGraph.ChoiceSelector(
                    WorkflowGraph.SelectorHandler<int>(fun _ -> "") :> WorkflowGraph.ISelectorHandler,
                    [ ""; "" ],
                    false
                ) }

        let definition =
            WorkflowDefinition<int, string>(
                DefinitionId.Create("workflow.invalid"),
                SemanticVersion.Parse("1.0.0"),
                [ branchNode; collectorNode; selectorNode ],
                [ { SourceId = "missing-source"
                    TargetId = "selector" }
                  { SourceId = "selector"
                    TargetId = "missing-target" } ],
                "missing-entry",
                []
            )

        let issues = Workflow.validate definition
        let codes = issues |> Seq.map _.Code |> Set.ofSeq

        Assert.Contains("missing-terminal", codes)
        Assert.Contains("dangling-source", codes)
        Assert.Contains("dangling-target", codes)
        Assert.Contains("missing-entry", codes)
        Assert.Contains("unreachable", codes)
        Assert.Contains("branch-key", codes)
        Assert.Contains("duplicate-branch-key", codes)
        Assert.Contains("parallel-branch-index", codes)
        Assert.Contains("empty-parallel", codes)

    [<Fact>]
    let ``workflow node kind names and choose builders cover every case adapter`` () =
        let branch =
            Workflow.define
                "workflow.branch"
                "1.0.0"
                (Workflow.code "branch.step" (fun _ value -> Task.FromResult(value + 1)))

        let defaultBranch =
            Workflow.define
                "workflow.default"
                "1.0.0"
                (Workflow.code "default.step" (fun _ value -> Task.FromResult(value + 10)))

        let chooseStep =
            Workflow.chooseCases
                "choose"
                (fun (value: int) -> if value > 0 then "known" else null)
                [ "known", branch; null, branch ]
                (Some defaultBranch)

        let nodeKinds =
            chooseStep.Fragment.Nodes
            |> List.map _.Kind
            |> List.map WorkflowGraph.nodeKindName
            |> Set.ofList

        Assert.Contains("choice-selector", nodeKinds)
        Assert.Contains("choice-case", nodeKinds)
        Assert.Contains("choice-default", nodeKinds)
        Assert.Contains("code", nodeKinds)

        let thenDefinition =
            Workflow.define
                "workflow.then"
                "1.0.0"
                (Workflow.code "step.one" (fun _ value -> Task.FromResult(value + 1)))

        let appended =
            Workflow.thenStep (Workflow.code "step.two" (fun _ value -> Task.FromResult(value * 2))) thenDefinition

        Assert.Equal(2, appended.Nodes.Length)

        let blankVersion =
            Assert.Throws<ArgumentException>(fun () ->
                Workflow.define "workflow.then" " " (Workflow.code "step" (fun _ value -> Task.FromResult value))
                |> ignore)

        Assert.Equal("version", blankVersion.ParamName)

        let nullStep =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.thenStep Unchecked.defaultof<WorkflowStep<int, int>> thenDefinition
                |> ignore)

        Assert.Equal("step", nullStep.ParamName)

        let nullDefinition =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.thenStep
                    (Workflow.code "step.two" (fun _ value -> Task.FromResult value))
                    Unchecked.defaultof<WorkflowDefinition<int, int>>
                |> ignore)

        Assert.Equal("definition", nullDefinition.ParamName)

        let nullFirst =
            Assert.Throws<ArgumentNullException>(fun () ->
                Workflow.define "workflow.then" "1.0.0" Unchecked.defaultof<WorkflowStep<int, int>>
                |> ignore)

        Assert.Equal("first", nullFirst.ParamName)

    [<Fact>]
    let ``workflow checkpoints deserialize a fresh envelope and clone its payload`` () =
        let createdAt = DateTimeOffset.UtcNow

        let serialized =
            use payloadDocument = JsonDocument.Parse("{\"approved\":true}")

            WorkflowCheckpoint<bool>(
                DefinitionId.Create("workflow.checkpoint"),
                SemanticVersion.Parse("1.0.0"),
                "fingerprint-1",
                "session-1",
                "checkpoint-1",
                createdAt,
                payloadDocument.RootElement
            )
                .Serialize()

        let deserialized =
            use freshDocument = JsonDocument.Parse(serialized.GetRawText())
            WorkflowCheckpoint<bool>.Deserialize(freshDocument.RootElement)

        Assert.Equal(DefinitionId.Create("workflow.checkpoint"), deserialized.DefinitionId)
        Assert.Equal(SemanticVersion.Parse("1.0.0"), deserialized.DefinitionVersion)
        Assert.Equal(createdAt, deserialized.CreatedAt)
        Assert.True(deserialized.Serialize().GetProperty("payload").GetProperty("approved").GetBoolean())

        let offsetEnvelope = JsonNode.Parse(serialized.GetRawText()) :?> JsonObject
        offsetEnvelope["createdAt"] <- JsonValue.Create("2026-07-12T12:00:00-07:00")
        use offsetDocument = JsonDocument.Parse(offsetEnvelope.ToJsonString())

        let offsetCheckpoint =
            WorkflowCheckpoint<bool>.Deserialize(offsetDocument.RootElement)

        Assert.Equal(TimeSpan.Zero, offsetCheckpoint.CreatedAt.Offset)
        Assert.Equal(DateTimeOffset(2026, 7, 12, 19, 0, 0, TimeSpan.Zero), offsetCheckpoint.CreatedAt)

    [<Fact>]
    let ``workflow checkpoint deserialization normalizes disposed and excessive-depth state`` () =
        let validEnvelope =
            """{"formatVersion":1,"definitionId":"workflow.checkpoint","definitionVersion":"1.0.0","fingerprint":"fingerprint-1","sessionId":"session-1","checkpointId":"checkpoint-1","createdAt":"2026-07-12T12:00:00Z","payload":{"approved":true}}"""

        let disposedDocument = JsonDocument.Parse(validEnvelope)
        let disposedState = disposedDocument.RootElement
        disposedDocument.Dispose()

        let disposedException =
            Assert.Throws<ArgumentException>(fun () -> WorkflowCheckpoint<bool>.Deserialize(disposedState) |> ignore)

        Assert.Equal("state", disposedException.ParamName)

        Assert.IsType<ObjectDisposedException>(disposedException.InnerException)
        |> ignore

        let nestedPayload =
            String.replicate 1100 "{\"nested\":" + "true" + String.replicate 1100 "}"

        let deepEnvelope =
            $"""{{"formatVersion":1,"definitionId":"workflow.checkpoint","definitionVersion":"1.0.0","fingerprint":"fingerprint-1","sessionId":"session-1","checkpointId":"checkpoint-1","createdAt":"2026-07-12T12:00:00Z","payload":{nestedPayload}}}"""

        let options = JsonDocumentOptions(MaxDepth = 2048)
        use deepDocument = JsonDocument.Parse(deepEnvelope, options)

        let deepException =
            Assert.Throws<ArgumentException>(fun () ->
                WorkflowCheckpoint<bool>.Deserialize(deepDocument.RootElement) |> ignore)

        Assert.Equal("state", deepException.ParamName)
        Assert.NotNull(deepException.InnerException)

    [<Fact>]
    let ``workflow checkpoint deserialization normalizes malformed envelopes`` () =
        let validEnvelope =
            """{"formatVersion":1,"definitionId":"workflow.checkpoint","definitionVersion":"1.0.0","fingerprint":"fingerprint-1","sessionId":"session-1","checkpointId":"checkpoint-1","createdAt":"2026-07-12T12:00:00Z","payload":{"approved":true}}"""

        let assertMalformed (json: string) =
            use document = JsonDocument.Parse(json)

            let ex =
                Assert.Throws<ArgumentException>(fun () ->
                    WorkflowCheckpoint<bool>.Deserialize(document.RootElement) |> ignore)

            Assert.Equal("state", ex.ParamName)

        for json in [ "null"; "[]"; "1"; "\"checkpoint\"" ] do
            assertMalformed json

        let mutable undefined = Unchecked.defaultof<JsonElement>

        let undefinedException =
            Assert.Throws<ArgumentException>(fun () -> WorkflowCheckpoint<bool>.Deserialize(undefined) |> ignore)

        Assert.Equal("state", undefinedException.ParamName)

        let propertyNames =
            [ "formatVersion"
              "definitionId"
              "definitionVersion"
              "fingerprint"
              "sessionId"
              "checkpointId"
              "createdAt"
              "payload" ]

        for propertyName in propertyNames do
            let envelope = JsonNode.Parse(validEnvelope) :?> JsonObject
            envelope.Remove(propertyName) |> ignore
            assertMalformed (envelope.ToJsonString())

        let wrongKinds =
            [ "formatVersion", JsonValue.Create("1") :> JsonNode
              "definitionId", JsonValue.Create(1) :> JsonNode
              "definitionVersion", JsonValue.Create(true) :> JsonNode
              "fingerprint", JsonValue.Create(1) :> JsonNode
              "sessionId", JsonValue.Create(1) :> JsonNode
              "checkpointId", JsonValue.Create(1) :> JsonNode
              "createdAt", JsonValue.Create(1) :> JsonNode
              "payload", JsonValue.Create("payload") :> JsonNode ]

        for propertyName, value in wrongKinds do
            let envelope = JsonNode.Parse(validEnvelope) :?> JsonObject
            envelope[propertyName] <- value
            assertMalformed (envelope.ToJsonString())

        let invalidValues =
            [ "definitionId", "INVALID"
              "definitionVersion", "1.0"
              "fingerprint", " "
              "sessionId", "\t"
              "checkpointId", ""
              "createdAt", "not-a-timestamp" ]

        for propertyName, value in invalidValues do
            let envelope = JsonNode.Parse(validEnvelope) :?> JsonObject
            envelope[propertyName] <- JsonValue.Create(value)
            assertMalformed (envelope.ToJsonString())

        let nonIntegerFormat = JsonNode.Parse(validEnvelope) :?> JsonObject
        nonIntegerFormat["formatVersion"] <- JsonValue.Create(1.5)
        assertMalformed (nonIntegerFormat.ToJsonString())

        let unsupportedEnvelope = JsonNode.Parse(validEnvelope) :?> JsonObject
        unsupportedEnvelope["formatVersion"] <- JsonValue.Create(2)
        use unsupportedDocument = JsonDocument.Parse(unsupportedEnvelope.ToJsonString())

        let unsupported =
            Assert.Throws<ArgumentOutOfRangeException>(fun () ->
                WorkflowCheckpoint<bool>.Deserialize(unsupportedDocument.RootElement) |> ignore)

        Assert.Equal("state", unsupported.ParamName)

    [<Fact>]
    let ``workflow validation reports entry and terminal shape issues across node kinds`` () =
        let codeNode: WorkflowGraph.Node =
            { Id = "entry"
              InputType = typeof<int>
              OutputType = typeof<string>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, string>(fun _ value -> Task.FromResult(string value))
                    :> WorkflowGraph.ICodeHandler
                ) }

        let selectorNode: WorkflowGraph.Node =
            { Id = "selector"
              InputType = typeof<int>
              OutputType = typeof<WorkflowGraph.BranchSelection<int>>
              Kind =
                WorkflowGraph.ChoiceSelector(
                    WorkflowGraph.SelectorHandler<int>(fun _ -> "") :> WorkflowGraph.ISelectorHandler,
                    [ ""; "dup"; "dup" ],
                    true
                ) }

        let collectorNode: WorkflowGraph.Node =
            { Id = "collector"
              InputType = typeof<int>
              OutputType = typeof<WorkflowGraph.ParallelBranchResult<int>>
              Kind = WorkflowGraph.ParallelCollector("parallel", -1, 0) }

        let aggregateNode: WorkflowGraph.Node =
            { Id = "aggregate"
              InputType = typeof<WorkflowGraph.ParallelBranchResult<int>>
              OutputType = typeof<WorkflowGraph.ParallelAggregateDispatch<int>>
              Kind =
                WorkflowGraph.ParallelAggregate(
                    "parallel",
                    0,
                    0,
                    WorkflowGraph.AggregateHandler<int, int>(fun values _ -> Task.FromResult(List.sum values))
                    :> WorkflowGraph.IAggregateHandler
                ) }

        let loopNode: WorkflowGraph.Node =
            { Id = "loop"
              InputType = typeof<int>
              OutputType = typeof<WorkflowGraph.LoopDecision<int>>
              Kind =
                WorkflowGraph.LoopGuard(
                    "loop",
                    0,
                    WorkflowGraph.LoopConditionHandler<int>(fun _ -> true) :> WorkflowGraph.ILoopConditionHandler
                ) }

        let promptNode: WorkflowGraph.Node =
            { Id = "prompt"
              InputType = typeof<int>
              OutputType = typeof<ApprovalPrompt>
              Kind =
                WorkflowGraph.RequestPrompt(
                    WorkflowGraph.PromptHandler<int>(fun _ -> ApprovalPrompt.Create("Approve", "Continue"))
                    :> WorkflowGraph.IPromptHandler
                ) }

        let portNode: WorkflowGraph.Node =
            { Id = "port"
              InputType = typeof<ApprovalPrompt>
              OutputType = typeof<ApprovalResponse>
              Kind = WorkflowGraph.RequestPort }

        let definition =
            WorkflowDefinition<string, int>(
                DefinitionId.Create("workflow.invalid.shapes"),
                SemanticVersion.Parse("1.0.0"),
                [ codeNode
                  selectorNode
                  collectorNode
                  aggregateNode
                  loopNode
                  promptNode
                  portNode ],
                [ { SourceId = "entry"
                    TargetId = "port" } ],
                "entry",
                [ "entry"; "missing-terminal" ]
            )

        let codes = Workflow.validate definition |> Seq.map _.Code |> Set.ofSeq

        Assert.Contains("entry-type", codes)
        Assert.Contains("terminal-type", codes)
        Assert.Contains("missing-terminal-node", codes)
        Assert.Contains("branch-key", codes)
        Assert.Contains("duplicate-branch-key", codes)
        Assert.Contains("parallel-branch-index", codes)
        Assert.Contains("empty-parallel", codes)
        Assert.Contains("parallel-concurrency", codes)
        Assert.Contains("loop-bound", codes)

    [<Fact>]
    let ``workflow validation accepts well formed choice parallel request and loop graphs`` () =
        let branchOne =
            Workflow.define
                "workflow.valid.parallel.one"
                "1.0.0"
                (Workflow.code "branch.one" (fun _ value -> Task.FromResult(value + 1)))

        let branchTwo =
            Workflow.define
                "workflow.valid.parallel.two"
                "1.0.0"
                (Workflow.code "branch.two" (fun _ value -> Task.FromResult(value * 2)))

        let fallbackBranch =
            Workflow.define
                "workflow.valid.choose.fallback"
                "1.0.0"
                (Workflow.code "branch.fallback" (fun _ value -> Task.FromResult(value - 1)))

        let chooseDefinition =
            Workflow.define
                "workflow.valid.choose"
                "1.0.0"
                (Workflow.chooseCases
                    "choose"
                    (fun (value: int) -> if value > 0 then "positive" else "negative")
                    [ "positive", branchOne; "negative", branchTwo ]
                    (Some fallbackBranch))

        let parallelDefinition =
            Workflow.define
                "workflow.valid.parallel"
                "1.0.0"
                (Workflow.``parallel`` "parallel" 2 [ branchOne; branchTwo ] (fun values ->
                    Task.FromResult(List.sum values)))

        let requestDefinition =
            Workflow.define
                "workflow.valid.request"
                "1.0.0"
                (Workflow.request "approve" (fun value -> ApprovalPrompt.Create($"Approve {value}", "Continue")))

        let loopBody =
            Workflow.define
                "workflow.valid.loop.body"
                "1.0.0"
                (Workflow.code "body" (fun _ value -> Task.FromResult(value + 1)))

        let loopDefinition =
            Workflow.define
                "workflow.valid.loop"
                "1.0.0"
                (Workflow.loop "loop" 3 (fun (value: int) -> value < 5) loopBody)

        Assert.Empty(Workflow.validate chooseDefinition)
        Assert.Empty(Workflow.validate parallelDefinition)
        Assert.Empty(Workflow.validate requestDefinition)
        Assert.Empty(Workflow.validate loopDefinition)

    [<Fact>]
    let ``workflow validation handles a 10k step chain without recursion blowups or pathological append costs`` () =
        let stepCount = 10000

        let seed =
            Workflow.define
                "workflow.large"
                "1.0.0"
                (Workflow.code "step.0" (fun _ value -> Task.FromResult(value + 1)))

        let stopwatch = Stopwatch.StartNew()

        let definition =
            [ 1 .. stepCount - 1 ]
            |> List.fold
                (fun current index ->
                    current
                    |> Workflow.thenStep (Workflow.code $"step.{index}" (fun _ value -> Task.FromResult(value + 1))))
                seed

        let issues = Workflow.validate definition
        stopwatch.Stop()

        Assert.Empty issues
        Assert.Equal(stepCount, definition.Nodes.Length)

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10.0),
            $"Building and validating {stepCount} steps took {stopwatch.Elapsed}."
        )

    [<Fact>]
    let ``workflow graph reports names for every node kind`` () =
        let agent =
            AgentDefinition.Create(
                "agent.test",
                "1.0.0",
                "Agent",
                "Do the thing.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<int, int>
                .Create(
                    "signature.test",
                    "1.0.0",
                    "Signature",
                    "Return an integer.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let names =
            [ WorkflowGraph.Code(
                  WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult value)
                  :> WorkflowGraph.ICodeHandler
              )
              WorkflowGraph.Agent(
                  agent,
                  WorkflowGraph.ErasedSignature<int, int>(signature) :> WorkflowGraph.IErasedSignature
              )
              WorkflowGraph.ChoiceSelector(
                  WorkflowGraph.SelectorHandler<int>(fun _ -> "branch") :> WorkflowGraph.ISelectorHandler,
                  [ "branch" ],
                  false
              )
              WorkflowGraph.ChoiceCaseAdapter("branch")
              WorkflowGraph.ChoiceDefaultAdapter
              WorkflowGraph.ParallelFanOut("parallel", 1, 1)
              WorkflowGraph.ParallelBranchAdapter(0)
              WorkflowGraph.ParallelCollector("parallel", 0, 1)
              WorkflowGraph.ParallelAggregate(
                  "parallel",
                  1,
                  1,
                  WorkflowGraph.AggregateHandler<int, int>(fun values _ -> Task.FromResult(List.sum values))
                  :> WorkflowGraph.IAggregateHandler
              )
              WorkflowGraph.RequestPrompt(
                  WorkflowGraph.PromptHandler<int>(fun _ -> ApprovalPrompt.Create("Approve", "Continue"))
                  :> WorkflowGraph.IPromptHandler
              )
              WorkflowGraph.RequestPort
              WorkflowGraph.LoopGuard(
                  "loop",
                  1,
                  WorkflowGraph.LoopConditionHandler<int>(fun _ -> true) :> WorkflowGraph.ILoopConditionHandler
              )
              WorkflowGraph.LoopContinueAdapter
              WorkflowGraph.LoopExit ]
            |> List.map WorkflowGraph.nodeKindName
            |> Set.ofList

        for expected in
            [ "code"
              "agent"
              "choice-selector"
              "choice-case"
              "choice-default"
              "parallel-fanout"
              "parallel-branch"
              "parallel-collector"
              "parallel-aggregate"
              "request"
              "request-port"
              "loop-guard"
              "loop-continue"
              "loop-exit" ] do
            Assert.Contains(expected, names)

    [<Fact>]
    let ``workflow run start and resume forward to the runtime`` () =
        let definition = createDefinition ()
        let runId = RunId.New()

        let result =
            RunResult(
                runId,
                CircuitResult<int>.Success(9),
                RunUsage(1, 2),
                ValueNone,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            )

        let startedRun =
            WorkflowRun<int>(
                runId,
                (EmptyAsyncEnumerable<RunEvent<int>>() :> IAsyncEnumerable<RunEvent<int>>),
                (fun (_response, _ct) -> ValueTask()),
                (fun _ct ->
                    let payload = JsonDocument.Parse("{\"approved\":true}").RootElement.Clone()

                    ValueTask<WorkflowCheckpoint<int>>(
                        WorkflowCheckpoint<int>(
                            definition.Id,
                            definition.Version,
                            definition.Fingerprint,
                            "session-1",
                            "checkpoint-1",
                            DateTimeOffset.UtcNow,
                            payload
                        )
                    )),
                (fun () -> ValueTask())
            )

        let mutable runCalled = false
        let mutable startCalled = false
        let mutable resumeCalled = false

        let runtime =
            DelegateWorkflowRuntime(
                (fun boxedDefinition boxedInput options ct ->
                    runCalled <- true
                    Assert.Equal(box definition, boxedDefinition)
                    Assert.Equal(box 8, boxedInput)
                    Assert.Equal(ValueNone, options.SessionId)
                    Assert.False(ct.IsCancellationRequested)
                    box result),
                (fun boxedDefinition boxedInput options ct ->
                    startCalled <- true
                    Assert.Equal(box definition, boxedDefinition)
                    Assert.Equal(box 8, boxedInput)
                    Assert.Equal(ValueNone, options.SessionId)
                    Assert.False(ct.IsCancellationRequested)
                    box startedRun),
                (fun boxedDefinition boxedCheckpoint ct ->
                    resumeCalled <- true
                    Assert.Equal(box definition, boxedDefinition)
                    Assert.False(ct.IsCancellationRequested)
                    Assert.IsType<WorkflowCheckpoint<int>>(boxedCheckpoint) |> ignore
                    box startedRun)
            )
            :> IWorkflowRuntime

        let runResult =
            Workflow.run runtime definition 8 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let started =
            Workflow.start runtime definition 8 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let resumed =
            use payloadDocument = JsonDocument.Parse("{\"approved\":true}")

            let checkpoint =
                WorkflowCheckpoint<int>(
                    definition.Id,
                    definition.Version,
                    definition.Fingerprint,
                    "session-1",
                    "checkpoint-1",
                    DateTimeOffset.UtcNow,
                    payloadDocument.RootElement.Clone()
                )

            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        Assert.True(runCalled)
        Assert.True(startCalled)
        Assert.True(resumeCalled)
        Assert.Equal(9, runResult.Result.Value)
        Assert.Equal(runId, started.RunId)
        Assert.Equal(runId, resumed.RunId)
