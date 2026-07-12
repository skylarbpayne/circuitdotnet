namespace Circuit.MicrosoftAgentFramework.Tests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Circuit.MicrosoftAgentFramework.MafWorkflows
open Microsoft.Extensions.AI
open Xunit

type private WorkflowArrayAsyncEnumerable<'T>(items: 'T[]) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
            let mutable index = -1
            let mutable current = Unchecked.defaultof<'T>

            { new IAsyncEnumerator<'T> with
                member _.Current = current

                member _.MoveNextAsync() =
                    if cancellationToken.IsCancellationRequested then
                        ValueTask<bool>(Task.FromCanceled<bool>(cancellationToken))
                    else
                        index <- index + 1

                        if index < items.Length then
                            current <- items[index]
                            ValueTask<bool>(true)
                        else
                            ValueTask<bool>(false)

                member _.DisposeAsync() = ValueTask() }

type private WorkflowFakeChatClient() =
    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(_messages, _options, _cancellationToken) =
            raise (InvalidOperationException("Workflow tests should not invoke the chat client."))

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            WorkflowArrayAsyncEnumerable(Array.empty) :> IAsyncEnumerable<_>

        member _.GetService(_serviceType, _serviceKey) = null

type private WorkflowRecordingObserver() =
    let events = ResizeArray<RunEventEnvelope>()

    member _.Events = events

    member _.Observations =
        events
        |> Seq.filter (fun event ->
            event.Kind = AgentRunEventKind.RunCompleted
            || event.Kind = AgentRunEventKind.RunFailed)
        |> Seq.toArray

    interface Circuit.IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            events.Add event
            ValueTask()

module private WorkflowHelpers =
    let createRuntime () =
        MafRuntime(new WorkflowFakeChatClient() :> IChatClient, MafRuntimeOptions())

    let asWorkflowRuntime (runtime: MafRuntime) = runtime :> IWorkflowRuntime

    let assertEnvelopeMetadataIsSafe (metadata: IReadOnlyDictionary<string, string>) =
        for value in metadata.Values do
            if not (isNull value) then
                Assert.DoesNotContain("workflow-startup-secret", value)
                Assert.DoesNotContain("workflow-checkpoint-secret", value)
                Assert.DoesNotContain("/tmp/", value)
                Assert.DoesNotContain("   at ", value)

    let createExternalApprovalRequest requestId title =
        let portInfo =
            Microsoft.Agents.AI.Workflows.Checkpointing.RequestPortInfo(
                Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof<ApprovalPrompt>),
                Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof<ApprovalResponse>),
                "approval"
            )

        let prompt = ApprovalPrompt.Create(title, "Need approval")

        Microsoft.Agents.AI.Workflows.ExternalRequest(
            portInfo,
            requestId,
            Microsoft.Agents.AI.Workflows.PortableValue(prompt)
        )

    let collectEvents<'T> (run: WorkflowRun<'T>) =
        task {
            let events = ResizeArray<RunEvent<'T>>()
            let enumerator = run.Events.GetAsyncEnumerator(CancellationToken.None)

            try
                let mutable keepGoing = true

                while keepGoing do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if moved then
                        events.Add enumerator.Current
                    else
                        keepGoing <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return events |> Seq.toArray
        }

    let collectUntil<'T> (predicate: RunEvent<'T> -> bool) (run: WorkflowRun<'T>) =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            let events = ResizeArray<RunEvent<'T>>()
            let enumerator = run.Events.GetAsyncEnumerator(cts.Token)

            try
                let mutable doneReading = false

                while not doneReading do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if not moved then
                        doneReading <- true
                    else
                        let event = enumerator.Current
                        events.Add event
                        doneReading <- predicate event
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return events |> Seq.toArray
        }

    [<Literal>]
    let StartupSecretRoot = "/tmp/workflow-startup-secret/root"

    [<Literal>]
    let CheckpointSecretRoot = "/tmp/workflow-checkpoint-secret/root"

    let createSecretBearingInvalidDefinition () =
        let entryNodeId = $"{StartupSecretRoot}/entry.step"
        let missingTerminalId = $"{StartupSecretRoot}/missing.terminal"

        let entryNode: WorkflowGraph.Node =
            { Id = entryNodeId
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult(value))
                    :> WorkflowGraph.ICodeHandler
                ) }

        WorkflowDefinition<int, int>(
            DefinitionId.Create("workflow.secret-startup"),
            SemanticVersion.Parse("1.0.0"),
            [ entryNode ],
            [],
            entryNodeId,
            [ missingTerminalId ]
        )

    let createSingleLineSecretBearingInvalidDefinition () =
        let entryNodeId = "workflow.resume.secret-entry"
        let missingTerminalId = "workflow.resume.secret-terminal"

        let entryNode: WorkflowGraph.Node =
            { Id = entryNodeId
              InputType = typeof<int>
              OutputType = typeof<int>
              Kind =
                WorkflowGraph.Code(
                    WorkflowGraph.CodeHandler<int, int>(fun _ value -> Task.FromResult(value))
                    :> WorkflowGraph.ICodeHandler
                ) }

        WorkflowDefinition<int, int>(
            DefinitionId.Create("workflow.secret.resume"),
            SemanticVersion.Parse("1.0.0"),
            [ entryNode ],
            [],
            entryNodeId,
            [ missingTerminalId ]
        )

    let createSecretBearingCheckpoint<'Output> (definition: WorkflowDefinition<int, 'Output>) =
        use payloadDocument = JsonDocument.Parse("{}")

        WorkflowCheckpoint<'Output>(
            definition.Id,
            definition.Version,
            $"{CheckpointSecretRoot}/fingerprint",
            "session-secret",
            "checkpoint-secret",
            DateTimeOffset.UtcNow,
            payloadDocument.RootElement.Clone()
        )

