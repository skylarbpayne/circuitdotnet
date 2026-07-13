module Circuit.MicrosoftAgentFramework.Tests.UnifiedRuntimeTests

open System
open System.Collections.Generic
open System.ComponentModel.DataAnnotations
open System.Diagnostics
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Circuit.MicrosoftAgentFramework
open Microsoft.Extensions.AI
open OpenTelemetry
open OpenTelemetry.Metrics
open OpenTelemetry.Trace
open Xunit

type private ArrayAsyncEnumerable<'T>(items: 'T array) =
    interface IAsyncEnumerable<'T> with
        member _.GetAsyncEnumerator(_cancellationToken) =
            let mutable index = -1

            { new IAsyncEnumerator<'T> with
                member _.Current = items[index]

                member _.MoveNextAsync() =
                    index <- index + 1
                    ValueTask<bool>(index < items.Length)

                member _.DisposeAsync() = ValueTask() }

type private EmptyAsyncEnumerable<'T>() =
    inherit ArrayAsyncEnumerable<'T>(Array.empty)

type private ResumeDependency() = class end

type private ResumeServiceProvider(dependency: ResumeDependency) =
    interface IServiceProvider with
        member _.GetService(serviceType) =
            if serviceType = typeof<ResumeDependency> then
                box dependency
            else
                null

type private ResumeRequiredToolResolver() =
    let mutable resolutions = 0
    member _.Resolutions = resolutions

    interface IToolResolver with
        member _.ResolveAsync(context, _cancellationToken) =
            if isNull (context.Services.GetService(typeof<ResumeDependency>)) then
                raise (InvalidOperationException("The resumed dependency is required."))

            Interlocked.Increment(&resolutions) |> ignore
            ValueTask<IReadOnlyList<ResolvedTool>>(Array.empty<ResolvedTool> :> IReadOnlyList<ResolvedTool>)

type private UnifiedObserver() =
    let events = ResizeArray<Circuit.RunEventEnvelope>()
    member _.Events = events.ToArray()

    interface Circuit.IRunObserver with
        member _.OnEventAsync(event, _cancellationToken) =
            events.Add event
            ValueTask()

type private UnifiedTelemetryHarness() =
    let spans = ResizeArray<Activity>()
    let metrics = ResizeArray<Metric>()

    let tracer =
        Sdk
            .CreateTracerProviderBuilder()
            .AddSource(TelemetryContracts.ActivitySourceName)
            .AddInMemoryExporter(spans)
            .Build()

    let meter =
        Sdk
            .CreateMeterProviderBuilder()
            .AddMeter(TelemetryContracts.ActivitySourceName)
            .AddInMemoryExporter(metrics)
            .Build()

    member _.Spans = spans.ToArray()
    member _.Metrics = metrics.ToArray()

    member _.Flush() =
        tracer.ForceFlush() |> ignore
        meter.ForceFlush() |> ignore

    interface IDisposable with
        member _.Dispose() =
            meter.Dispose()
            tracer.Dispose()

type private ThrowingChatClient() =
    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(_messages, _options, _cancellationToken) =
            Task.FromException<ChatResponse>(InvalidOperationException("not expected"))

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            EmptyAsyncEnumerable<ChatResponseUpdate>() :> IAsyncEnumerable<ChatResponseUpdate>

        member _.GetService(_serviceType, _serviceKey) = null

