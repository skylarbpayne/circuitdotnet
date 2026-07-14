namespace Circuit.Testing

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Circuit.Core

module internal Reflection =
    let private nonPublicInstance = BindingFlags.Instance ||| BindingFlags.NonPublic
    let private nonPublicStatic = BindingFlags.Static ||| BindingFlags.NonPublic

    let private requireConstructor (owner: Type) (parameterTypes: Type array) =
        match owner.GetConstructor(nonPublicInstance, null, parameterTypes, null) with
        | null -> invalidOp $"Could not find internal constructor for {owner.FullName}."
        | ctor -> ctor

    let private circuitFailureCtor =
        requireConstructor
            typeof<CircuitFailure>
            [| typeof<CircuitFailureCode>
               typeof<string>
               typeof<RunId voption>
               typeof<string voption>
               typeof<string voption>
               typeof<exn voption> |]

    let private runUsageCtor =
        requireConstructor typeof<RunUsage> [| typeof<int>; typeof<int> |]

    let private circuitSessionCtor =
        requireConstructor
            typeof<CircuitSession>
            [| typeof<string>
               typeof<IReadOnlyDictionary<string, string>>
               typeof<string voption>
               typeof<string voption>
               typeof<obj voption> |]

    let private runEventCtor<'T> =
        requireConstructor
            (typedefof<RunEvent<_>>.MakeGenericType(typeof<'T>))
            [| typeof<int64>
               typeof<RunId>
               typeof<DateTimeOffset>
               typeof<RunEventKind>
               typeof<string voption>
               typeof<string voption>
               typeof<'T voption>
               typeof<CircuitFailure voption>
               typeof<ApprovalRequest voption> |]

    let private runResultCtor<'T> =
        requireConstructor
            (typedefof<RunResult<_>>.MakeGenericType(typeof<'T>))
            [| typeof<RunId>
               typedefof<CircuitResult<_>>.MakeGenericType(typeof<'T>)
               typeof<RunUsage>
               typeof<CircuitSession voption>
               typeof<DateTimeOffset>
               typeof<DateTimeOffset> |]

    let createFailure
        (code: CircuitFailureCode)
        (message: string)
        (runId: RunId voption)
        (operationId: string voption)
        (requestId: string voption)
        (innerException: exn voption)
        =
        circuitFailureCtor.Invoke(
            [| box code
               box message
               box runId
               box operationId
               box requestId
               box innerException |]
        )
        :?> CircuitFailure

    let createRunUsage inputTokens outputTokens =
        runUsageCtor.Invoke([| box inputTokens; box outputTokens |]) :?> RunUsage

    let createRunEvent<'T>
        (sequence: int64)
        (runId: RunId)
        (timestamp: DateTimeOffset)
        (kind: RunEventKind)
        (operationId: string voption)
        (textDelta: string voption)
        (value: 'T voption)
        (failure: CircuitFailure voption)
        (approval: ApprovalRequest voption)
        =
        runEventCtor<'T>
            .Invoke(
                [| box sequence
                   box runId
                   box timestamp
                   box kind
                   box operationId
                   box textDelta
                   box value
                   box failure
                   box approval |]
            )
        :?> RunEvent<'T>

    let createRunResult<'T>
        (runId: RunId)
        (result: CircuitResult<'T>)
        (usage: RunUsage)
        (session: CircuitSession voption)
        (startedAt: DateTimeOffset)
        (completedAt: DateTimeOffset)
        =
        runResultCtor<'T>
            .Invoke(
                [| box runId
                   box result
                   box usage
                   box session
                   box startedAt
                   box completedAt |]
            )
        :?> RunResult<'T>

    let createSession (id: string) (metadata: IReadOnlyDictionary<string, string>) =
        circuitSessionCtor.Invoke(
            [| box id
               box metadata
               box (ValueNone: string voption)
               box (ValueNone: string voption)
               box (ValueNone: obj voption) |]
        )
        :?> CircuitSession

    let getSignatureJsonOptions (signature: Signature<'Input, 'Output>) =
        let propertyInfo =
            signature.GetType().GetProperty("JsonSerializerOptions", nonPublicInstance)

        if isNull propertyInfo then
            invalidOp "Could not access the signature JSON serializer options."

        propertyInfo.GetValue(signature) :?> JsonSerializerOptions

    let stampFailureRunId (runId: RunId) (failure: CircuitFailure) =
        if isNull (box failure) then
            nullArg "failure"

        match failure.RunId with
        | ValueSome _ -> failure
        | ValueNone ->
            createFailure
                failure.Code
                failure.Message
                (ValueSome runId)
                failure.OperationId
                failure.RequestId
                failure.Exception

    let createCancelledFailure (runId: RunId) =
        createFailure
            CircuitFailureCode.Cancelled
            "The scripted response waited for cancellation and the token was cancelled."
            (ValueSome runId)
            ValueNone
            ValueNone
            ValueNone

    let createSessionState (session: CircuitSession) =
        let metadata =
            let sorted = SortedDictionary<string, string>(StringComparer.Ordinal)

            for KeyValue(key, value) in session.Metadata do
                sorted[key] <- value

            sorted :> IReadOnlyDictionary<string, string>

        let metadataNode = JsonObject()

        for KeyValue(key, value) in metadata do
            metadataNode[key] <- JsonValue.Create(value)

        let stateNode = JsonObject()
        stateNode["id"] <- JsonValue.Create(session.Id)
        stateNode["metadata"] <- metadataNode

        use document = JsonDocument.Parse(stateNode.ToJsonString())
        document.RootElement.Clone()

    let deserializeSessionState (state: JsonElement) =
        let root =
            if state.ValueKind = JsonValueKind.Undefined then
                invalidArg "state" "Session state JSON must be defined."
            else
                state

        let idProperty = ref Unchecked.defaultof<JsonElement>

        if not (root.TryGetProperty("id", idProperty)) then
            invalidArg "state" "Session state JSON must contain an 'id' property."

        let id = (!idProperty).GetString()

        if String.IsNullOrWhiteSpace id then
            invalidArg "state" "Session state JSON must contain a non-blank 'id' property."

        let metadata = SortedDictionary<string, string>(StringComparer.Ordinal)
        let metadataProperty = ref Unchecked.defaultof<JsonElement>

        if root.TryGetProperty("metadata", metadataProperty) then
            if (!metadataProperty).ValueKind <> JsonValueKind.Object then
                invalidArg "state" "Session state 'metadata' must be a JSON object."

            for property in (!metadataProperty).EnumerateObject() do
                metadata[property.Name] <- property.Value.GetString()

        createSession id (metadata :> IReadOnlyDictionary<string, string>)

    let getEnvelopeFactory () =
        let methodInfo =
            typeof<Circuit.RunEventEnvelope>.GetMethod("Create", nonPublicStatic)

        if isNull methodInfo then
            invalidOp "Could not access the internal RunEventEnvelope factory."

        methodInfo

module internal Snapshots =
    let private jsonOptions =
        let options = CircuitJson.createOptions ()
        options.MakeReadOnly()
        options

    let private nonPublicInstance = BindingFlags.Instance ||| BindingFlags.NonPublic

    let private getPropertyValue (propertyName: string) (instance: obj) =
        let propertyInfo = instance.GetType().GetProperty(propertyName)

        if isNull propertyInfo then
            invalidOp $"Could not find property '{propertyName}' on {instance.GetType().FullName}."

        propertyInfo.GetValue(instance)

    let private getNonPublicPropertyValue (propertyName: string) (instance: obj) =
        let propertyInfo = instance.GetType().GetProperty(propertyName, nonPublicInstance)

        if isNull propertyInfo then
            invalidOp $"Could not find non-public property '{propertyName}' on {instance.GetType().FullName}."

        propertyInfo.GetValue(instance)

    let private getSchemaJson (contract: obj) =
        let schema = getPropertyValue "Schema" contract
        let methodInfo = schema.GetType().GetMethod("ToJsonString", Type.EmptyTypes)

        if isNull methodInfo then
            invalidOp $"Could not serialize schema from {contract.GetType().FullName}."

        methodInfo.Invoke(schema, [||]) :?> string

    let private getContractValueType (contract: obj) =
        getPropertyValue "ValueType" contract :?> Type

    let private validateContractValue (contract: obj) (value: obj) =
        let methodInfo = contract.GetType().GetMethod("Validate")

        if isNull methodInfo then
            invalidOp $"Could not validate value with {contract.GetType().FullName}."

        methodInfo.Invoke(contract, [| value |]) :?> IReadOnlyList<ValidationIssue>

    let getSignatureId (signature: obj) =
        let id = getPropertyValue "Id" signature
        getPropertyValue "Value" id :?> string

    let getSignatureVersion (signature: obj) =
        let version = getPropertyValue "Version" signature
        version.ToString()

    let getInputSchemaJson (signature: obj) =
        getPropertyValue "Input" signature |> getSchemaJson

    let getOutputSchemaJson (signature: obj) =
        getPropertyValue "Output" signature |> getSchemaJson

    let serializeInputUntyped (signature: obj) (input: obj) =
        let inputContract = getPropertyValue "Input" signature
        let inputType = getContractValueType inputContract

        let options =
            getNonPublicPropertyValue "JsonSerializerOptions" signature :?> JsonSerializerOptions

        JsonSerializer.Serialize(input, inputType, options)

    let deserializeOutputUntyped<'Output> (signature: obj) (json: string) =
        let options =
            getNonPublicPropertyValue "JsonSerializerOptions" signature :?> JsonSerializerOptions

        JsonSerializer.Deserialize<'Output>(json, options)

    let convertOutputValueUntyped<'Output> (signature: obj) (value: obj) =
        let options =
            getNonPublicPropertyValue "JsonSerializerOptions" signature :?> JsonSerializerOptions

        match value with
        | null -> JsonSerializer.Deserialize<'Output>("null", options)
        | :? 'Output as typed -> typed
        | other ->
            let json = JsonSerializer.Serialize(other, other.GetType(), options)
            JsonSerializer.Deserialize<'Output>(json, options)

    let validateOutputUntyped<'Output> (signature: obj) (value: 'Output) =
        let issues =
            getPropertyValue "Output" signature
            |> fun contract -> validateContractValue contract (box value)

        if issues.Count > 0 then
            let formatted =
                issues
                |> Seq.map (fun issue -> $"{issue.Path} ({issue.Code}): {issue.Message}")
                |> String.concat "; "

            invalidOp $"The scripted response did not satisfy the output contract: {formatted}"

        value

    let serializeOptions (options: RunOptions) =
        let tags = SortedDictionary<string, string>(StringComparer.Ordinal)

        for KeyValue(key, value) in options.Tags do
            tags[key] <- value

        let sessionId, sessionMetadata =
            match options.Session with
            | ValueSome session ->
                let metadata = SortedDictionary<string, string>(StringComparer.Ordinal)

                for KeyValue(key, value) in session.Metadata do
                    metadata[key] <- value

                session.Id, metadata :> IReadOnlyDictionary<string, string>
            | ValueNone -> null, null

        let sessionMetadataNode =
            if isNull sessionMetadata then
                null
            else
                let node = JsonObject()

                for KeyValue(key, value) in sessionMetadata do
                    node[key] <- JsonValue.Create(value)

                node

        let tagsNode = JsonObject()

        for KeyValue(key, value) in tags do
            tagsNode[key] <- JsonValue.Create(value)

        let snapshot = JsonObject()
        snapshot["sessionId"] <- JsonValue.Create(sessionId)
        snapshot["sessionMetadata"] <- sessionMetadataNode
        snapshot["tenantId"] <- JsonValue.Create(options.TenantId |> ValueOption.toObj)
        snapshot["userId"] <- JsonValue.Create(options.UserId |> ValueOption.toObj)
        snapshot["tags"] <- tagsNode
        snapshot["structuredOutputPolicy"] <- JsonValue.Create(options.StructuredOutputPolicy)
        snapshot["sensitiveDataMode"] <- JsonValue.Create(options.SensitiveDataMode)

        snapshot["servicesType"] <-
            JsonValue.Create(
                if isNull options.Services then
                    null
                else
                    options.Services.GetType().AssemblyQualifiedName
            )

        snapshot.ToJsonString(jsonOptions)


/// Thrown when a scripted runtime call is attempted after all scripted responses were consumed.
[<Sealed>]
type ScriptedResponseExhaustedException(message: string) =
    inherit InvalidOperationException(message)

/// Represents one scripted runtime response.
[<RequireQualifiedAccess>]
type ScriptedResponse =
    /// Return the supplied JSON text and decode it through the active signature output contract.
    | OutputJson of string
    /// Return the supplied object directly as the typed output value.
    | OutputValue of obj
    /// Emit a streaming run with the supplied text deltas before completing successfully.
    | Stream of string list
    /// Complete the run with the supplied Circuit failure.
    | Failure of CircuitFailure
    /// Raise the supplied exception from the runtime call.
    | Throw of exn
    /// Start the stream and then block until cancellation is requested.
    | WaitForCancellation
    /// Match this response deterministically to a scheduler node-path suffix.
    | ForNode of nodePathSuffix: string * response: ScriptedResponse

/// Identifies how a scripted call reached the runtime.
type ScriptedCallKind =
    /// A non-streaming <c>RunAsync</c> call.
    | Run = 0
    /// A streaming <c>RunStreamingAsync</c> call.
    | Streaming = 1

/// Captures one recorded call made to <see cref="T:Circuit.Testing.ScriptedRuntime" />.
[<Sealed>]
type RecordedCall
    internal
    (
        kind: ScriptedCallKind,
        runId: string,
        nodePath: string,
        agentId: string,
        agentVersion: string,
        agentName: string,
        signatureId: string,
        signatureVersion: string,
        inputJson: string,
        inputSchemaJson: string,
        outputSchemaJson: string,
        optionsJson: string
    ) =
    /// Gets whether the runtime was called in streaming or non-streaming mode.
    member _.Kind = kind

    /// Gets the scheduler run identifier.
    member _.RunId = runId

    /// Gets the distinct scheduler node path.
    member _.NodePath = nodePath

    /// Gets the agent identifier.
    member _.AgentId = agentId

    /// Gets the agent version.
    member _.AgentVersion = agentVersion

    /// Gets the agent name.
    member _.AgentName = agentName

    /// Gets the signature identifier.
    member _.SignatureId = signatureId

    /// Gets the signature version.
    member _.SignatureVersion = signatureVersion

    /// Gets the serialized input payload.
    member _.InputJson = inputJson

    /// Gets the serialized input schema.
    member _.InputSchemaJson = inputSchemaJson

    /// Gets the serialized output schema.
    member _.OutputSchemaJson = outputSchemaJson

    /// Gets the serialized run options snapshot.
    member _.OptionsJson = optionsJson

/// Convenience constructors for <see cref="T:Circuit.Testing.ScriptedResponse" /> values.
[<AbstractClass; Sealed>]
type ScriptedResponses private () =
    /// Creates an <see cref="F:Circuit.Testing.ScriptedResponse.OutputJson" /> response.
    static member OutputJson(json: string) =
        if isNull json then
            nullArg "json"

        ScriptedResponse.OutputJson json

    /// Creates an <see cref="F:Circuit.Testing.ScriptedResponse.OutputValue" /> response.
    static member OutputValue(value: obj) = ScriptedResponse.OutputValue value

    /// Creates an <see cref="F:Circuit.Testing.ScriptedResponse.Stream" /> response.
    static member Stream(chunks: IEnumerable<string>) =
        if isNull chunks then
            nullArg "chunks"

        let snapshot = chunks |> Seq.toList

        if snapshot |> List.exists isNull then
            invalidArg "chunks" "Stream chunks cannot contain null entries."

        ScriptedResponse.Stream snapshot

    /// Creates a <see cref="F:Circuit.Testing.ScriptedResponse.Failure" /> response.
    static member Failure(failure: CircuitFailure) =
        if isNull (box failure) then
            nullArg "failure"

        ScriptedResponse.Failure failure

    /// Creates a <see cref="F:Circuit.Testing.ScriptedResponse.Throw" /> response.
    static member Throw(error: exn) =
        if isNull error then
            nullArg "error"

        ScriptedResponse.Throw error

    /// Creates a <see cref="F:Circuit.Testing.ScriptedResponse.WaitForCancellation" /> response.
    static member WaitForCancellation() = ScriptedResponse.WaitForCancellation

    /// Creates a response matched to a distinct scheduler node-path suffix.
    static member ForNode(nodePathSuffix: string, response: ScriptedResponse) =
        if String.IsNullOrWhiteSpace nodePathSuffix then
            invalidArg "nodePathSuffix" "nodePathSuffix cannot be blank."

        ScriptedResponse.ForNode(nodePathSuffix, response)

/// Creates <see cref="T:Circuit.Core.CircuitFailure" /> values for tests.
[<AbstractClass; Sealed>]
type TestFailures private () =
    /// Creates a test failure with only a code and message.
    static member Create(code: CircuitFailureCode, message: string) =
        Reflection.createFailure code message ValueNone ValueNone ValueNone ValueNone

    /// Creates a test failure with optional run, operation, request, and exception metadata.
    static member Create
        (
            code: CircuitFailureCode,
            message: string,
            runId: string,
            operationId: string,
            requestId: string,
            innerException: exn
        ) =
        let runIdValue =
            if String.IsNullOrWhiteSpace runId then
                ValueNone
            else
                ValueSome(RunId.Parse runId)

        let operationIdValue =
            if String.IsNullOrWhiteSpace operationId then
                ValueNone
            else
                ValueSome operationId

        let requestIdValue =
            if String.IsNullOrWhiteSpace requestId then
                ValueNone
            else
                ValueSome requestId

        let innerExceptionValue =
            if isNull innerException then
                ValueNone
            else
                ValueSome innerException

        Reflection.createFailure code message runIdValue operationIdValue requestIdValue innerExceptionValue

module internal AsyncEnumerable =
    type private ArrayAsyncEnumerator<'T>(items: 'T[]) =
        let mutable index = -1

        interface IAsyncEnumerator<'T> with
            member _.Current = items[index]

            member _.MoveNextAsync() =
                index <- index + 1
                ValueTask<bool>(index < items.Length)

            member _.DisposeAsync() = ValueTask()

    type ArrayAsyncEnumerable<'T>(items: 'T[]) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_cancellationToken) =
                ArrayAsyncEnumerator(items) :> IAsyncEnumerator<'T>

    type ThrowingAsyncEnumerable<'T>(error: exn) =
        interface IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_cancellationToken) =
                { new IAsyncEnumerator<'T> with
                    member _.Current = Unchecked.defaultof<'T>

                    member _.MoveNextAsync() =
                        ValueTask<bool>(Task.FromException<bool>(error))

                    member _.DisposeAsync() = ValueTask() }

    type WaitForCancellationAsyncEnumerable<'T>(started: RunEvent<'T>, terminal: RunEvent<'T>) =
        interface IAsyncEnumerable<RunEvent<'T>> with
            member _.GetAsyncEnumerator(cancellationToken) =
                let mutable state = 0
                let mutable current = Unchecked.defaultof<RunEvent<'T>>
                let mutable registration = Unchecked.defaultof<CancellationTokenRegistration>

                { new IAsyncEnumerator<RunEvent<'T>> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        match state with
                        | 0 ->
                            state <- 1
                            current <- started
                            ValueTask<bool>(true)
                        | 1 ->
                            let task =
                                task {
                                    if cancellationToken.IsCancellationRequested then
                                        ()
                                    else
                                        let tcs =
                                            TaskCompletionSource<unit>(
                                                TaskCreationOptions.RunContinuationsAsynchronously
                                            )

                                        registration <-
                                            cancellationToken.Register(fun () -> tcs.TrySetResult(()) |> ignore)

                                        do! tcs.Task

                                    registration.Dispose()
                                    current <- terminal
                                    state <- 2
                                    return true
                                }

                            ValueTask<bool>(task)
                        | _ -> ValueTask<bool>(false)

                    member _.DisposeAsync() =
                        registration.Dispose()
                        ValueTask() }

module internal ScriptedRuntimeHelpers =
    let inline recordCall
        (calls: ConcurrentQueue<RecordedCall>)
        kind
        (runId: RunId)
        (nodePath: string)
        (agent: AgentDefinition)
        (signature: obj)
        (input: obj)
        (options: RunOptions)
        =
        if isNull (box agent) then
            nullArg "agent"

        if isNull (box signature) then
            nullArg "signature"

        if isNull (box options) then
            nullArg "options"

        let call =
            RecordedCall(
                kind,
                runId.Value,
                nodePath,
                agent.Id.Value,
                agent.Version.ToString(),
                agent.Name,
                Snapshots.getSignatureId signature,
                Snapshots.getSignatureVersion signature,
                Snapshots.serializeInputUntyped signature input,
                Snapshots.getInputSchemaJson signature,
                Snapshots.getOutputSchemaJson signature,
                Snapshots.serializeOptions options
            )

        calls.Enqueue call

    let inline dequeueResponse
        (responses: ConcurrentQueue<ScriptedResponse>)
        (matched: ConcurrentDictionary<string, ScriptedResponse>)
        (nodePath: string)
        (runId: RunId)
        (agent: AgentDefinition)
        (signature: obj)
        =
        let matchingKey =
            matched.Keys
            |> Seq.filter (fun suffix -> nodePath.EndsWith(suffix, StringComparison.Ordinal))
            |> Seq.sortByDescending _.Length
            |> Seq.tryHead

        match matchingKey with
        | Some suffix ->
            match matched.TryRemove suffix with
            | true, response -> response
            | _ ->
                raise (
                    ScriptedResponseExhaustedException(
                        $"The matched scripted response '{suffix}' was already consumed."
                    )
                )
        | None ->
            match responses.TryDequeue() with
            | true, response -> response
            | false, _ ->
                raise (
                    ScriptedResponseExhaustedException(
                        $"ScriptedRuntime has no response matching node '{nodePath}' for run '{runId.Value}', agent '{agent.Name}', signature '{Snapshots.getSignatureId signature}'."
                    )
                )

    let inline createResult<'Output> (runId: RunId) (result: CircuitResult<'Output>) startedAt completedAt =
        Reflection.createRunResult<'Output> runId result (Reflection.createRunUsage 0 0) ValueNone startedAt completedAt

    let inline createStartedEvent<'Output> (runId: RunId) timestamp =
        Reflection.createRunEvent<'Output>
            0L
            runId
            timestamp
            RunEventKind.RunStarted
            ValueNone
            ValueNone
            ValueNone
            ValueNone
            ValueNone

    let inline createDeltaEvent<'Output> (runId: RunId) sequence timestamp delta =
        Reflection.createRunEvent<'Output>
            sequence
            runId
            timestamp
            RunEventKind.OutputDelta
            ValueNone
            (ValueSome delta)
            ValueNone
            ValueNone
            ValueNone

    let inline createCompletedEvent<'Output> (runId: RunId) sequence timestamp value =
        Reflection.createRunEvent<'Output>
            sequence
            runId
            timestamp
            RunEventKind.RunCompleted
            ValueNone
            ValueNone
            (ValueSome value)
            ValueNone
            ValueNone

    let inline createFailedEvent<'Output> (runId: RunId) sequence timestamp failure =
        Reflection.createRunEvent<'Output>
            sequence
            runId
            timestamp
            RunEventKind.RunFailed
            ValueNone
            ValueNone
            ValueNone
            (ValueSome failure)
            ValueNone

    let inline readSuccess (signature: obj) response : 'Output =
        match response with
        | ScriptedResponse.OutputJson json ->
            if isNull json then
                nullArg "response"

            json
            |> Snapshots.deserializeOutputUntyped signature
            |> Snapshots.validateOutputUntyped signature
        | ScriptedResponse.OutputValue value ->
            value
            |> Snapshots.convertOutputValueUntyped signature
            |> Snapshots.validateOutputUntyped signature
        | ScriptedResponse.Stream deltas ->
            if deltas |> List.exists isNull then
                invalidOp "Scripted stream responses cannot contain null chunks."

            deltas
            |> String.concat ""
            |> Snapshots.deserializeOutputUntyped signature
            |> Snapshots.validateOutputUntyped signature
        | ScriptedResponse.Failure _
        | ScriptedResponse.Throw _
        | ScriptedResponse.WaitForCancellation
        | ScriptedResponse.ForNode _ -> invalidOp "The scripted response does not describe a successful output."

    let waitForCancellation (cancellationToken: CancellationToken) =
        task {
            if cancellationToken.IsCancellationRequested then
                return ()
            else
                let tcs =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                use _registration =
                    cancellationToken.Register(fun () -> tcs.TrySetResult(()) |> ignore)

                do! tcs.Task
        }

    let inline runAsync
        (responseQueue: ConcurrentQueue<ScriptedResponse>)
        (matchedResponses: ConcurrentDictionary<string, ScriptedResponse>)
        (calls: ConcurrentQueue<RecordedCall>)
        (runId: RunId)
        (nodePath: string)
        (agent: AgentDefinition)
        (signature: obj)
        (input: obj)
        (options: RunOptions)
        (onDelta: string -> Task)
        (cancellationToken: CancellationToken)
        =
        let startedAt = DateTimeOffset.UtcNow
        recordCall calls ScriptedCallKind.Run runId nodePath agent signature input options

        let response =
            dequeueResponse responseQueue matchedResponses nodePath runId agent signature

        task {
            match response with
            | ScriptedResponse.OutputJson _
            | ScriptedResponse.OutputValue _ ->
                let output = readSuccess signature response
                let completedAt = DateTimeOffset.UtcNow
                return createResult runId (CircuitResult<'Output>.Success output) startedAt completedAt
            | ScriptedResponse.Stream deltas ->
                for delta in deltas do
                    do! onDelta delta

                let output = readSuccess signature response
                let completedAt = DateTimeOffset.UtcNow
                return createResult runId (CircuitResult<'Output>.Success output) startedAt completedAt
            | ScriptedResponse.Failure failure ->
                let completedAt = DateTimeOffset.UtcNow
                let stampedFailure = Reflection.stampFailureRunId runId failure
                return createResult runId (CircuitResult<'Output>.Error stampedFailure) startedAt completedAt
            | ScriptedResponse.Throw error -> return raise error
            | ScriptedResponse.WaitForCancellation ->
                do! waitForCancellation cancellationToken
                let completedAt = DateTimeOffset.UtcNow
                let failure = Reflection.createCancelledFailure runId
                return createResult runId (CircuitResult<'Output>.Error failure) startedAt completedAt
            | ScriptedResponse.ForNode _ -> return invalidOp "Matched responses must be unwrapped before execution."
        }

    let inline runStreaming
        (responseQueue: ConcurrentQueue<ScriptedResponse>)
        (calls: ConcurrentQueue<RecordedCall>)
        (agent: AgentDefinition)
        (signature: obj)
        (input: obj)
        (options: RunOptions)
        (_cancellationToken: CancellationToken)
        =
        let runId = RunId.New()
        let startedAt = DateTimeOffset.UtcNow
        recordCall calls ScriptedCallKind.Streaming runId "legacy-stream" agent signature input options

        let response =
            dequeueResponse
                responseQueue
                (ConcurrentDictionary<string, ScriptedResponse>(StringComparer.Ordinal))
                "legacy-stream"
                runId
                agent
                signature

        match response with
        | ScriptedResponse.OutputJson _
        | ScriptedResponse.OutputValue _ ->
            let output = readSuccess signature response
            let completedAt = DateTimeOffset.UtcNow

            AsyncEnumerable.ArrayAsyncEnumerable(
                [| createStartedEvent runId startedAt
                   createCompletedEvent runId 1L completedAt output |]
            )
            :> IAsyncEnumerable<RunEvent<'Output>>
        | ScriptedResponse.Stream deltas ->
            let output = readSuccess signature (ScriptedResponse.Stream deltas)
            let events = ResizeArray<RunEvent<'Output>>()
            events.Add(createStartedEvent runId startedAt)

            deltas
            |> List.iteri (fun index delta ->
                events.Add(createDeltaEvent runId (int64 index + 1L) (DateTimeOffset.UtcNow) delta))

            events.Add(createCompletedEvent runId (int64 deltas.Length + 1L) (DateTimeOffset.UtcNow) output)
            AsyncEnumerable.ArrayAsyncEnumerable(events.ToArray()) :> IAsyncEnumerable<RunEvent<'Output>>
        | ScriptedResponse.Failure failure ->
            let completedAt = DateTimeOffset.UtcNow
            let stampedFailure = Reflection.stampFailureRunId runId failure

            AsyncEnumerable.ArrayAsyncEnumerable(
                [| createStartedEvent runId startedAt
                   createFailedEvent runId 1L completedAt stampedFailure |]
            )
            :> IAsyncEnumerable<RunEvent<'Output>>
        | ScriptedResponse.Throw error ->
            AsyncEnumerable.ThrowingAsyncEnumerable error :> IAsyncEnumerable<RunEvent<'Output>>
        | ScriptedResponse.WaitForCancellation ->
            let terminal =
                createFailedEvent runId 1L (DateTimeOffset.UtcNow) (Reflection.createCancelledFailure runId)

            AsyncEnumerable.WaitForCancellationAsyncEnumerable(createStartedEvent runId startedAt, terminal)
            :> IAsyncEnumerable<RunEvent<'Output>>
        | ScriptedResponse.ForNode _ -> invalidOp "Matched responses are supported only by unified scheduler leaves."

/// Implements the unified Circuit runtime with a deterministic queue of scripted agent-leaf responses.
[<Sealed>]
type ScriptedRuntime(responses: IEnumerable<ScriptedResponse>) =
    inherit CircuitRuntime()

    let responseSnapshot =
        if isNull responses then
            nullArg "responses"

        responses |> Seq.toArray

    let matchedResponses =
        ConcurrentDictionary<string, ScriptedResponse>(StringComparer.Ordinal)

    let ordinaryResponses = ResizeArray<ScriptedResponse>()

    do
        for response in responseSnapshot do
            match response with
            | ScriptedResponse.ForNode(suffix, value) ->
                if String.IsNullOrWhiteSpace suffix then
                    invalidArg "responses" "Matched node suffixes cannot be blank."

                if not (matchedResponses.TryAdd(suffix, value)) then
                    invalidArg "responses" $"Duplicate matched node suffix '{suffix}'."
            | value -> ordinaryResponses.Add value

    let responseQueue = ConcurrentQueue<ScriptedResponse>(ordinaryResponses)
    let calls = ConcurrentQueue<RecordedCall>()

    /// Gets the calls recorded so far.
    member _.Calls = calls.ToArray() :> IReadOnlyList<RecordedCall>

    /// Gets the number of scripted responses that have not yet been consumed.
    member _.RemainingResponses = responseQueue.Count + matchedResponses.Count

    /// <summary>Gets the execute agent async value.</summary>
    override _.ExecuteAgentAsync<'Input, 'Output>
        (
            schedulerRunId,
            nodePath,
            agent,
            signature: Signature<'Input, 'Output>,
            input: 'Input,
            options,
            _idempotencyKey,
            onDelta,
            _onApproval,
            _onSession,
            cancellationToken
        ) : Task<RunResult<'Output>> =
        ScriptedRuntimeHelpers.runAsync
            responseQueue
            matchedResponses
            calls
            schedulerRunId
            nodePath
            agent
            (box signature)
            (box input)
            options
            onDelta
            cancellationToken

    /// <summary>Gets the serialize session core async value.</summary>
    override _.SerializeSessionCoreAsync(_agent, session, _runOptions, _cancellationToken) =
        if isNull (box session) then
            nullArg "session"

        ValueTask<JsonElement>(Reflection.createSessionState session)

    /// <summary>Gets the deserialize session core async value.</summary>
    override _.DeserializeSessionCoreAsync(_agent, state, _runOptions, _cancellationToken) =
        ValueTask<CircuitSession>(Reflection.deserializeSessionState state)