module WorkflowRuntimeTests =
    open WorkflowHelpers

    [<Fact>]
    let ``workflow agent tool resolver failures stay generic in workflow results and observer events`` () =
        let observer = WorkflowRecordingObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]

        options.ToolResolvers <-
            [| { new IToolResolver with
                   member _.ResolveAsync(_context, _cancellationToken) =
                       raise (InvalidOperationException("resolver-secret /tmp/tool-secret-root/secret.txt")) } |]

        let runtime =
            MafRuntime(new WorkflowFakeChatClient() :> IChatClient, options)
            |> asWorkflowRuntime

        let agent =
            AgentDefinition.Create(
                "workflow.agent",
                "1.0.0",
                "Workflow Agent",
                "Resolve tools safely.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<TestInput, TestOutput>
                .Create(
                    "workflow.signature",
                    "1.0.0",
                    "Workflow signature",
                    "Return structured JSON.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition =
            Workflow.define "workflow.agent.tool-resolution" "1.0.0" (Workflow.agent "agent.step" agent signature)

        let result =
            Workflow.run
                runtime
                definition
                (TestInput(Token = "resolver-secret"))
                WorkflowRunOptions.Default
                CancellationToken.None
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, result.Result.Failure.Code)
        Assert.Equal("Tool resolution failed.", result.Result.Failure.Message)
        Assert.DoesNotContain("resolver-secret", result.Result.Failure.Message)
        Assert.DoesNotContain("/tmp/tool-secret-root", result.Result.Failure.Message)
        Assert.True(result.Result.Failure.Exception.IsSome)
        Assert.Contains("resolver-secret", result.Result.Failure.Exception.Value.Message)

        let failedEvents =
            observer.Events
            |> Seq.filter (fun event -> event.Kind = AgentRunEventKind.RunFailed)
            |> Seq.toArray

        Assert.Equal(2, failedEvents.Length)

        for failedEvent in failedEvents do
            Assert.Equal("Tool resolution failed.", failedEvent.Failure.Message)
            Assert.DoesNotContain("resolver-secret", failedEvent.Failure.Message)
            Assert.DoesNotContain("/tmp/tool-secret-root", failedEvent.Failure.Message)

        Assert.Equal(2, observer.Observations.Length)

        for observation in observer.Observations do
            Assert.Equal("Tool resolution failed.", observation.Failure.Message)

    [<Fact>]
    let ``workflow start returns a failed run instead of throwing when startup validation contains secrets`` () =
        let runtime = createRuntime () |> asWorkflowRuntime
        let definition = createSecretBearingInvalidDefinition ()

        let run =
            Workflow.start runtime definition 7 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let events = collectEvents run |> _.Result

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)

        let failure =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)).Failure.Value

        Assert.Equal(CircuitFailureCode.Workflow, failure.Code)
        Assert.Equal("The workflow failed to start.", failure.Message)
        Assert.DoesNotContain("workflow-startup-secret", failure.Message)
        Assert.DoesNotContain("/tmp/", failure.Message)
        Assert.True(failure.Exception.IsSome)

    [<Fact>]
    let ``workflow startup failures stay sanitized in run results and observer diagnostics`` () =
        let observer = WorkflowRecordingObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]

        let runtime =
            MafRuntime(new WorkflowFakeChatClient() :> IChatClient, options)
            |> asWorkflowRuntime

        let definition = createSecretBearingInvalidDefinition ()

        let result =
            Workflow.run runtime definition 9 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.Equal("The workflow failed to start.", result.Result.Failure.Message)
        Assert.DoesNotContain("workflow-startup-secret", result.Result.Failure.Message)
        Assert.DoesNotContain("/tmp/", result.Result.Failure.Message)
        Assert.True(result.Result.Failure.Exception.IsSome)

        let observation = Assert.Single(observer.Observations)
        Assert.Equal(AgentRunEventKind.RunFailed, observation.Kind)
        Assert.Equal("The workflow failed to start.", observation.Failure.Message)
        Assert.DoesNotContain("workflow-startup-secret", observation.Failure.Message)
        Assert.DoesNotContain("circuit.workflow.failure.exception", observation.DiagnosticMetadata.Keys)
        Assert.Equal(result.Result.Failure.Exception.Value.GetType().Name, observation.DiagnosticMetadata["error.type"])
        Assert.Equal("Workflow", observation.DiagnosticMetadata["circuit.failure.code"])
        Assert.Equal("Run", observation.DiagnosticMetadata["circuit.operation.kind"])
        assertEnvelopeMetadataIsSafe observation.DiagnosticMetadata
        Assert.Same(result.Result.Failure.Exception.Value, observation.Failure.Exception)

        for event in observer.Events do
            assertEnvelopeMetadataIsSafe event.DiagnosticMetadata

    [<Fact>]
    let ``workflow resume returns a checkpoint mismatch run instead of throwing when checkpoint secrets do not match``
        ()
        =
        let observer = WorkflowRecordingObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]

        let runtime =
            MafRuntime(new WorkflowFakeChatClient() :> IChatClient, options)
            |> asWorkflowRuntime

        let definition =
            Workflow.define
                "workflow.resume.secret"
                "1.0.0"
                (Workflow.code "resume.step" (fun _ value -> Task.FromResult(value + 1)))

        let checkpoint = createSecretBearingCheckpoint definition

        let run =
            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        let events = collectEvents run |> _.Result

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)

        let failure =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)).Failure.Value

        Assert.Equal(CircuitFailureCode.CheckpointMismatch, failure.Code)

        Assert.Equal(
            "The supplied workflow checkpoint does not match this workflow definition fingerprint.",
            failure.Message
        )

        Assert.DoesNotContain("workflow-checkpoint-secret", failure.Message)
        Assert.DoesNotContain("/tmp/", failure.Message)

    [<Fact>]
    let ``workflow resume returns a generic workflow failure when validation throws a single-line secret`` () =
        let observer = WorkflowRecordingObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]

        let runtime =
            MafRuntime(new WorkflowFakeChatClient() :> IChatClient, options)
            |> asWorkflowRuntime

        let definition = createSingleLineSecretBearingInvalidDefinition ()

        let checkpoint =
            use payloadDocument = JsonDocument.Parse("{}")

            WorkflowCheckpoint<int>(
                definition.Id,
                definition.Version,
                definition.Fingerprint,
                "resume-session-secret",
                "resume-checkpoint-secret",
                DateTimeOffset.UtcNow,
                payloadDocument.RootElement.Clone()
            )

        let run =
            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        let events = collectEvents run |> _.Result

        Assert.Equal<RunEventKind[]>([| RunEventKind.RunStarted; RunEventKind.RunFailed |], events |> Array.map _.Kind)

        let failure =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)).Failure.Value

        Assert.Equal(CircuitFailureCode.Workflow, failure.Code)
        Assert.Equal("The workflow failed to resume.", failure.Message)
        Assert.DoesNotContain("workflow.resume.secret", failure.Message)
        Assert.Contains("workflow.resume.secret", failure.Exception.Value.Message)
        Assert.Equal("InvalidOperationException", failure.Exception.Value.GetType().Name)

        let observation = Assert.Single(observer.Observations)
        Assert.Equal(AgentRunEventKind.RunFailed, observation.Kind)
        Assert.Equal("The workflow failed to resume.", observation.Failure.Message)
        Assert.DoesNotContain("workflow.resume.secret", observation.Failure.Message)
        Assert.DoesNotContain("circuit.workflow.failure.exception", observation.DiagnosticMetadata.Keys)
        Assert.Equal("InvalidOperationException", observation.DiagnosticMetadata["error.type"])
        Assert.Equal("Workflow", observation.DiagnosticMetadata["circuit.failure.code"])
        Assert.Equal("Run", observation.DiagnosticMetadata["circuit.operation.kind"])
        assertEnvelopeMetadataIsSafe observation.DiagnosticMetadata

        for event in observer.Events do
            assertEnvelopeMetadataIsSafe event.DiagnosticMetadata

    [<Fact>]
    let ``workflow start maps startup cancellation to a cancelled failure`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let definition =
            Workflow.define
                "workflow.cancel.prestart"
                "1.0.0"
                (Workflow.code "cancel.step" (fun _ value -> Task.FromResult(value)))

        use cts = new CancellationTokenSource()
        cts.Cancel()

        let run =
            Workflow.start runtime definition 3 WorkflowRunOptions.Default cts.Token
            |> _.Result

        let events = collectEvents run |> _.Result

        let failure =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)).Failure.Value

        Assert.Equal(CircuitFailureCode.Cancelled, failure.Code)

    [<Fact>]
    let ``sequence transforms values and emits ordered step events`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let addOne = Workflow.code "step.one" (fun _ value -> Task.FromResult(value + 1))
        let doubleIt = Workflow.code "step.two" (fun _ value -> Task.FromResult(value * 2))

        let definition =
            Workflow.define "workflow.sequence" "1.0.0" addOne |> Workflow.thenStep doubleIt

        let run =
            Workflow.start runtime definition 2 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let events = collectEvents run |> _.Result

        let kinds = events |> Array.map _.Kind
        Assert.Contains(RunEventKind.RunStarted, kinds)

        Assert.Equal<RunEventKind[]>(
            [| RunEventKind.StepStarted
               RunEventKind.StepCompleted
               RunEventKind.StepStarted
               RunEventKind.StepCompleted |],
            events
            |> Array.filter (fun event ->
                event.Kind = RunEventKind.StepStarted || event.Kind = RunEventKind.StepCompleted)
            |> Array.map _.Kind
        )

        let operationIds =
            events
            |> Array.filter (fun event ->
                event.Kind = RunEventKind.StepStarted || event.Kind = RunEventKind.StepCompleted)
            |> Array.choose (fun event -> event.OperationId |> ValueOption.toOption)

        Assert.Equal<string[]>([| "step.one"; "step.one"; "step.two"; "step.two" |], operationIds)

        let completed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted))

        Assert.Equal(6, completed.Value.Value)

    [<Fact>]
    let ``streaming emits contiguous sequences and exactly one terminal event`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let addOne =
            Workflow.code "step.seq.one" (fun _ value -> Task.FromResult(value + 1))

        let doubleIt =
            Workflow.code "step.seq.two" (fun _ value -> Task.FromResult(value * 2))

        let definition =
            Workflow.define "workflow.sequence.stable" "1.0.0" addOne
            |> Workflow.thenStep doubleIt

        let run =
            Workflow.start runtime definition 2 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let events = collectEvents run |> _.Result

        Assert.Equal<int64[]>(events |> Array.mapi (fun index _ -> int64 index), events |> Array.map _.Sequence)

        Assert.Single(
            events
            |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
        )
        |> ignore

    [<Fact>]
    let ``streaming emits a single cancelled terminal event when the run is cancelled`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let started =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use cts = new CancellationTokenSource()

        let waitingStep =
            Workflow.code "step.cancel.stream" (fun context value ->
                task {
                    started.TrySetResult() |> ignore
                    do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                    return value
                })

        let definition = Workflow.define "workflow.cancel.stream" "1.0.0" waitingStep

        let run =
            Workflow.start runtime definition 2 WorkflowRunOptions.Default cts.Token
            |> _.Result

        let eventsTask = collectEvents run

        try
            started.Task.WaitAsync(TimeSpan.FromMilliseconds 1000.0).GetAwaiter().GetResult()
            |> ignore
        with :? TimeoutException ->
            Assert.True(false, "Expected the workflow step to start before cancellation.")

        cts.Cancel()

        let events = eventsTask.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        let failed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed))

        Assert.Equal(CircuitFailureCode.Cancelled, failed.Failure.Value.Code)

        Assert.Single(
            events
            |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
        )
        |> ignore

    [<Fact>]
    let ``run async drains cancelled streaming workflow to a cancelled result`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let started =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        use cts = new CancellationTokenSource()

        let waitingStep =
            Workflow.code "step.cancel.run" (fun context value ->
                task {
                    started.TrySetResult() |> ignore
                    do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                    return value
                })

        let definition = Workflow.define "workflow.cancel.run" "1.0.0" waitingStep
        let runTask = Workflow.run runtime definition 2 WorkflowRunOptions.Default cts.Token

        try
            started.Task.WaitAsync(TimeSpan.FromMilliseconds 1000.0).GetAwaiter().GetResult()
            |> ignore
        with :? TimeoutException ->
            Assert.True(false, "Expected the workflow step to start before cancellation.")

        cts.Cancel()

        let result = runTask.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Cancelled, result.Result.Failure.Code)

    [<Fact>]
    let ``branch executes matching path and default`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let low =
            Workflow.define
                "workflow.branch.low"
                "1.0.0"
                (Workflow.code "low" (fun _ value -> Task.FromResult($"low:{value}")))

        let high =
            Workflow.define
                "workflow.branch.high"
                "1.0.0"
                (Workflow.code "high" (fun _ value -> Task.FromResult($"high:{value}")))

        let chooser =
            Workflow.choose
                "branch.select"
                (fun value -> if value > 10 then "high" else "low")
                (Map.ofList [ "low", low; "high", high ])
                None

        let definition = Workflow.define "workflow.branch" "1.0.0" chooser

        let lowResult =
            Workflow.run runtime definition 4 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let highResult =
            Workflow.run runtime definition 20 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.Equal("low:4", lowResult.Result.Value)
        Assert.Equal("high:20", highResult.Result.Value)

        let defaultChooser =
            Workflow.choose "branch.default" (fun (_: int) -> "missing") (Map.ofList [ "low", low ]) (Some high)

        let defaultDefinition =
            Workflow.define "workflow.branch.default" "1.0.0" defaultChooser

        let defaultResult =
            Workflow.run runtime defaultDefinition 99 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.Equal("high:99", defaultResult.Result.Value)

    [<Fact>]
    let ``sequence emits intermediate output for non terminal final-contract steps`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let first =
            Workflow.code "step.intermediate" (fun _ value -> Task.FromResult(value + 1))

        let second = Workflow.code "step.final" (fun _ value -> Task.FromResult(value * 2))

        let definition =
            Workflow.define "workflow.intermediate" "1.0.0" first
            |> Workflow.thenStep second

        let run =
            Workflow.start runtime definition 2 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let events = collectEvents run |> _.Result

        let intermediate =
            Assert.Single(
                events
                |> Array.filter (fun event -> event.Kind = RunEventKind.IntermediateOutput)
            )

        Assert.Equal("step.intermediate", intermediate.OperationId.Value)
        Assert.Equal(3, intermediate.Value.Value)

        let completed =
            Assert.Single(events |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted))

        Assert.Equal(6, completed.Value.Value)

    [<Fact>]
    let ``parallel preserves declared result order on success`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let branchOne =
            Workflow.define
                "parallel.success.one"
                "1.0.0"
                (Workflow.code "branch.success.one" (fun _ value ->
                    task {
                        do! Task.Delay 40
                        return value + 1
                    }))

        let branchTwo =
            Workflow.define
                "parallel.success.two"
                "1.0.0"
                (Workflow.code "branch.success.two" (fun _ value ->
                    task {
                        do! Task.Delay 5
                        return value + 2
                    }))

        let parallelStep =
            Workflow.``parallel`` "parallel.success" 2 [ branchOne; branchTwo ] (fun values -> Task.FromResult values)

        let definition = Workflow.define "workflow.parallel.success" "1.0.0" parallelStep

        let result =
            Workflow.run runtime definition 5 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.True(result.Result.IsSuccess)
        Assert.Equal<int list>([ 6; 7 ], result.Result.Value)

    [<Fact>]
    let ``parallel overlaps preserves order and cancels siblings on failure`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let started =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let siblingCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable entered = 0

        let branchOne =
            Workflow.define
                "parallel.branch.one"
                "1.0.0"
                (Workflow.code "branch.one" (fun context value ->
                    task {
                        if Interlocked.Increment(&entered) = 2 then
                            started.TrySetResult() |> ignore

                        try
                            do! release.Task.WaitAsync(context.CancellationToken)
                            return value + 1
                        with :? OperationCanceledException ->
                            siblingCancelled.TrySetResult() |> ignore
                            return raise (OperationCanceledException(context.CancellationToken))
                    }))

        let branchTwo =
            Workflow.define
                "parallel.branch.two"
                "1.0.0"
                (Workflow.code "branch.two" (fun _ (_: int) ->
                    task {
                        if Interlocked.Increment(&entered) = 2 then
                            started.TrySetResult() |> ignore

                        do! Task.Delay(20)
                        return raise (InvalidOperationException("boom"))
                    }))

        let parallelStep =
            Workflow.``parallel`` "parallel.main" 2 [ branchOne; branchTwo ] (fun values -> Task.FromResult values)

        let definition = Workflow.define "workflow.parallel" "1.0.0" parallelStep

        let runTask =
            Workflow.run runtime definition 5 WorkflowRunOptions.Default CancellationToken.None

        Assert.True(started.Task.Wait(1000), "Expected both branches to start.")
        let result = runTask.Result

        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)
        Assert.True(siblingCancelled.Task.Wait(1000), "Expected the sibling branch to observe cancellation.")

    [<Fact>]
    let ``parallel enforces maxConcurrency two across five branches`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let firstTwoStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let thirdStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let startedOrder = ResizeArray<int>()

        let releaseSignals =
            [| for _ in 0..4 -> TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) |]

        let orderGate = obj ()
        let mutable currentConcurrency = 0
        let mutable maxObservedConcurrency = 0

        let recordEnter branchIndex =
            let now = Interlocked.Increment(&currentConcurrency)

            lock orderGate (fun () ->
                startedOrder.Add branchIndex
                maxObservedConcurrency <- max maxObservedConcurrency now

                if startedOrder.Count = 2 then
                    firstTwoStarted.TrySetResult() |> ignore
                elif startedOrder.Count = 3 then
                    thirdStarted.TrySetResult() |> ignore)

        let recordExit () =
            Interlocked.Decrement(&currentConcurrency) |> ignore

        let branches =
            [ for branchIndex in 0..4 ->
                  Workflow.define
                      $"workflow.parallel.bound.two.branch.{branchIndex}"
                      "1.0.0"
                      (Workflow.code $"workflow.parallel.bound.two.branch.{branchIndex}.step" (fun context _ ->
                          task {
                              recordEnter branchIndex

                              try
                                  do! releaseSignals[branchIndex].Task.WaitAsync(context.CancellationToken)
                                  return branchIndex
                              finally
                                  recordExit ()
                          })) ]

        let parallelStep =
            Workflow.``parallel`` "workflow.parallel.bound.two" 2 branches (fun values -> Task.FromResult values)

        let definition =
            Workflow.define "workflow.parallel.bound.two.root" "1.0.0" parallelStep

        let runTask =
            Workflow.run runtime definition 0 WorkflowRunOptions.Default CancellationToken.None

        let runCompletion = runTask.ContinueWith(fun (_: Task<_>) -> ())

        let firstSignal =
            Task
                .WhenAny(firstTwoStarted.Task, runCompletion)
                .WaitAsync(TimeSpan.FromSeconds 5.0)
                .GetAwaiter()
                .GetResult()

        if obj.ReferenceEquals(firstSignal, runCompletion) then
            Assert.True(false, $"Run completed before two branches started: {runTask.Exception}")

        Assert.Equal(2, maxObservedConcurrency)
        Task.Delay(150).Wait()
        Assert.False(thirdStarted.Task.IsCompleted, "Expected the third branch to remain queued at maxConcurrency 2.")

        let releasedBranches =
            lock orderGate (fun () -> [| startedOrder[0]; startedOrder[1] |])

        releaseSignals[releasedBranches[0]].TrySetResult() |> ignore
        Task.Delay(150).Wait()

        Assert.False(
            thirdStarted.Task.IsCompleted,
            "Expected the next wave to remain queued until the first wave completes."
        )

        releaseSignals[releasedBranches[1]].TrySetResult() |> ignore

        thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        Assert.Equal(2, maxObservedConcurrency)

        for signal in releaseSignals do
            signal.TrySetResult() |> ignore

        let result = runTask.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        Assert.True(result.Result.IsSuccess)
        Assert.Equal<int list>([ 0; 1; 2; 3; 4 ], result.Result.Value)
        Assert.Equal(2, maxObservedConcurrency)

    [<Fact>]
    let ``parallel maxConcurrency one serializes branch starts`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let firstStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let secondStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let thirdStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let startedOrder = ResizeArray<int>()

        let releaseSignals =
            [| for _ in 0..2 -> TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) |]

        let orderGate = obj ()
        let mutable currentConcurrency = 0
        let mutable maxObservedConcurrency = 0

        let recordEnter branchIndex =
            let now = Interlocked.Increment(&currentConcurrency)

            lock orderGate (fun () ->
                startedOrder.Add branchIndex
                maxObservedConcurrency <- max maxObservedConcurrency now

                match startedOrder.Count with
                | 1 -> firstStarted.TrySetResult() |> ignore
                | 2 -> secondStarted.TrySetResult() |> ignore
                | 3 -> thirdStarted.TrySetResult() |> ignore
                | _ -> ())

        let recordExit () =
            Interlocked.Decrement(&currentConcurrency) |> ignore

        let branches =
            [ for branchIndex in 0..2 ->
                  Workflow.define
                      $"workflow.parallel.bound.one.branch.{branchIndex}"
                      "1.0.0"
                      (Workflow.code $"workflow.parallel.bound.one.branch.{branchIndex}.step" (fun context _ ->
                          task {
                              recordEnter branchIndex

                              try
                                  do! releaseSignals[branchIndex].Task.WaitAsync(context.CancellationToken)
                                  return branchIndex
                              finally
                                  recordExit ()
                          })) ]

        let parallelStep =
            Workflow.``parallel`` "workflow.parallel.bound.one" 1 branches (fun values -> Task.FromResult values)

        let definition =
            Workflow.define "workflow.parallel.bound.one.root" "1.0.0" parallelStep

        let runTask =
            Workflow.run runtime definition 0 WorkflowRunOptions.Default CancellationToken.None

        firstStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        Task.Delay(150).Wait()
        Assert.False(secondStarted.Task.IsCompleted, "Expected only one branch to start while maxConcurrency is 1.")
        Assert.Equal(1, maxObservedConcurrency)

        let firstBranch = lock orderGate (fun () -> startedOrder[0])
        releaseSignals[firstBranch].TrySetResult() |> ignore

        secondStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        Task.Delay(150).Wait()

        Assert.False(
            thirdStarted.Task.IsCompleted,
            "Expected the third branch to remain queued until the second finishes."
        )

        Assert.Equal(1, maxObservedConcurrency)

        let secondBranch = lock orderGate (fun () -> startedOrder[1])
        releaseSignals[secondBranch].TrySetResult() |> ignore

        thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        for signal in releaseSignals do
            signal.TrySetResult() |> ignore

        let result = runTask.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        Assert.True(result.Result.IsSuccess)
        Assert.Equal<int list>([ 0; 1; 2 ], result.Result.Value)
        Assert.Equal(1, maxObservedConcurrency)

    [<Fact>]
    let ``parallel failure cancels queued siblings without hanging`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let firstTwoStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let holderCancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let unexpectedQueuedBranchStart =
            TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable startedCount = 0

        let markStarted () =
            if Interlocked.Increment(&startedCount) = 2 then
                firstTwoStarted.TrySetResult() |> ignore

        let longRunningBranch branchIndex =
            Workflow.define
                $"workflow.parallel.failure.branch.{branchIndex}"
                "1.0.0"
                (Workflow.code $"workflow.parallel.failure.branch.{branchIndex}.step" (fun context _ ->
                    task {
                        markStarted ()

                        try
                            do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                            return branchIndex
                        with :? OperationCanceledException ->
                            holderCancelled.TrySetResult() |> ignore
                            return raise (OperationCanceledException(context.CancellationToken))
                    }))

        let failingBranch =
            Workflow.define
                "workflow.parallel.failure.branch.fail"
                "1.0.0"
                (Workflow.code "workflow.parallel.failure.branch.fail.step" (fun _ (_: int) ->
                    task {
                        markStarted ()
                        do! Task.Delay 50
                        return raise (InvalidOperationException("boom"))
                    }))

        let queuedBranch branchIndex =
            Workflow.define
                $"workflow.parallel.failure.branch.queued.{branchIndex}"
                "1.0.0"
                (Workflow.code $"workflow.parallel.failure.branch.queued.{branchIndex}.step" (fun context _ ->
                    task {
                        unexpectedQueuedBranchStart.TrySetResult(branchIndex) |> ignore
                        do! Task.Delay(Timeout.Infinite, context.CancellationToken)
                        return branchIndex
                    }))

        let branches =
            [ longRunningBranch 0
              failingBranch
              queuedBranch 2
              queuedBranch 3
              queuedBranch 4 ]

        let parallelStep =
            Workflow.``parallel`` "workflow.parallel.failure.bound" 2 branches (fun values -> Task.FromResult values)

        let definition =
            Workflow.define "workflow.parallel.failure.bound.root" "1.0.0" parallelStep

        let runTask =
            Workflow.run runtime definition 0 WorkflowRunOptions.Default CancellationToken.None

        firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        let result = runTask.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        Assert.False(result.Result.IsSuccess)
        Assert.Equal(CircuitFailureCode.Workflow, result.Result.Failure.Code)

        holderCancelled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        Assert.False(
            unexpectedQueuedBranchStart.Task.IsCompleted,
            "Expected queued branches to stay blocked and then cancel."
        )

        Assert.Equal(2, startedCount)

    [<Fact>]
    let ``parallel waves are isolated across simultaneous runs`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let runOneStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let runTwoStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let bothRunsStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let releaseByRun = Dictionary<int, TaskCompletionSource>()
        releaseByRun[1] <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        releaseByRun[2] <- TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable startedRuns = 0

        let firstBranch =
            Workflow.define
                "workflow.parallel.shared-gate.first"
                "1.0.0"
                (Workflow.code "workflow.parallel.shared-gate.first.step" (fun context runId ->
                    task {
                        match runId with
                        | 1 -> runOneStarted.TrySetResult() |> ignore
                        | 2 -> runTwoStarted.TrySetResult() |> ignore
                        | _ -> ()

                        if Interlocked.Increment(&startedRuns) = 2 then
                            bothRunsStarted.TrySetResult() |> ignore

                        do! releaseByRun[runId].Task.WaitAsync(context.CancellationToken)
                        return runId * 10
                    }))

        let secondBranch =
            Workflow.define
                "workflow.parallel.shared-gate.second"
                "1.0.0"
                (Workflow.code "workflow.parallel.shared-gate.second.step" (fun _ runId ->
                    Task.FromResult(runId * 10 + 1)))

        let parallelStep =
            Workflow.``parallel`` "workflow.parallel.shared-gate" 1 [ firstBranch; secondBranch ] (fun values ->
                Task.FromResult values)

        let definition =
            Workflow.define "workflow.parallel.shared-gate.root" "1.0.0" parallelStep

        let firstRun =
            Workflow.run runtime definition 1 WorkflowRunOptions.Default CancellationToken.None

        let secondRun =
            Workflow.run runtime definition 2 WorkflowRunOptions.Default CancellationToken.None

        bothRunsStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        |> ignore

        Assert.True(runOneStarted.Task.IsCompleted)
        Assert.True(runTwoStarted.Task.IsCompleted)

        releaseByRun[1].TrySetResult() |> ignore
        releaseByRun[2].TrySetResult() |> ignore

        let firstResult =
            firstRun.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        let secondResult =
            secondRun.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        Assert.Equal<int list>([ 10; 11 ], firstResult.Result.Value)
        Assert.Equal<int list>([ 20; 21 ], secondResult.Result.Value)

    [<Fact>]
    let ``bounded loop stops at result or max`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let body =
            Workflow.define
                "loop.body"
                "1.0.0"
                (Workflow.code "loop.body.step" (fun _ value -> Task.FromResult(value + 1)))

        let loopStep = Workflow.loop "loop.main" 3 (fun value -> value < 5) body
        let definition = Workflow.define "workflow.loop" "1.0.0" loopStep

        let underLimit =
            Workflow.run runtime definition 0 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let overLimit =
            Workflow.run runtime definition 10 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        Assert.Equal(3, underLimit.Result.Value)
        Assert.Equal(10, overLimit.Result.Value)

    [<Fact>]
    let ``request pauses accepts only matching token and resumes`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let requestStep =
            Workflow.request "approval" (fun value -> ApprovalPrompt.Create($"approve:{value}", "Need approval"))

        let toStringStep =
            Workflow.code "approval.result" (fun _ (response: ApprovalResponse) -> Task.FromResult(response.Approved))

        let definition =
            Workflow.define "workflow.request" "1.0.0" requestStep
            |> Workflow.thenStep toStringStep

        let run =
            Workflow.start runtime definition 42 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) run
            |> _.Result

        let approvalEvent =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let badResponse = ApprovalResponse("wrong-token", true, null)

        Assert.ThrowsAny<Exception>(fun () ->
            run.RespondAsync(badResponse, CancellationToken.None).AsTask().GetAwaiter().GetResult())
        |> ignore

        let goodResponse =
            ApprovalResponse(approvalEvent.Approval.Value.RequestId, true, "approved")

        run.RespondAsync(goodResponse, CancellationToken.None).AsTask().GetAwaiter().GetResult()

        let secondPass =
            collectUntil
                (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
                run
            |> _.Result

        let completed =
            Assert.Single(secondPass |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted))

        Assert.True(completed.Value.Value)

    [<Fact>]
    let ``approval response dispatch releases reservations after send failure and rejects replay after success`` () =
        let pending = PendingApprovalRegistry()
        let request = createExternalApprovalRequest "approval-token" "Approve"
        let response = ApprovalResponse(request.RequestId, true, "approved")
        let mutable attempts = 0

        pending.Register request

        let send _ _ =
            match Interlocked.Increment(&attempts) with
            | 1 -> Task.FromException(InvalidOperationException("transient send failure"))
            | _ -> Task.CompletedTask

        Assert.ThrowsAny<Exception>(
            Action(fun () ->
                ApprovalResponseDispatch.sendAsync pending response CancellationToken.None send
                |> _.GetAwaiter().GetResult())
        )
        |> ignore

        Assert.Equal(1, pending.Count)

        ApprovalResponseDispatch.sendAsync pending response CancellationToken.None send
        |> _.GetAwaiter().GetResult()

        Assert.Equal(0, pending.Count)

        Assert.ThrowsAny<Exception>(
            Action(fun () ->
                ApprovalResponseDispatch.sendAsync pending response CancellationToken.None send
                |> _.GetAwaiter().GetResult())
        )
        |> ignore

    [<Fact>]
    let ``approval response dispatch rejects concurrent duplicate sends for the same token`` () =
        let pending = PendingApprovalRegistry()
        let request = createExternalApprovalRequest "approval-duplicate" "Approve"
        let response = ApprovalResponse(request.RequestId, true, "approved")

        let sendStarted =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let releaseSend =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        pending.Register request

        let send _ _ =
            sendStarted.TrySetResult() |> ignore
            releaseSend.Task

        let firstSend =
            ApprovalResponseDispatch.sendAsync pending response CancellationToken.None send

        Assert.True(sendStarted.Task.Wait(1000), "Expected the first send attempt to reserve the token.")

        let duplicateError =
            Task.Run(fun () ->
                try
                    ApprovalResponseDispatch.sendAsync pending response CancellationToken.None send
                    |> _.GetAwaiter().GetResult()

                    null
                with ex ->
                    ex)

        let duplicateException = duplicateError.Result
        Assert.NotNull(duplicateException)
        Assert.Contains("unknown or has already been used", duplicateException.Message)

        releaseSend.TrySetResult() |> ignore
        firstSend.GetAwaiter().GetResult()

        Assert.Equal(0, pending.Count)

    [<Fact>]
    let ``disposing a paused workflow clears pending approval tokens`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let requestStep =
            Workflow.request "approval.dispose" (fun value ->
                ApprovalPrompt.Create($"approve:{value}", "Need approval"))

        let finalStep =
            Workflow.code "approval.dispose.final" (fun _ (response: ApprovalResponse) ->
                Task.FromResult(response.Approved))

        let definition =
            Workflow.define "workflow.approval.dispose" "1.0.0" requestStep
            |> Workflow.thenStep finalStep

        let run =
            Workflow.start runtime definition 5 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) run
            |> _.Result

        let approvalEvent =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        (run :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

        Assert.ThrowsAny<Exception>(fun () ->
            run
                .RespondAsync(
                    ApprovalResponse(approvalEvent.Approval.Value.RequestId, true, "approved"),
                    CancellationToken.None
                )
                .AsTask()
                .GetAwaiter()
                .GetResult())
        |> ignore

    [<Fact>]
    let ``parallel aggregate capture preserves declaration order under completion races`` () =
        let state = ParallelAggregateState.create<int> 8

        let captures =
            [| for index in 0..7 ->
                   Task.Run(fun () ->
                       Task.Delay((7 - index) * 5).Wait()

                       ParallelAggregateState.capture
                           8
                           ({ BranchIndex = index; Value = index }: WorkflowGraph.ParallelBranchResult<int>)
                           state) |]
            |> Task.WhenAll
            |> fun task -> task.GetAwaiter().GetResult()

        let ordered =
            captures
            |> Array.choose (function
                | ParallelAggregateCapture.Complete values -> Some values
                | _ -> None)

        Assert.Equal<int list>([ 0; 1; 2; 3; 4; 5; 6; 7 ], Assert.Single(ordered))

    [<Fact>]
    let ``parallel aggregate capture rejects duplicate branch envelopes`` () =
        let state = ParallelAggregateState.create<int> 2

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 0; Value = 10 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.Pending -> ()
        | other -> Assert.True(false, $"Expected Pending, got {other}.")

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 0; Value = 20 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.DuplicateBranch 0 -> ()
        | other -> Assert.True(false, $"Expected DuplicateBranch 0, got {other}.")

        match
            ParallelAggregateState.capture
                2
                ({ BranchIndex = 1; Value = 30 }: WorkflowGraph.ParallelBranchResult<int>)
                state
        with
        | ParallelAggregateCapture.Complete values -> Assert.Equal<int list>([ 10; 30 ], values)
        | other -> Assert.True(false, $"Expected Complete, got {other}.")

    [<Fact>]
    let ``checkpoint rehydrates same fingerprint and rejects changed version or topology`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let requestStep =
            Workflow.request "checkpoint.approval" (fun value ->
                ApprovalPrompt.Create($"approve:{value}", "Need approval"))

        let finalStep =
            Workflow.code "checkpoint.final" (fun _ (response: ApprovalResponse) -> Task.FromResult(response.Approved))

        let definition =
            Workflow.define "workflow.checkpoint" "1.0.0" requestStep
            |> Workflow.thenStep finalStep

        let run =
            Workflow.start runtime definition 7 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) run
            |> _.Result

        let _approvalEvent =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask().Result

        let resumed =
            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        let resumedFirstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) resumed
            |> _.Result

        let resumedApproval =
            Assert.Single(
                resumedFirstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        resumed
            .RespondAsync(
                ApprovalResponse(resumedApproval.Approval.Value.RequestId, true, null),
                CancellationToken.None
            )
            .AsTask()
            .GetAwaiter()
            .GetResult()

        let resumedEvents =
            collectUntil
                (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
                resumed
            |> _.Result

        let completed =
            Assert.Single(
                resumedEvents
                |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted)
            )

        Assert.True(completed.Value.Value)

        let assertCheckpointMismatch definition expectedMessage =
            let resumedMismatch =
                Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

            let mismatchEvents = collectEvents resumedMismatch |> _.Result

            Assert.Equal<RunEventKind[]>(
                [| RunEventKind.RunStarted; RunEventKind.RunFailed |],
                mismatchEvents |> Array.map _.Kind
            )

            let mismatchFailure =
                Assert
                    .Single(
                        mismatchEvents
                        |> Array.filter (fun event -> event.Kind = RunEventKind.RunFailed)
                    )
                    .Failure.Value

            Assert.Equal(CircuitFailureCode.CheckpointMismatch, mismatchFailure.Code)
            Assert.Equal(expectedMessage, mismatchFailure.Message)


        let changedVersion =
            Workflow.define "workflow.checkpoint" "2.0.0" requestStep
            |> Workflow.thenStep finalStep

        assertCheckpointMismatch
            changedVersion
            "The supplied workflow checkpoint does not match this workflow definition version."

        let changedTopology =
            Workflow.define
                "workflow.checkpoint"
                "1.0.0"
                (Workflow.request "checkpoint.approval.changed" (fun value ->
                    ApprovalPrompt.Create($"approve:{value}", "Need approval")))
            |> Workflow.thenStep finalStep

        assertCheckpointMismatch
            changedTopology
            "The supplied workflow checkpoint does not match this workflow definition fingerprint."

    [<Fact>]
    let ``checkpoint round-trips delimiter-heavy branch keys and node ids`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let low =
            Workflow.define
                "workflow.branch.low"
                "1.0.0"
                (Workflow.request "low.approval|one" (fun value ->
                    ApprovalPrompt.Create($"low:{value}", "Need approval")))
            |> Workflow.thenStep (
                Workflow.code "low.final=one" (fun _ (response: ApprovalResponse) -> Task.FromResult(response.Approved))
            )

        let high =
            Workflow.define
                "workflow.branch.high"
                "1.0.0"
                (Workflow.request "high.approval,two" (fun value ->
                    ApprovalPrompt.Create($"high:{value}", "Need approval")))
            |> Workflow.thenStep (
                Workflow.code "high.final|two" (fun _ (response: ApprovalResponse) ->
                    Task.FromResult(response.Approved))
            )

        let definition =
            Workflow.define
                "workflow.checkpoint.root"
                "1.0.0"
                (Workflow.choose
                    "branch.select|root=one"
                    (fun value -> if value > 10 then "high=two" else "low,one")
                    (Map.ofList [ "low,one", low; "high=two", high ])
                    None)

        let run =
            Workflow.start runtime definition 7 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let firstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) run
            |> _.Result

        let _approvalEvent =
            Assert.Single(
                firstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        let checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask().Result

        let resumed =
            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        let resumedFirstPass =
            collectUntil (fun event -> event.Kind = RunEventKind.ApprovalRequested) resumed
            |> _.Result

        let resumedApproval =
            Assert.Single(
                resumedFirstPass
                |> Array.filter (fun event -> event.Kind = RunEventKind.ApprovalRequested)
            )

        resumed
            .RespondAsync(
                ApprovalResponse(resumedApproval.Approval.Value.RequestId, true, null),
                CancellationToken.None
            )
            .AsTask()
            .GetAwaiter()
            .GetResult()

        let resumedEvents =
            collectUntil
                (fun event -> event.Kind = RunEventKind.RunCompleted || event.Kind = RunEventKind.RunFailed)
                resumed
            |> _.Result

        let completed =
            Assert.Single(
                resumedEvents
                |> Array.filter (fun event -> event.Kind = RunEventKind.RunCompleted)
            )

        Assert.True(completed.Value.Value)

    [<Fact>]
    let ``parallel resume restores the active wave before queued branches`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let approvalBranch branchIndex =
            Workflow.define
                $"workflow.parallel.resume.branch.{branchIndex}"
                "1.0.0"
                (Workflow.request $"workflow.parallel.resume.branch.{branchIndex}.approval" (fun (_: int) ->
                    ApprovalPrompt.Create($"branch:{branchIndex}", "Need approval")))
            |> Workflow.thenStep (
                Workflow.code
                    $"workflow.parallel.resume.branch.{branchIndex}.final"
                    (fun _ (response: ApprovalResponse) ->
                        Task.FromResult(if response.Approved then branchIndex else -1))
            )

        let parallelStep =
            Workflow.``parallel``
                "workflow.parallel.resume"
                2
                [ approvalBranch 0; approvalBranch 1; approvalBranch 2 ]
                (fun values -> Task.FromResult values)

        let definition =
            Workflow.define "workflow.parallel.resume.root" "1.0.0" parallelStep

        let collectTwoApprovals (run: WorkflowRun<int list>) =
            let approvals = ResizeArray<ApprovalRequest>()

            collectUntil
                (fun event ->
                    if event.Kind = RunEventKind.ApprovalRequested then
                        approvals.Add event.Approval.Value

                    approvals.Count = 2)
                run
            |> _.Result
            |> ignore

            approvals |> Seq.toArray

        let run =
            Workflow.start runtime definition 0 WorkflowRunOptions.Default CancellationToken.None
            |> _.Result

        let _initialApprovals = collectTwoApprovals run
        let checkpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask().Result

        let resumed =
            Workflow.resume runtime definition checkpoint CancellationToken.None |> _.Result

        let queuedApprovalToolName = "branch:2"

        let isQueuedWaveApproval (event: RunEvent<int list>) =
            event.Kind = RunEventKind.ApprovalRequested
            && StringComparer.Ordinal.Equals(event.Approval.Value.ToolName, queuedApprovalToolName)

        let queuedApprovalBeforeRelease =
            use probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)
            let probe = resumed.Events.GetAsyncEnumerator(probeCts.Token)

            try
                let mutable found = false
                let mutable keepReading = true

                while keepReading && not found do
                    let moved =
                        try
                            probe.MoveNextAsync().AsTask().GetAwaiter().GetResult()
                        with :? OperationCanceledException ->
                            false

                    if not moved then
                        keepReading <- false
                    else
                        found <- isQueuedWaveApproval probe.Current

                found
            finally
                probe.DisposeAsync().AsTask().GetAwaiter().GetResult()

        Assert.False(queuedApprovalBeforeRelease, "Expected the queued branch to stay blocked after resume.")

    [<Fact>]
    let ``simultaneous workflow runs do not share aggregate or loop state`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let left =
            Workflow.define
                "state.left"
                "1.0.0"
                (Workflow.code "state.left.step" (fun _ value -> Task.FromResult(value + 1)))

        let right =
            Workflow.define
                "state.right"
                "1.0.0"
                (Workflow.code "state.right.step" (fun _ value -> Task.FromResult(value * 2)))

        let parallelStep =
            Workflow.``parallel`` "state.parallel" 2 [ left; right ] (fun values -> Task.FromResult(List.sum values))

        let definition = Workflow.define "workflow.state" "1.0.0" parallelStep

        let firstTask =
            Workflow.run runtime definition 2 WorkflowRunOptions.Default CancellationToken.None

        let secondTask =
            Workflow.run runtime definition 10 WorkflowRunOptions.Default CancellationToken.None

        Task.WaitAll(firstTask, secondTask)

        Assert.True(firstTask.Result.Result.IsSuccess)
        Assert.True(secondTask.Result.Result.IsSuccess)
        Assert.Equal(7, firstTask.Result.Result.Value)
        Assert.Equal(31, secondTask.Result.Result.Value)

    [<Fact>]
    let ``parallel aggregate keeps simultaneous runs isolated under high concurrency`` () =
        let runtime = createRuntime () |> asWorkflowRuntime

        let branches =
            [ for index in 0..7 ->
                  Workflow.define
                      $"workflow.aggregate.branch.{index}"
                      "1.0.0"
                      (Workflow.code $"workflow.aggregate.branch.{index}.step" (fun _ value ->
                          task {
                              do! Task.Delay(((value + 1) * (index + 3)) % 17)
                              return value * 100 + index
                          })) ]

        let parallelStep =
            Workflow.``parallel`` "workflow.aggregate.parallel" branches.Length branches (fun values ->
                Task.FromResult values)

        let definition = Workflow.define "workflow.aggregate.stress" "1.0.0" parallelStep

        let runForValue value =
            task {
                let! result = Workflow.run runtime definition value WorkflowRunOptions.Default CancellationToken.None

                return value, result
            }

        let results =
            [| for value in 0..63 -> runForValue value |] |> Task.WhenAll |> _.Result

        for value, result in results do
            Assert.True(result.Result.IsSuccess, $"Expected run {value} to succeed.")
            Assert.Equal<int list>([ for index in 0..7 -> value * 100 + index ], result.Result.Value)