[<AllowNullLiteral>]
type WriteInput() =
    [<property: Required>]
    member val Path: string = null with get, set

    [<property: Required>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type WriteOutput() =
    [<property: Required>]
    member val Status: string = null with get, set

type private UnifiedApprovalPolicy(result: bool) =
    interface IToolApprovalPolicy with
        member _.IsApprovedAsync(_policyName, _context) = ValueTask<bool>(result)

type private ApprovalChatClient() =
    let mutable calls = 0
    let mutable toolResult: obj = null
    member _.Calls = calls
    member _.ToolResult = toolResult

    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(messages, options, _cancellationToken) =
            calls <- calls + 1

            let hasToolResult =
                messages
                |> Seq.exists (fun message ->
                    message.Role = ChatRole.Tool
                    && not (isNull message.Contents)
                    && message.Contents
                       |> Seq.exists (function
                           | :? FunctionResultContent as result when result.CallId = "write-call" ->
                               toolResult <- result.Result
                               true
                           | _ -> false))

            if hasToolResult then
                Task.FromResult(ChatResponse(ChatMessage(ChatRole.Assistant, "\"completed\"")))
            else
                let arguments = Dictionary<string, obj>()
                arguments["path"] <- "notes.txt"
                arguments["content"] <- "hello"
                let toolName = options.Tools |> Seq.head |> _.Name
                let content = FunctionCallContent("write-call", toolName, arguments)

                Task.FromResult(
                    ChatResponse(
                        ChatMessage(
                            ChatRole.Assistant,
                            ResizeArray<AIContent>([ content :> AIContent ]) :> IList<AIContent>
                        )
                    )
                )

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            EmptyAsyncEnumerable<ChatResponseUpdate>() :> IAsyncEnumerable<ChatResponseUpdate>

        member _.GetService(_serviceType, _serviceKey) = null

type private RepeatedToolChatClient
    (?gate: TaskCompletionSource<unit> * TaskCompletionSource<unit>, ?reverse: bool, ?callPrefix: string) =
    let started, release =
        match gate with
        | Some(started, release) -> Some started, Some release
        | None -> None, None

    let mutable calls = 0
    let reverse = defaultArg reverse false
    let callPrefix = defaultArg callPrefix "repeat"

    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(messages, options, cancellationToken) =
            task {
                calls <- calls + 1

                if calls = 1 then
                    match started, release with
                    | Some signal, Some gate ->
                        signal.TrySetResult(()) |> ignore
                        do! gate.Task.WaitAsync(cancellationToken)
                    | _ -> ()

                let resultCount =
                    messages
                    |> Seq.collect _.Contents
                    |> Seq.filter (fun content -> content :? FunctionResultContent)
                    |> Seq.length

                if resultCount >= 2 then
                    return ChatResponse(ChatMessage(ChatRole.Assistant, "\"completed\""))
                else
                    let toolName = options.Tools |> Seq.head |> _.Name

                    let makeCall callId path =
                        let arguments = Dictionary<string, obj>()
                        arguments["path"] <- path
                        arguments["content"] <- "value"
                        FunctionCallContent(callId, toolName, arguments) :> AIContent

                    let calls =
                        [ makeCall ($"{callPrefix}-1") "one"; makeCall ($"{callPrefix}-2") "two" ]
                        |> fun values -> if reverse then List.rev values else values

                    return
                        ChatResponse(ChatMessage(ChatRole.Assistant, ResizeArray<AIContent>(calls) :> IList<AIContent>))
            }

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            EmptyAsyncEnumerable<ChatResponseUpdate>() :> IAsyncEnumerable<ChatResponseUpdate>

        member _.GetService(_serviceType, _serviceKey) = null

type private SessionStreamingChatClient(blockAfterFirst: bool, output: string) =
    let mutable calls = 0

    let blocked =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let messages = ResizeArray<IReadOnlyList<ChatMessage>>()
    member _.Blocked = blocked.Task
    member _.Calls = calls
    member _.Messages = messages.ToArray()

    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(currentMessages, _options, cancellationToken) =
            task {
                calls <- calls + 1
                messages.Add(currentMessages |> Seq.map _.Clone() |> Seq.toArray)

                if blockAfterFirst && calls > 1 then
                    blocked.TrySetResult(()) |> ignore
                    do! Task.Delay(Timeout.Infinite, cancellationToken)

                return ChatResponse(ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize output))
            }

        member _.GetStreamingResponseAsync(currentMessages, _options, _cancellationToken) =
            calls <- calls + 1
            messages.Add(currentMessages |> Seq.map _.Clone() |> Seq.toArray)

            if blockAfterFirst && calls > 1 then
                { new IAsyncEnumerable<ChatResponseUpdate> with
                    member _.GetAsyncEnumerator(cancellationToken) =
                        { new IAsyncEnumerator<ChatResponseUpdate> with
                            member _.Current = Unchecked.defaultof<_>

                            member _.MoveNextAsync() =
                                blocked.TrySetResult(()) |> ignore

                                ValueTask<bool>(
                                    Task
                                        .Delay(Timeout.Infinite, cancellationToken)
                                        .ContinueWith(fun completed ->
                                            if completed.IsCanceled then
                                                cancellationToken.ThrowIfCancellationRequested()

                                            false)
                                )

                            member _.DisposeAsync() = ValueTask() } }
            else
                ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, JsonSerializer.Serialize output) |])
                :> IAsyncEnumerable<ChatResponseUpdate>

        member _.GetService(_serviceType, _serviceKey) = null

type private BlockingStreamingChatClient() =
    let blocked =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Blocked = blocked.Task

    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(_messages, _options, _cancellationToken) =
            Task.FromException<ChatResponse>(InvalidOperationException("streaming expected"))

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            { new IAsyncEnumerable<ChatResponseUpdate> with
                member _.GetAsyncEnumerator(cancellationToken) =
                    { new IAsyncEnumerator<ChatResponseUpdate> with
                        member _.Current = Unchecked.defaultof<_>

                        member _.MoveNextAsync() =
                            blocked.TrySetResult(()) |> ignore

                            ValueTask<bool>(
                                task {
                                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                                    return false
                                }
                            )

                        member _.DisposeAsync() = ValueTask() } }

        member _.GetService(_serviceType, _serviceKey) = null

