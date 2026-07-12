namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open Circuit.Core
open Xunit

module IdentifiersAndResultsTests =
    let private invokeCopyTags tags =
        let owner = typeof<RunOptions>.Assembly.GetType("Circuit.Core.RunValidation", true)

        let methodInfo =
            owner.GetMethod("copyTags", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)

        methodInfo.Invoke(null, [| box tags |]) :?> IReadOnlyDictionary<string, string>

    [<Fact>]
    let ``run ids parse compare and reject invalid values`` () =
        let value = String('a', 32)
        let parsed = RunId.Parse(value)
        let mutable reparsed = Unchecked.defaultof<RunId>

        Assert.True(RunId.TryParse(value, &reparsed))
        Assert.Equal(parsed, reparsed)
        Assert.True(parsed.Equals(box reparsed))
        Assert.False(parsed.Equals("not-a-run-id"))
        Assert.Equal(0, (parsed :> IComparable).CompareTo(box reparsed))
        Assert.Equal(1, (parsed :> IComparable).CompareTo(null))

        let invalidParse =
            Assert.Throws<ArgumentException>(fun () -> RunId.Parse("ABC") |> ignore)

        Assert.Equal("value", invalidParse.ParamName)

        let mutable failed = parsed
        Assert.False(RunId.TryParse("ABC", &failed))

        let invalidCompare =
            Assert.Throws<ArgumentException>(fun () -> (parsed :> IComparable).CompareTo("ABC") |> ignore)

        Assert.Equal("other", invalidCompare.ParamName)

    [<Fact>]
    let ``definition ids and semantic versions compare using their canonical values`` () =
        let definition = DefinitionId.Create("agent.test")
        let mutable reparsedDefinition = Unchecked.defaultof<DefinitionId>
        Assert.True(DefinitionId.TryCreate("agent.test", &reparsedDefinition))
        Assert.Equal(definition, reparsedDefinition)
        Assert.False(definition.Equals(42))
        Assert.Equal(0, (definition :> IComparable).CompareTo(box reparsedDefinition))
        Assert.Equal(1, (definition :> IComparable).CompareTo(null))

        let invalidDefinition =
            Assert.Throws<ArgumentException>(fun () -> DefinitionId.Create("Agent.Test") |> ignore)

        Assert.Equal("value", invalidDefinition.ParamName)

        let mutable failedDefinition = definition
        Assert.False(DefinitionId.TryCreate("Agent.Test", &failedDefinition))

        let invalidDefinitionCompare =
            Assert.Throws<ArgumentException>(fun () -> (definition :> IComparable).CompareTo(42) |> ignore)

        Assert.Equal("other", invalidDefinitionCompare.ParamName)

        let version = SemanticVersion.Parse("1.2.3")
        let mutable reparsedVersion = Unchecked.defaultof<SemanticVersion>
        Assert.True(SemanticVersion.TryParse("1.2.3", &reparsedVersion))
        Assert.Equal(version, reparsedVersion)
        Assert.False(version.Equals("1.2.3"))
        Assert.Equal(0, (version :> IComparable).CompareTo(box reparsedVersion))
        Assert.Equal(1, (version :> IComparable).CompareTo(null))

        let mutable failedVersion = version
        Assert.False(SemanticVersion.TryParse("1.2", &failedVersion))

        let invalidVersionCompare =
            Assert.Throws<ArgumentException>(fun () -> (version :> IComparable).CompareTo("1.2.3") |> ignore)

        Assert.Equal("other", invalidVersionCompare.ParamName)

    [<Fact>]
    let ``circuit results expose value or failure branches consistently`` () =
        let success = CircuitResult<int>.Success(42)
        let mutable successValue = 0

        Assert.True(success.IsSuccess)
        Assert.Equal(42, success.Value)
        Assert.True(success.TryGetValue(&successValue))
        Assert.Equal(42, successValue)

        let successFailure =
            Assert.Throws<InvalidOperationException>(fun () -> success.Failure |> ignore)

        Assert.Equal("The result does not contain a failure.", successFailure.Message)

        let failure =
            CircuitFailure(
                CircuitFailureCode.Provider,
                "boom",
                ValueSome(RunId.New()),
                ValueSome("op-0001"),
                ValueSome("req-1"),
                ValueSome(InvalidOperationException("inner") :> exn)
            )

        let error = CircuitResult<int>.Error(failure)
        let mutable failedValue = 99

        Assert.False(error.IsSuccess)
        Assert.Equal(failure, error.Failure)
        Assert.False(error.TryGetValue(&failedValue))
        Assert.Equal(0, failedValue)

        let missingValue =
            Assert.Throws<InvalidOperationException>(fun () -> error.Value |> ignore)

        Assert.Equal("The result does not contain a value.", missingValue.Message)

        let blankFailure =
            Assert.Throws<ArgumentException>(fun () ->
                CircuitFailure(CircuitFailureCode.Workflow, " ", ValueNone, ValueNone, ValueNone, ValueNone)
                |> ignore)

        Assert.Equal("message", blankFailure.ParamName)

        let nullFailure =
            Assert.Throws<ArgumentNullException>(fun () ->
                CircuitResult<int>.Error(Unchecked.defaultof<CircuitFailure>) |> ignore)

        Assert.Equal("failure", nullFailure.ParamName)

    [<Fact>]
    let ``default run options use documented defaults`` () =
        let defaults = RunOptions.Default

        Assert.Equal(ValueNone, defaults.Session)
        Assert.Equal(ValueNone, defaults.TenantId)
        Assert.Equal(ValueNone, defaults.UserId)
        Assert.Empty(defaults.Tags)
        Assert.Equal(StructuredOutputPolicy.NativeOnly, defaults.StructuredOutputPolicy)
        Assert.Equal(SensitiveDataMode.Standard, defaults.SensitiveDataMode)
        Assert.NotNull(defaults.Services)

    [<Fact>]
    let ``WithSession replaces the session and preserves every other property`` () =
        let tags =
            Dictionary<string, string>(seq { KeyValuePair("team", "core") }, StringComparer.Ordinal)
            :> IReadOnlyDictionary<string, string>

        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let options =
            RunOptions(
                ValueNone,
                ValueSome("tenant-1"),
                ValueSome("user-1"),
                tags,
                StructuredOutputPolicy.AllowSecondaryModelRepair,
                SensitiveDataMode.Redact,
                services
            )

        let session =
            CircuitSession(
                "session-1",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let updated = options.WithSession(session)

        Assert.NotSame(options, updated)
        Assert.Equal(ValueSome session, updated.Session)
        Assert.Equal(options.TenantId, updated.TenantId)
        Assert.Equal(options.UserId, updated.UserId)
        Assert.Same(options.Tags, updated.Tags)
        Assert.Equal(options.StructuredOutputPolicy, updated.StructuredOutputPolicy)
        Assert.Equal(options.SensitiveDataMode, updated.SensitiveDataMode)
        Assert.Same(options.Services, updated.Services)

        let nullSession =
            Assert.Throws<ArgumentNullException>(fun () ->
                options.WithSession(Unchecked.defaultof<CircuitSession>) |> ignore)

        Assert.Equal("session", nullSession.ParamName)

    [<Fact>]
    let ``WithStructuredOutputPolicy replaces the policy and preserves every other property`` () =
        let tags =
            Dictionary<string, string>(seq { KeyValuePair("team", "core") }, StringComparer.Ordinal)
            :> IReadOnlyDictionary<string, string>

        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let session =
            CircuitSession(
                "session-1",
                Dictionary<string, string>() :> IReadOnlyDictionary<string, string>,
                ValueNone,
                ValueNone,
                ValueNone
            )

        let options =
            RunOptions(
                ValueSome session,
                ValueSome("tenant-1"),
                ValueSome("user-1"),
                tags,
                StructuredOutputPolicy.AllowSecondaryModelRepair,
                SensitiveDataMode.Redact,
                services
            )

        let updated = options.WithStructuredOutputPolicy(StructuredOutputPolicy.NativeOnly)

        Assert.NotSame(options, updated)
        Assert.Equal(options.Session, updated.Session)
        Assert.Equal(options.TenantId, updated.TenantId)
        Assert.Equal(options.UserId, updated.UserId)
        Assert.Same(options.Tags, updated.Tags)
        Assert.Equal(StructuredOutputPolicy.NativeOnly, updated.StructuredOutputPolicy)
        Assert.Equal(options.SensitiveDataMode, updated.SensitiveDataMode)
        Assert.Same(options.Services, updated.Services)

    [<Theory>]
    [<InlineData(-1)>]
    [<InlineData(2)>]
    let ``WithStructuredOutputPolicy rejects values outside the defined enum range`` invalidValue =
        let invalidPolicy = enum<StructuredOutputPolicy> invalidValue

        let error =
            Assert.Throws<ArgumentOutOfRangeException>(fun () ->
                RunOptions.Default.WithStructuredOutputPolicy(invalidPolicy) |> ignore)

        Assert.Equal("policy", error.ParamName)
        Assert.Equal(invalidPolicy, unbox<StructuredOutputPolicy> error.ActualValue)

    [<Fact>]
    let ``run options and approval requests validate constructor arguments`` () =
        let tags =
            Dictionary<string, string>(seq { KeyValuePair("team", "core") }, StringComparer.Ordinal)
            :> IReadOnlyDictionary<string, string>

        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let options =
            RunOptions(
                ValueNone,
                ValueSome("tenant-1"),
                ValueSome("user-1"),
                tags,
                StructuredOutputPolicy.AllowSecondaryModelRepair,
                SensitiveDataMode.Redact,
                services
            )

        Assert.Equal(ValueSome("tenant-1"), options.TenantId)
        Assert.Equal(ValueSome("user-1"), options.UserId)
        Assert.Equal(StructuredOutputPolicy.AllowSecondaryModelRepair, options.StructuredOutputPolicy)
        Assert.Equal(SensitiveDataMode.Redact, options.SensitiveDataMode)
        Assert.Same(services, options.Services)

        let nullTags =
            Assert.Throws<ArgumentNullException>(fun () ->
                RunOptions(
                    ValueNone,
                    ValueNone,
                    ValueNone,
                    null,
                    StructuredOutputPolicy.NativeOnly,
                    SensitiveDataMode.Standard,
                    services
                )
                |> ignore)

        Assert.Equal("tags", nullTags.ParamName)

        let nullServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                RunOptions(
                    ValueNone,
                    ValueNone,
                    ValueNone,
                    tags,
                    StructuredOutputPolicy.NativeOnly,
                    SensitiveDataMode.Standard,
                    null
                )
                |> ignore)

        Assert.Equal("services", nullServices.ParamName)

        let request =
            ApprovalRequest("request-1", "tool.read", ValueSome("{\"path\":\"README.md\"}"))

        Assert.Equal("request-1", request.RequestId)
        Assert.Equal("tool.read", request.ToolName)
        Assert.True(request.ArgumentsJson.IsSome)

        let invalidRequestId =
            Assert.Throws<ArgumentException>(fun () -> ApprovalRequest(" ", "tool.read", ValueNone) |> ignore)

        Assert.Equal("requestId", invalidRequestId.ParamName)

        let invalidToolName =
            Assert.Throws<ArgumentException>(fun () -> ApprovalRequest("request-1", " ", ValueNone) |> ignore)

        Assert.Equal("toolName", invalidToolName.ParamName)

    [<Fact>]
    let ``circuit failure code mappings and signature creation cover every branch`` () =
        for code in Enum.GetValues<CircuitFailureCode>() do
            let failure =
                CircuitFailure(code, $"{code}", ValueNone, ValueNone, ValueNone, ValueNone)

            let result = CircuitResult<int>.Error(failure)
            Assert.False(result.IsSuccess)
            Assert.Equal(code, result.Failure.Code)

        let options = CircuitJson.createOptions ()

        let signature =
            Signature<int, int>
                .Create(
                    "signature.test",
                    "1.0.0",
                    "Describe the signature.",
                    "Return the same value.",
                    options,
                    Seq.empty,
                    Seq.empty
                )

        Assert.NotSame(options, signature.JsonSerializerOptions)
        Assert.True(signature.JsonSerializerOptions.IsReadOnly)

        let blankDescription =
            Assert.Throws<ArgumentException>(fun () ->
                Signature<int, int>
                    .Create("signature.test", "1.0.0", " ", "Return the same value.", options, Seq.empty, Seq.empty)
                |> ignore)

        Assert.Equal("description", blankDescription.ParamName)

        let blankInstructions =
            Assert.Throws<ArgumentException>(fun () ->
                Signature<int, int>
                    .Create("signature.test", "1.0.0", "Describe the signature.", " ", options, Seq.empty, Seq.empty)
                |> ignore)

        Assert.Equal("instructions", blankInstructions.ParamName)

        let nullOptions =
            Assert.Throws<ArgumentNullException>(fun () ->
                Signature<int, int>
                    .Create(
                        "signature.test",
                        "1.0.0",
                        "Describe the signature.",
                        "Return the same value.",
                        null,
                        Seq.empty,
                        Seq.empty
                    )
                |> ignore)

        Assert.Equal("jsonOptions", nullOptions.ParamName)

        let nullInputValidators =
            Assert.Throws<ArgumentNullException>(fun () ->
                Signature<int, int>
                    .Create(
                        "signature.test",
                        "1.0.0",
                        "Describe the signature.",
                        "Return the same value.",
                        options,
                        null,
                        Seq.empty
                    )
                |> ignore)

        Assert.Equal("inputValidators", nullInputValidators.ParamName)

        let nullOutputValidators =
            Assert.Throws<ArgumentNullException>(fun () ->
                Signature<int, int>
                    .Create(
                        "signature.test",
                        "1.0.0",
                        "Describe the signature.",
                        "Return the same value.",
                        options,
                        Seq.empty,
                        null
                    )
                |> ignore)

        Assert.Equal("outputValidators", nullOutputValidators.ParamName)

    [<Fact>]
    let ``unknown failure codes are rejected and run session event data round trips`` () =
        let invalidCode = enum<CircuitFailureCode> 999

        let ex =
            Assert.Throws<ArgumentException>(fun () ->
                CircuitFailure(invalidCode, "bad", ValueNone, ValueNone, ValueNone, ValueNone)
                |> CircuitResult<int>.Error
                |> ignore)

        Assert.Equal("failure", ex.ParamName)

        let runId = RunId.New()

        let session =
            CircuitSession(
                "session-1",
                Dictionary<string, string>(seq { KeyValuePair("team", "core") }, StringComparer.Ordinal)
                :> IReadOnlyDictionary<string, string>,
                ValueSome("adapter"),
                ValueSome("fingerprint"),
                ValueSome(box 42)
            )

        let failure =
            CircuitFailure(
                CircuitFailureCode.Tool,
                "tool failed",
                ValueSome(runId),
                ValueSome("op-1"),
                ValueSome("req-1"),
                ValueNone
            )

        let approval = ApprovalRequest("request-1", "tool.read", ValueSome("{}"))
        let startedAt = DateTimeOffset.UtcNow
        let completedAt = startedAt.AddSeconds(1)

        let result =
            RunResult(
                runId,
                CircuitResult<int>.Error(failure),
                RunUsage(2, 3),
                ValueSome session,
                startedAt,
                completedAt
            )

        let event =
            RunEvent<int>(
                7L,
                runId,
                startedAt,
                RunEventKind.ApprovalRequested,
                ValueSome("op-1"),
                ValueSome("delta"),
                ValueSome 4,
                ValueSome failure,
                ValueSome approval
            )

        Assert.Equal(5, result.Usage.TotalTokens)
        Assert.True(result.Session.IsSome)
        Assert.Equal("session-1", result.Session.Value.Id)
        Assert.Equal("core", result.Session.Value.Metadata["team"])
        Assert.Equal(7L, event.Sequence)
        Assert.Equal(RunEventKind.ApprovalRequested, event.Kind)
        Assert.Equal(ValueSome("delta"), event.TextDelta)
        Assert.Equal(ValueSome 4, event.Value)
        Assert.Equal(ValueSome failure, event.Failure)
        Assert.Equal(ValueSome approval, event.Approval)

    [<Fact>]
    let ``run tag validation rejects reserved duplicate and oversized entries`` () =
        let valid = invokeCopyTags (seq { KeyValuePair("team", "core") })
        Assert.Equal("core", valid["team"])

        let tooManyTags =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (Seq.init 33 (fun index -> KeyValuePair($"tag-{index}", "value")))
                |> ignore)

        Assert.Equal("tags", (tooManyTags.InnerException :?> ArgumentException).ParamName)

        let blankKey =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (seq { KeyValuePair(" ", "value") }) |> ignore)

        Assert.Equal("tags", (blankKey.InnerException :?> ArgumentException).ParamName)

        let reservedKey =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (seq { KeyValuePair("circuit.trace", "value") }) |> ignore)

        Assert.Equal("tags", (reservedKey.InnerException :?> ArgumentException).ParamName)

        let longKey =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (seq { KeyValuePair(String.replicate 65 "k", "value") })
                |> ignore)

        Assert.Equal("tags", (longKey.InnerException :?> ArgumentException).ParamName)

        let nullValue =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags ([| KeyValuePair("team", null) |] :> seq<KeyValuePair<string, string>>)
                |> ignore)

        Assert.Equal("tags", (nullValue.InnerException :?> ArgumentNullException).ParamName)

        let longValue =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (seq { KeyValuePair("team", String.replicate 257 "v") })
                |> ignore)

        Assert.Equal("tags", (longValue.InnerException :?> ArgumentException).ParamName)

        let duplicateKey =
            Assert.Throws<TargetInvocationException>(fun () ->
                invokeCopyTags (
                    seq {
                        KeyValuePair("team", "one")
                        KeyValuePair("team", "two")
                    }
                )
                |> ignore)

        Assert.Equal("tags", (duplicateKey.InnerException :?> ArgumentException).ParamName)

    [<Fact>]
    let ``agent definitions validate null collections blank model hints and null skill entries`` () =
        let source = SkillSource.CreateInline("Use the inline skill.")
        let skill = SkillReference.Create("skill.one", "1.0.0", "Skill", source)

        let blankModelHint =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent",
                    "Do things.",
                    ValueSome(" "),
                    Seq.empty,
                    Seq.singleton skill,
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("modelHint", blankModelHint.ParamName)

        let nullToolTags =
            Assert.Throws<ArgumentNullException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent",
                    "Do things.",
                    ValueNone,
                    null,
                    Seq.singleton skill,
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("toolTags", nullToolTags.ParamName)

        let nullSkills =
            Assert.Throws<ArgumentNullException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    null,
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("skills", nullSkills.ParamName)

        let nullMetadata =
            Assert.Throws<ArgumentNullException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    Seq.singleton skill,
                    null
                )
                |> ignore)

        Assert.Equal("metadata", nullMetadata.ParamName)

        let nullSkillEntry =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    seq {
                        skill
                        Unchecked.defaultof<SkillReference>
                    },
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("skills", nullSkillEntry.ParamName)