type private StreamingChatClient() =
    interface IDisposable with
        member _.Dispose() = ()

    interface IChatClient with
        member _.GetResponseAsync(_messages, _options, _cancellationToken) =
            Task.FromException<ChatResponse>(InvalidOperationException("streaming expected"))

        member _.GetStreamingResponseAsync(_messages, _options, _cancellationToken) =
            ArrayAsyncEnumerable([| ChatResponseUpdate(ChatRole.Assistant, "\"done\"") |])
            :> IAsyncEnumerable<ChatResponseUpdate>

        member _.GetService(_serviceType, _serviceKey) = null

let private createObservedEnvelope
    (runId: string)
    (kind: Circuit.AgentRunEventKind)
    (operationId: string)
    (operationName: string)
    (operationKind: Circuit.RunOperationKind)
    (failure: Circuit.AgentFailure)
    (repaired: bool)
    =
    let methodInfo =
        typeof<Circuit.RunEventEnvelope>.GetMethod("Create", BindingFlags.Static ||| BindingFlags.NonPublic)

    methodInfo.Invoke(
        null,
        [| box runId
           box DateTimeOffset.UtcNow
           box kind
           box "unified-otel"
           box "1.0.0"
           box "unified-otel"
           box operationId
           box operationName
           box operationKind
           null
           null
           null
           null
           null
           null
           box failure
           null
           box (Nullable<DateTimeOffset>())
           box (Nullable<DateTimeOffset>())
           box repaired
           null
           null
           null |]
    )
    :?> Circuit.RunEventEnvelope

let private createObservedFailure code runId operationId =
    let constructor =
        typeof<Circuit.AgentFailure>.GetConstructors(BindingFlags.Instance ||| BindingFlags.NonPublic)
        |> Array.exactlyOne

    constructor.Invoke([| box code; box "controlled"; box runId; box operationId; null; null |])
    :?> Circuit.AgentFailure

[<Fact>]
let ``MafRuntime executes agent leaves through Core scheduler`` () =
    task {
        let runtime =
            new MafRuntime(new StreamingChatClient(), MafRuntimeOptions()) :> ICircuitRuntime

        let agent =
            AgentDefinition.Create(
                "maf-agent",
                "1.0.0",
                "MAF",
                "Return output",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "maf-signature",
                    "1.0.0",
                    "MAF",
                    "Return a JSON string.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let! response =
            Circuit.run runtime (Circuit.agent agent signature) "input" RunOptions.Default CancellationToken.None

        Assert.True(response.IsSuccess, if response.IsSuccess then "" else response.Failure.Message)
        Assert.Equal("done", response.Value)
    }

[<Fact>]
let ``fresh active MAF leaf exposes checkpointable provider session`` () =
    task {
        let agent =
            AgentDefinition.Create(
                "fresh-session-agent",
                "1.0.0",
                "Session",
                "Remember",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "fresh-session",
                    "1.0.0",
                    "Session",
                    "Return JSON",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature
        let firstClient = new BlockingStreamingChatClient()
        let firstRuntime = MafRuntime(firstClient, MafRuntimeOptions()) :> ICircuitRuntime
        let! active = Circuit.start firstRuntime definition "fresh" RunOptions.Default CancellationToken.None
        do! firstClient.Blocked
        let! saved = active.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess, if saved.IsSuccess then "" else saved.Failure.Message)
        let serialized = saved.Value.Serialize()

        Assert.True(
            serialized.GetProperty("payload").GetProperty("sessions").EnumerateObject()
            |> Seq.isEmpty
            |> not
        )

        let checkpoint = CircuitCheckpoint<string>.Deserialize(serialized)
        do! (active :> IAsyncDisposable).DisposeAsync().AsTask()

        let secondRuntime =
            MafRuntime(new StreamingChatClient(), MafRuntimeOptions()) :> ICircuitRuntime

        let! resumed = Circuit.resume secondRuntime definition checkpoint ResumeOptions.Default CancellationToken.None
        let events = ResizeArray<CircuitEvent<string>>()
        let enumerator = resumed.Events.GetAsyncEnumerator()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                events.Add enumerator.Current

        let output =
            events
            |> Seq.choose (function
                | OutputProduced(_, value) -> Some value
                | _ -> None)
            |> Seq.exactlyOne

        Assert.True(output.IsSuccess)
        Assert.Equal("done", output.Value)
        do! enumerator.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``MAF active agent checkpoint restores provider session in a new runtime`` () =
    task {
        let agent =
            AgentDefinition.Create(
                "session-agent",
                "1.0.0",
                "Session",
                "Remember the conversation.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "session-signature",
                    "1.0.0",
                    "Session",
                    "Return a JSON string.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature
        let firstClient = new SessionStreamingChatClient(true, "first")
        let firstRuntime = MafRuntime(firstClient, MafRuntimeOptions()) :> ICircuitRuntime

        let! firstResponse =
            Circuit.run firstRuntime definition "remember-this" RunOptions.Default CancellationToken.None

        Assert.True(firstResponse.IsSuccess)
        Assert.True(firstResponse.Metadata.Session.IsSome)

        let activeOptions =
            RunOptions.Default.WithSession(firstResponse.Metadata.Session.Value)

        let! active = Circuit.start firstRuntime definition "second" activeOptions CancellationToken.None
        do! firstClient.Blocked
        let! checkpoint = active.CreateCheckpointAsync(CancellationToken.None).AsTask()

        Assert.True(
            checkpoint.IsSuccess,
            if checkpoint.IsSuccess then
                ""
            else
                checkpoint.Failure.Message
        )

        let serialized = checkpoint.Value.Serialize()
        let roundTrip = CircuitCheckpoint<string>.Deserialize(serialized)
        do! (active :> IAsyncDisposable).DisposeAsync().AsTask()

        let secondClient = new SessionStreamingChatClient(false, "resumed")
        let secondRuntime = MafRuntime(secondClient, MafRuntimeOptions()) :> ICircuitRuntime
        let! resumed = Circuit.resume secondRuntime definition roundTrip ResumeOptions.Default CancellationToken.None
        let events = ResizeArray<CircuitEvent<string>>()
        let enumerator = resumed.Events.GetAsyncEnumerator()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                events.Add enumerator.Current

        do! enumerator.DisposeAsync().AsTask()

        let output =
            events
            |> Seq.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Seq.exactlyOne

        Assert.True(output.IsSuccess)
        Assert.Equal("resumed", output.Value)
        Assert.Single(secondClient.Messages) |> ignore

        let transcript =
            secondClient.Messages[0]
            |> Seq.collect _.Contents
            |> Seq.choose (function
                | :? TextContent as text -> Some text.Text
                | _ -> None)
            |> String.concat " "

        Assert.Contains("remember-this", transcript)
        Assert.Contains("first", transcript)
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``MAF tool approval flows through unified Circuit run and executes once`` () =
    task {
        let mutable executions = 0
        let input = Contract<WriteInput>.Create(CircuitJson.createOptions (), Seq.empty)
        let output = Contract<WriteOutput>.Create(CircuitJson.createOptions (), Seq.empty)

        let tool =
            ToolDefinition<WriteInput, WriteOutput>
                .Create(
                    "tool.write",
                    "1.0.0",
                    "Write a file",
                    input,
                    output,
                    ApprovalMode.Always,
                    ValueNone,
                    Func<ToolContext, WriteInput, Task<WriteOutput>>(fun context value ->
                        Assert.False(String.IsNullOrWhiteSpace(context.IdempotencyKey))
                        Interlocked.Increment(&executions) |> ignore
                        Task.FromResult(WriteOutput(Status = value.Path)))
                )
            |> ResolvedTool.Create

        let options = MafRuntimeOptions()
        options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |]
        let chatClient = new ApprovalChatClient()
        let runtime = MafRuntime(chatClient, options) :> ICircuitRuntime

        let agent =
            AgentDefinition.Create(
                "approval-agent",
                "1.0.0",
                "Approval",
                "Use the write tool.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "approval-signature",
                    "1.0.0",
                    "Approval",
                    "Return a JSON string.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let! run =
            Circuit.start runtime (Circuit.agent agent signature) "write" RunOptions.Default CancellationToken.None

        let enumerator = run.Events.GetAsyncEnumerator()
        let mutable approvals = 0
        let mutable acceptedCheckpoint: CircuitCheckpoint<string> option = None
        let mutable terminal = false

        while not terminal do
            let! more = enumerator.MoveNextAsync().AsTask()
            Assert.True(more)

            match enumerator.Current with
            | ApprovalRequested request ->
                approvals <- approvals + 1
                Assert.Equal(0, executions)

                let! pendingCheckpoint = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
                Assert.True(pendingCheckpoint.IsSuccess)

                Assert.True(
                    pendingCheckpoint.Value.Serialize().GetProperty("payload").GetProperty("sessions").EnumerateObject()
                    |> Seq.isEmpty
                    |> not
                )

                let! accepted =
                    run.RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None).AsTask()

                Assert.True(accepted.IsSuccess)

                let! savedAfterAcceptance = run.CreateCheckpointAsync(CancellationToken.None).AsTask()
                Assert.True(savedAfterAcceptance.IsSuccess)

                acceptedCheckpoint <-
                    Some(CircuitCheckpoint<string>.Deserialize(savedAfterAcceptance.Value.Serialize()))

                let! replay =
                    run.RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None).AsTask()

                Assert.False(replay.IsSuccess)
            | RunCompleted response ->
                Assert.True(response.IsSuccess, if response.IsSuccess then "" else response.Failure.Message)
                terminal <- true
            | _ -> ()

        Assert.True(
            (approvals = 1),
            $"Approvals: {approvals}; executions: {executions}; provider calls: {chatClient.Calls}"
        )

        Assert.True((executions = 1), $"Tool result: {chatClient.ToolResult}")
        do! enumerator.DisposeAsync().AsTask()
        do! (run :> IAsyncDisposable).DisposeAsync().AsTask()

        let resumedClient = new ApprovalChatClient()
        let resumedRuntime = MafRuntime(resumedClient, options) :> ICircuitRuntime

        let! resumed =
            Circuit.resume
                resumedRuntime
                (Circuit.agent agent signature)
                acceptedCheckpoint.Value
                ResumeOptions.Default
                CancellationToken.None

        let resumedEnumerator = resumed.Events.GetAsyncEnumerator()
        let mutable resumedApprovals = 0
        let mutable resumedMore = true

        while resumedMore do
            let! available = resumedEnumerator.MoveNextAsync().AsTask()
            resumedMore <- available

            if available then
                match resumedEnumerator.Current with
                | ApprovalRequested _ -> resumedApprovals <- resumedApprovals + 1
                | _ -> ()

        Assert.Equal(0, resumedApprovals)
        Assert.InRange(executions, 1, 2)
        do! resumedEnumerator.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }

[<Fact>]
let ``repeated tool invocations have unique replay-stable idempotency keys`` () =
    task {
        let keys = ResizeArray<struct (string * string)>()
        let input = Contract<WriteInput>.Create(CircuitJson.createOptions (), Seq.empty)
        let output = Contract<WriteOutput>.Create(CircuitJson.createOptions (), Seq.empty)

        let tool =
            ToolDefinition<WriteInput, WriteOutput>
                .Create(
                    "tool.repeat",
                    "1.0.0",
                    "Repeated tool",
                    input,
                    output,
                    ApprovalMode.Never,
                    ValueNone,
                    Func<ToolContext, WriteInput, Task<WriteOutput>>(fun context value ->
                        lock keys (fun () -> keys.Add(struct (value.Path, context.IdempotencyKey)))
                        Task.FromResult(WriteOutput(Status = value.Path)))
                )
            |> ResolvedTool.Create

        let options = MafRuntimeOptions()
        options.ToolResolvers <- [| StaticToolResolver([| tool |]) :> IToolResolver |]

        let agent =
            AgentDefinition.Create(
                "repeat-agent",
                "1.0.0",
                "Repeat",
                "Call twice",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create("repeat", "1.0.0", "Repeat", "Return JSON", CircuitJson.createOptions (), Seq.empty, Seq.empty)

        let definition = Circuit.agent agent signature

        let started =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let firstRuntime =
            MafRuntime(new RepeatedToolChatClient((started, release)), options) :> ICircuitRuntime

        let! firstRun = Circuit.start firstRuntime definition "go" RunOptions.Default CancellationToken.None
        do! started.Task
        let! saved = firstRun.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess)
        let checkpoint = CircuitCheckpoint<string>.Deserialize(saved.Value.Serialize())
        release.TrySetResult(()) |> ignore
        let firstEnumerator = firstRun.Events.GetAsyncEnumerator()
        let mutable firstMore = true

        while firstMore do
            let! available = firstEnumerator.MoveNextAsync().AsTask()
            firstMore <- available

        do! firstEnumerator.DisposeAsync().AsTask()
        let first = keys.ToArray()
        do! (firstRun :> IAsyncDisposable).DisposeAsync().AsTask()

        keys.Clear()

        let secondRuntime =
            MafRuntime(new RepeatedToolChatClient(reverse = true, callPrefix = "changed"), options) :> ICircuitRuntime

        let! replayedRun =
            Circuit.resume secondRuntime definition checkpoint ResumeOptions.Default CancellationToken.None

        let replayEnumerator = replayedRun.Events.GetAsyncEnumerator()
        let mutable replayMore = true

        while replayMore do
            let! available = replayEnumerator.MoveNextAsync().AsTask()
            replayMore <- available

        do! replayEnumerator.DisposeAsync().AsTask()
        let replay = keys.ToArray()
        do! (replayedRun :> IAsyncDisposable).DisposeAsync().AsTask()
        Assert.Equal(2, first.Length)

        Assert.Equal(
            2,
            first
            |> Array.map (fun struct (_, key) -> key)
            |> Array.distinct
            |> Array.length
        )

        let byPath values =
            values |> Seq.map (fun struct (path, key) -> path, key) |> dict

        let firstByPath = byPath first
        let replayByPath = byPath replay
        Assert.Equal(firstByPath["one"], replayByPath["one"])
        Assert.Equal(firstByPath["two"], replayByPath["two"])
    }

[<Fact>]
let ``ByPolicy tools fail closed and auto approval controls unified pause behavior`` () =
    task {
        let agent =
            AgentDefinition.Create(
                "policy-agent",
                "1.0.0",
                "Policy",
                "Use the write tool.",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "policy-signature",
                    "1.0.0",
                    "Policy",
                    "Return a JSON string.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature

        let createTool (executions: int ref) =
            ToolDefinition<WriteInput, WriteOutput>
                .Create(
                    "tool.policy-write",
                    "1.0.0",
                    "Write a file",
                    Contract<WriteInput>.Create(CircuitJson.createOptions (), Seq.empty),
                    Contract<WriteOutput>.Create(CircuitJson.createOptions (), Seq.empty),
                    ApprovalMode.ByPolicy,
                    ValueSome "trusted",
                    Func<ToolContext, WriteInput, Task<WriteOutput>>(fun _ value ->
                        executions.Value <- executions.Value + 1
                        Task.FromResult(WriteOutput(Status = value.Path)))
                )
            |> ResolvedTool.Create

        let missingExecutions = ref 0
        let missingClient = new ApprovalChatClient()
        let missingOptions = MafRuntimeOptions()
        missingOptions.ToolResolvers <- [| StaticToolResolver([| createTool missingExecutions |]) :> IToolResolver |]
        let missingRuntime = MafRuntime(missingClient, missingOptions) :> ICircuitRuntime
        let! missing = Circuit.run missingRuntime definition "write" RunOptions.Default CancellationToken.None
        Assert.False(missing.IsSuccess)
        Assert.Equal(CircuitFailureCode.Tool, missing.Failure.Code)
        Assert.Equal(0, missingClient.Calls)
        Assert.Equal(0, missingExecutions.Value)

        let runPolicy decision =
            task {
                let executions = ref 0
                let client = new ApprovalChatClient()
                let options = MafRuntimeOptions()
                options.ToolResolvers <- [| StaticToolResolver([| createTool executions |]) :> IToolResolver |]
                options.ToolApprovalPolicy <- ValueSome(UnifiedApprovalPolicy(decision) :> IToolApprovalPolicy)
                let runtime = MafRuntime(client, options) :> ICircuitRuntime
                let! run = Circuit.start runtime definition "write" RunOptions.Default CancellationToken.None
                let enumerator = run.Events.GetAsyncEnumerator()
                let mutable approvals = 0
                let mutable terminal = false

                while not terminal do
                    let! more = enumerator.MoveNextAsync().AsTask()
                    Assert.True(more)

                    match enumerator.Current with
                    | ApprovalRequested request ->
                        approvals <- approvals + 1

                        let! accepted =
                            run
                                .RespondAsync(ApprovalResponse.Create(request.RequestId, true), CancellationToken.None)
                                .AsTask()

                        Assert.True(accepted.IsSuccess)
                    | RunCompleted response ->
                        Assert.True(response.IsSuccess)
                        terminal <- true
                    | _ -> ()

                do! enumerator.DisposeAsync().AsTask()
                do! (run :> IAsyncDisposable).DisposeAsync().AsTask()
                return approvals, executions.Value
            }

        let! autoApprovals, autoExecutions = runPolicy true
        Assert.Equal(0, autoApprovals)
        Assert.Equal(1, autoExecutions)
        let! pausedApprovals, pausedExecutions = runPolicy false
        Assert.Equal(1, pausedApprovals)
        Assert.Equal(1, pausedExecutions)
    }

[<Fact>]
let ``Circuit root observer correlates code nodes under one scheduler run`` () =
    task {
        let observer = UnifiedObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]
        let runtime = MafRuntime(new ThrowingChatClient(), options) :> ICircuitRuntime

        let definition =
            Circuit.code "observed-code" "1.0.0" (fun context value ->
                Task.FromResult(Response.succeed context (value + 1)))

        let! response = Circuit.run runtime definition 1 RunOptions.Default CancellationToken.None
        Assert.True(response.IsSuccess)
        let events = observer.Events
        Assert.Contains(events, fun event -> event.Kind = Circuit.AgentRunEventKind.RunStarted)
        Assert.Contains(events, fun event -> event.Kind = Circuit.AgentRunEventKind.StepStarted)
        Assert.Contains(events, fun event -> event.Kind = Circuit.AgentRunEventKind.StepCompleted)
        Assert.Contains(events, fun event -> event.Kind = Circuit.AgentRunEventKind.RunCompleted)
        Assert.Single(events |> Array.map _.RunId |> Array.distinct) |> ignore
        Assert.Equal(response.Metadata.RunId.Value, events[0].RunId)
    }

[<Fact>]
let ``OpenTelemetry forced close exports the full metric set`` () =
    use telemetry = new UnifiedTelemetryHarness()
    let observer = OpenTelemetryRunObserver() :> Circuit.IRunObserver
    let runId = Guid.NewGuid().ToString("N")
    let failure = createObservedFailure Circuit.AgentFailureCode.Validation runId "root"

    let publish kind operationId operationName operationKind eventFailure repaired =
        observer
            .OnEventAsync(
                createObservedEnvelope runId kind operationId operationName operationKind eventFailure repaired,
                CancellationToken.None
            )
            .AsTask()
            .GetAwaiter()
            .GetResult()

    publish Circuit.AgentRunEventKind.RunStarted runId "circuit.run" Circuit.RunOperationKind.Run null false
    publish Circuit.AgentRunEventKind.ToolStarted "tool-1" "tool.one" Circuit.RunOperationKind.Tool null false
    publish Circuit.AgentRunEventKind.StepStarted "step-1" "step.one" Circuit.RunOperationKind.Node null false

    publish
        Circuit.AgentRunEventKind.ApprovalRequested
        "approval-1"
        "approval"
        Circuit.RunOperationKind.Approval
        null
        false

    publish Circuit.AgentRunEventKind.RunFailed runId "circuit.run" Circuit.RunOperationKind.Run failure true
    telemetry.Flush()

    let names = telemetry.Metrics |> Seq.map _.Name |> Set.ofSeq

    let expected =
        set
            [ "circuit.runs"
              "circuit.run.duration"
              "circuit.runs.active"
              "circuit.tools"
              "circuit.tool.duration"
              "circuit.workflow.steps"
              "circuit.workflow.step.duration"
              "circuit.validation.failures"
              "circuit.approvals.requested"
              "circuit.structured_output.repairs" ]

    Assert.Equal<Set<string>>(expected, names)
    let spans = telemetry.Spans

    let tool =
        Assert.Single(spans |> Array.filter (fun span -> span.OperationName = "tool.one"))

    let step =
        Assert.Single(spans |> Array.filter (fun span -> span.OperationName = "step.one"))

    Assert.Equal(ActivityStatusCode.Error, tool.Status)
    Assert.Equal(ActivityStatusCode.Error, step.Status)
    Assert.False(tool.SpanId = Unchecked.defaultof<ActivitySpanId>)
    Assert.False(step.SpanId = Unchecked.defaultof<ActivitySpanId>)

[<Fact>]
let ``OpenTelemetry isolates simultaneous unified Circuit runs`` () =
    task {
        use telemetry = new UnifiedTelemetryHarness()
        let observer = OpenTelemetryRunObserver()
        let options = MafRuntimeOptions()
        options.Observers <- [| observer :> Circuit.IRunObserver |]
        let runtime = MafRuntime(new ThrowingChatClient(), options) :> ICircuitRuntime

        let definition =
            Circuit.code "parallel-observed" "1.0.0" (fun context value ->
                task {
                    do! Task.Delay(20, context.CancellationToken)
                    return Response.succeed context (value + 1)
                })

        let! results =
            Task.WhenAll(
                [| Circuit.run runtime definition 1 RunOptions.Default CancellationToken.None
                   Circuit.run runtime definition 10 RunOptions.Default CancellationToken.None |]
            )

        Assert.All(results, fun response -> Assert.True(response.IsSuccess))
        telemetry.Flush()

        let roots =
            telemetry.Spans |> Array.filter (fun span -> span.OperationName = "circuit.run")

        Assert.Equal(2, roots.Length)
        Assert.Equal(2, roots |> Array.map _.TraceId |> Array.distinct |> Array.length)

        let steps =
            telemetry.Spans
            |> Array.filter (fun span -> span.OperationName.Contains("parallel-observed", StringComparison.Ordinal))

        Assert.Equal(2, steps.Length)

        for step in steps do
            let root = roots |> Array.find (fun candidate -> candidate.TraceId = step.TraceId)
            Assert.Equal(root.SpanId, step.ParentSpanId)
    }

[<Fact>]
let ``MafRuntime exposes only unified runtime and Core schedules non-agent graph`` () =
    task {
        let runtime = new MafRuntime(new ThrowingChatClient(), MafRuntimeOptions())
        let unified = runtime :> ICircuitRuntime
        let! response = Circuit.run unified (Circuit.value 7) () RunOptions.Default CancellationToken.None
        Assert.True(response.IsSuccess)
        Assert.Equal(7, response.Value)

        Assert.DoesNotContain(
            runtime.GetType().GetInterfaces(),
            fun value -> value.Name.Contains("Workflow" + "Runtime", StringComparison.Ordinal)
        )
    }

[<Fact>]
let ``MAF session deserialization resolves capabilities from rebound resume services`` () =
    task {
        let agent =
            AgentDefinition.Create(
                "resume-di-agent",
                "1.0.0",
                "Resume DI",
                "Remember",
                ValueNone,
                Seq.empty,
                Seq.empty,
                Seq.empty
            )

        let signature =
            Signature<string, string>
                .Create(
                    "resume-di",
                    "1.0.0",
                    "Resume DI",
                    "Return JSON",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let definition = Circuit.agent agent signature
        let firstResolver = ResumeRequiredToolResolver()
        let firstMafOptions = MafRuntimeOptions()
        firstMafOptions.ToolResolvers <- [| firstResolver :> IToolResolver |]
        let firstClient = new SessionStreamingChatClient(true, "first")
        let initialRuntime = MafRuntime(firstClient, MafRuntimeOptions()) :> ICircuitRuntime
        let firstRuntime = MafRuntime(firstClient, firstMafOptions) :> ICircuitRuntime
        let defaults = RunOptions.Default
        let firstServices = ResumeServiceProvider(ResumeDependency()) :> IServiceProvider

        let initialOptions =
            RunOptions(
                ValueNone,
                defaults.TenantId,
                defaults.UserId,
                defaults.Tags,
                defaults.StructuredOutputPolicy,
                defaults.SensitiveDataMode,
                firstServices
            )

        let! initial = Circuit.run initialRuntime definition "remember" initialOptions CancellationToken.None
        Assert.True(initial.IsSuccess, if initial.IsSuccess then "" else initial.Failure.Message)
        Assert.True(initial.Metadata.Session.IsSome)

        let activeOptions =
            RunOptions(
                initial.Metadata.Session,
                defaults.TenantId,
                defaults.UserId,
                defaults.Tags,
                defaults.StructuredOutputPolicy,
                defaults.SensitiveDataMode,
                firstServices
            )

        let! active = Circuit.start firstRuntime definition "active" activeOptions CancellationToken.None
        do! firstClient.Blocked
        let! saved = active.CreateCheckpointAsync(CancellationToken.None).AsTask()
        Assert.True(saved.IsSuccess, if saved.IsSuccess then "" else saved.Failure.Message)
        let checkpoint = CircuitCheckpoint<string>.Deserialize(saved.Value.Serialize())
        do! (active :> IAsyncDisposable).DisposeAsync().AsTask()

        let resumedResolver = ResumeRequiredToolResolver()
        let resumedMafOptions = MafRuntimeOptions()
        resumedMafOptions.ToolResolvers <- [| resumedResolver :> IToolResolver |]

        let resumedRuntime =
            MafRuntime(new SessionStreamingChatClient(false, "resumed"), resumedMafOptions) :> ICircuitRuntime

        let resumedServices = ResumeServiceProvider(ResumeDependency()) :> IServiceProvider

        let! resumed =
            Circuit.resume resumedRuntime definition checkpoint (ResumeOptions(resumedServices)) CancellationToken.None

        let events = ResizeArray<CircuitEvent<string>>()
        let enumerator = resumed.Events.GetAsyncEnumerator()
        let mutable more = true

        while more do
            let! available = enumerator.MoveNextAsync().AsTask()
            more <- available

            if available then
                events.Add enumerator.Current

        let output =
            events
            |> Seq.choose (function
                | OutputProduced(_, response) -> Some response
                | _ -> None)
            |> Seq.exactlyOne

        Assert.True(output.IsSuccess, if output.IsSuccess then "" else output.Failure.Message)
        Assert.True(resumedResolver.Resolutions > 0)
        do! enumerator.DisposeAsync().AsTask()
        do! (resumed :> IAsyncDisposable).DisposeAsync().AsTask()
    }
