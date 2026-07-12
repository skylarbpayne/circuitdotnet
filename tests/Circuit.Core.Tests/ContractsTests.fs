namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

[<Description("Ticket input payload")>]
type SchemaInput() =
    [<property: Description("Ticket name")>]
    [<property: JsonRequired>]
    member val Name = "" with get, set

    member val Count = 0 with get, set

[<AllowNullLiteral>]
type CyclicNode() =
    [<property: Required>]
    member val Name: string = null with get, set

    member val Children = ResizeArray<CyclicNode>() with get, set
    member val Parent: CyclicNode = null with get, set

[<AllowNullLiteral>]
type SharedReferenceNode() =
    [<property: Required>]
    member val Name: string = null with get, set

    member val Left: SharedReferenceNode = null with get, set
    member val Right: SharedReferenceNode = null with get, set

[<AllowNullLiteral>]
type ResolverInput() =
    member val Name: string = null with get, set

[<AllowNullLiteral>]
type MutableResolver() =
    let inner = DefaultJsonTypeInfoResolver()
    member val RequireName = false with get, set

    interface IJsonTypeInfoResolver with
        member this.GetTypeInfo(typ, options) =
            let jsonTypeInfo = inner.GetTypeInfo(typ, options)

            if not (isNull jsonTypeInfo) && this.RequireName && typ = typeof<ResolverInput> then
                for propertyInfo in jsonTypeInfo.Properties do
                    if String.Equals(propertyInfo.Name, "name", StringComparison.OrdinalIgnoreCase) then
                        propertyInfo.IsRequired <- true

            jsonTypeInfo

type PrefixNamingPolicy(prefix: string) =
    inherit JsonNamingPolicy()

    override _.ConvertName(name) = prefix + name

type DerivedEnumConverter() =
    inherit JsonStringEnumConverter()

type CallbackValidator<'T>(validate: 'T -> IReadOnlyList<ValidationIssue>) =
    interface IContractValidator<'T> with
        member _.Validate(value) = validate value

[<AllowNullLiteral>]
type CustomValidationPayload() =
    member val DisplayName: string = null with get, set

    interface IValidatableObject with
        member _.Validate(_validationContext) =
            seq {
                ValidationResult(" ", [| "displayname" |])
                ValidationResult(null, [| "missingMember" |])
                ValidationResult("", Array.empty<string>)
            }

[<AllowNullLiteral>]
type NamingPolicyLeaf() =
    [<property: Required>]
    member val DisplayName: string = null with get, set

    [<property: Required; property: JsonPropertyName("wire_name")>]
    member val WireName: string = null with get, set

[<AllowNullLiteral>]
type NamingPolicyRoot() =
    member val Leaf = NamingPolicyLeaf() with get, set

type CacheProbe<'T>() =
    member val Value = Unchecked.defaultof<'T> with get, set

type NullServiceProvider() =
    interface IServiceProvider with
        member _.GetService(_serviceType) = null

type SignatureInput() =
    member val Name: string = null with get, set

type SignatureOutput() =
    member val Accepted = false with get, set

module ContractsTests =
    let private createCacheProbeContract<'T> () =
        Contract<CacheProbe<'T>>.Create(CircuitJson.createOptions (), Seq.empty)

    let private createTestTool id version approval approvalPolicy =
        ToolDefinition<SignatureInput, SignatureOutput>
            .Create(
                id,
                version,
                $"{id} description",
                Contract<SignatureInput>.Create(CircuitJson.createOptions (), Seq.empty),
                Contract<SignatureOutput>.Create(CircuitJson.createOptions (), Seq.empty),
                approval,
                approvalPolicy,
                Func<ToolContext, SignatureInput, Task<SignatureOutput>>(fun _ _ ->
                    Task.FromResult(SignatureOutput(Accepted = true)))
            )

    [<Fact>]
    let ``generated schema uses camel case, descriptions, required members, and disallows unknown members`` () =
        let options = CircuitJson.createOptions ()
        let contract = Contract<SchemaInput>.Create(options, Seq.empty)
        let schemaDocument = contract.Schema
        let schema = schemaDocument.RootElement

        Assert.Equal(JsonValueKind.Object, schemaDocument.ValueType)
        Assert.Equal(JsonValueKind.Object, schema.ValueKind)
        Assert.Equal("Ticket input payload", schema.GetProperty("description").GetString())
        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind)

        let properties = schema.GetProperty("properties")
        Assert.True(properties.TryGetProperty("name") |> fst)
        Assert.False(properties.TryGetProperty("Name") |> fst)
        Assert.Equal("Ticket name", properties.GetProperty("name").GetProperty("description").GetString())

        let required = schema.GetProperty("required")
        let requiredNames = required.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
        Assert.Contains("name", requiredNames)

    [<Fact>]
    let ``validation paths use the contract naming policy and json property overrides`` () =
        let options = CircuitJson.createOptions ()
        options.PropertyNamingPolicy <- PrefixNamingPolicy("json_")

        let contract = Contract<NamingPolicyRoot>.Create(options, Seq.empty)
        let issues = contract.Validate(NamingPolicyRoot())

        Assert.Equal(2, issues.Count)
        let paths = issues |> Seq.map _.Path |> Set.ofSeq
        Assert.Contains("$.json_Leaf.json_DisplayName", paths)
        Assert.Contains("$.json_Leaf.wire_name", paths)

    [<Fact>]
    let ``custom validation results use fallback messages and resolve member names without a naming policy`` () =
        let options = CircuitJson.createOptions ()
        options.PropertyNamingPolicy <- null

        let contract = Contract<CustomValidationPayload>.Create(options, Seq.empty)
        let issues = contract.Validate(CustomValidationPayload())

        Assert.Equal(3, issues.Count)
        let issueMap = issues |> Seq.map (fun issue -> issue.Path, issue.Message) |> dict
        Assert.Equal("Validation failed.", issueMap["$.DisplayName"])
        Assert.Equal("Validation failed.", issueMap["$.missingMember"])
        Assert.Equal("Validation failed.", issueMap["$"])

    [<Fact>]
    let ``recursive validation reports nested collection paths without looping on object cycles`` () =
        let root = CyclicNode(Name = "root")
        let child = CyclicNode(Parent = root)
        root.Children.Add child

        let contract = Contract<CyclicNode>.Create(CircuitJson.createOptions (), Seq.empty)
        let issues = contract.Validate root

        Assert.Single issues |> ignore
        let issue = issues[0]
        Assert.Equal("$.children[0].name", issue.Path)

    [<Fact>]
    let ``self-referential collections terminate without overflowing the stack`` () =
        let items = ResizeArray<obj>()
        items.Add(items :> obj)

        let contract =
            Contract<ResizeArray<obj>>.Create(CircuitJson.createOptions (), Seq.empty)

        let issues = contract.Validate items

        Assert.Empty issues

    [<Fact>]
    let ``repeated references are validated at each path`` () =
        let shared = SharedReferenceNode()
        let root = SharedReferenceNode(Name = "root", Left = shared, Right = shared)

        let contract =
            Contract<SharedReferenceNode>.Create(CircuitJson.createOptions (), Seq.empty)

        let issues = contract.Validate root

        Assert.Equal(2, issues.Count)
        let paths = issues |> Seq.map (fun issue -> issue.Path) |> Set.ofSeq
        Assert.True((paths = Set.ofList [ "$.left.name"; "$.right.name" ]))

    [<Fact>]
    let ``agent definitions accept long lowercase tool tags`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")
        let longTag = String.replicate 128 "a"

        let tags =
            seq {
                yield longTag
                yield! Seq.init 64 (fun i -> $"tag-{i}")
            }

        let metadata = Seq.init 33 (fun i -> KeyValuePair($"key-{i}", $"value-{i}"))

        let definition =
            AgentDefinition.Create(
                "agent.test",
                "1.0.0",
                "Agent Test",
                "Do things.",
                ValueNone,
                tags,
                Seq.singleton skill,
                metadata
            )

        Assert.Equal(65, definition.ToolTags.Count)
        Assert.Contains(longTag, definition.ToolTags)
        Assert.Equal(33, definition.Metadata.Count)

    [<Fact>]
    let ``agent definitions reject tool tags with invalid characters`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")

        let ex =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent Test",
                    "Do things.",
                    ValueNone,
                    seq { "Tag.One" },
                    Seq.singleton skill,
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("toolTags", ex.ParamName)

    [<Fact>]
    let ``duplicate tool tags are still rejected`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")

        let ex =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent Test",
                    "Do things.",
                    ValueNone,
                    seq {
                        "tag.one"
                        "tag.one"
                    },
                    Seq.singleton skill,
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("toolTags", ex.ParamName)

    [<Fact>]
    let ``agent definitions accept metadata keys up to 64 characters and values up to 256 characters`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")
        let key64 = String.replicate 64 "k"
        let value256 = String.replicate 256 "v"

        let definition =
            AgentDefinition.Create(
                "agent.test",
                "1.0.0",
                "Agent Test",
                "Do things.",
                ValueNone,
                Seq.empty,
                Seq.singleton skill,
                seq { KeyValuePair(key64, value256) }
            )

        Assert.Equal(value256, definition.Metadata[key64])

    [<Fact>]
    let ``agent definitions reject metadata keys or values beyond the documented limits`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")
        let key65 = String.replicate 65 "k"
        let value257 = String.replicate 257 "v"

        let keyEx =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent Test",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    Seq.singleton skill,
                    seq { KeyValuePair(key65, "value") }
                )
                |> ignore)

        Assert.Equal("metadata", keyEx.ParamName)

        let valueEx =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent Test",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    Seq.singleton skill,
                    seq { KeyValuePair("key", value257) }
                )
                |> ignore)

        Assert.Equal("metadata", valueEx.ParamName)

    [<Fact>]
    let ``duplicate skill references are still rejected`` () =
        let skill = SkillReference.Create("skill.one", "1.0.0")

        let ex =
            Assert.Throws<ArgumentException>(fun () ->
                AgentDefinition.Create(
                    "agent.test",
                    "1.0.0",
                    "Agent Test",
                    "Do things.",
                    ValueNone,
                    Seq.empty,
                    seq {
                        skill
                        skill
                    },
                    Seq.empty
                )
                |> ignore)

        Assert.Equal("skills", ex.ParamName)

    [<Fact>]
    let ``fresh CircuitJson options share the schema cache`` () =
        let contract1 =
            Contract<ResolverInput>.Create(CircuitJson.createOptions (), Seq.empty)

        let contract2 =
            Contract<ResolverInput>.Create(CircuitJson.createOptions (), Seq.empty)

        Assert.Same(contract1.Schema, contract2.Schema)

    [<Fact>]
    let ``contracts ignore null validator outputs and reject null validator entries`` () =
        let nullReturningValidator =
            CallbackValidator<ResolverInput>(fun _ -> null) :> IContractValidator<ResolverInput>

        let issueReturningValidator =
            CallbackValidator<ResolverInput>(fun _ ->
                [| { Path = "$.name"
                     Code = "custom"
                     Message = "Name is invalid." } |]
                :> IReadOnlyList<ValidationIssue>)
            :> IContractValidator<ResolverInput>

        let contract =
            Contract<ResolverInput>
                .Create(
                    CircuitJson.createOptions (),
                    seq {
                        nullReturningValidator
                        issueReturningValidator
                    }
                )

        let issues = contract.Validate(ResolverInput())

        Assert.Single issues |> ignore
        Assert.Equal("custom", issues[0].Code)
        Assert.Equal("$.name", issues[0].Path)

        let nullEntry =
            Assert.Throws<ArgumentException>(fun () ->
                Contract<ResolverInput>
                    .Create(
                        CircuitJson.createOptions (),
                        seq {
                            issueReturningValidator
                            Unchecked.defaultof<IContractValidator<ResolverInput>>
                        }
                    )
                |> ignore)

        Assert.Equal("validators", nullEntry.ParamName)

    [<Fact>]
    let ``contract validators expose a read-only wrapper`` () =
        let contract =
            Contract<CacheProbe<int>>.Create(CircuitJson.createOptions (), Seq.empty)

        let validators = contract.Validators

        Assert.Equal(1, validators.Count)
        Assert.False(box validators :? System.Array)

        let asList = box validators :?> System.Collections.IList

        Assert.Throws<NotSupportedException>(fun () -> asList.Add(null) |> ignore)

    [<Fact>]
    let ``schema cache respects type info resolver changes`` () =
        let defaultOptions = CircuitJson.createOptions ()
        let defaultContract = Contract<ResolverInput>.Create(defaultOptions, Seq.empty)
        let defaultSchema = defaultContract.Schema.RootElement
        Assert.False(defaultSchema.TryGetProperty("required") |> fst)

        let resolver = DefaultJsonTypeInfoResolver()

        resolver.Modifiers.Add(
            Action<JsonTypeInfo>(fun typeInfo ->
                if typeInfo.Type = typeof<ResolverInput> then
                    for propertyInfo in typeInfo.Properties do
                        if String.Equals(propertyInfo.Name, "name", StringComparison.OrdinalIgnoreCase) then
                            propertyInfo.IsRequired <- true)
        )

        let customOptions = CircuitJson.createOptions ()
        customOptions.TypeInfoResolver <- resolver
        let customContract = Contract<ResolverInput>.Create(customOptions, Seq.empty)
        Assert.NotSame(defaultContract.Schema, customContract.Schema)
        let required = customContract.Schema.RootElement.GetProperty("required")
        let requiredNames = required.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq

        Assert.Contains("name", requiredNames)

    [<Fact>]
    let ``resolver mutation changes the generated schema instead of reusing a stale cache entry`` () =
        let resolver = MutableResolver()

        let options1 = CircuitJson.createOptions ()
        options1.TypeInfoResolver <- resolver
        let before = Contract<ResolverInput>.Create(options1, Seq.empty)
        Assert.False(before.Schema.RootElement.TryGetProperty("required") |> fst)

        resolver.RequireName <- true

        let options2 = CircuitJson.createOptions ()
        options2.TypeInfoResolver <- resolver
        let after = Contract<ResolverInput>.Create(options2, Seq.empty)
        Assert.NotSame(before.Schema, after.Schema)

        let required = after.Schema.RootElement.GetProperty("required")
        let requiredNames = required.EnumerateArray() |> Seq.map _.GetString() |> Set.ofSeq
        Assert.Contains("name", requiredNames)

    [<Fact>]
    let ``schema cache remains bounded across many distinct contract types`` () =
        let first = createCacheProbeContract<int> ()

        createCacheProbeContract<string> () |> ignore
        createCacheProbeContract<bool> () |> ignore
        createCacheProbeContract<decimal> () |> ignore
        createCacheProbeContract<float> () |> ignore
        createCacheProbeContract<Guid> () |> ignore
        createCacheProbeContract<DateTime> () |> ignore
        createCacheProbeContract<DateTimeOffset> () |> ignore
        createCacheProbeContract<TimeSpan> () |> ignore
        createCacheProbeContract<Uri> () |> ignore
        createCacheProbeContract<byte> () |> ignore
        createCacheProbeContract<int64> () |> ignore
        createCacheProbeContract<uint32> () |> ignore
        createCacheProbeContract<int option> () |> ignore
        createCacheProbeContract<string list> () |> ignore
        createCacheProbeContract<int[]> () |> ignore
        createCacheProbeContract<ResizeArray<int>> () |> ignore

        let second = createCacheProbeContract<int> ()

        Assert.NotSame(first.Schema, second.Schema)
        Assert.Equal<string>(first.Schema.ToJsonString(), second.Schema.ToJsonString())

    [<Fact>]
    let ``semantic version parse rejects overflowing numeric components`` () =
        let ex =
            Assert.Throws<ArgumentException>(fun () -> SemanticVersion.Parse("2147483648.0.0") |> ignore)

        Assert.Equal("value", ex.ParamName)

    [<Fact>]
    let ``semantic version tryparse rejects overflowing numeric components`` () =
        let mutable result = SemanticVersion.Parse("1.2.3")

        let ok = SemanticVersion.TryParse("2147483648.0.0", &result)

        Assert.False(ok)

    [<Fact>]
    let ``signature rejects a null input before the runtime is called`` () =
        let signature =
            Signature<SignatureInput, SignatureOutput>
                .Create(
                    "ticket.triage",
                    "1.0.0",
                    "Ticket triage",
                    "Route the ticket.",
                    CircuitJson.createOptions (),
                    Seq.empty,
                    Seq.empty
                )

        let issues = signature.Input.Validate null

        Assert.Single issues |> ignore
        Assert.Equal("$", issues[0].Path)
        Assert.Equal("required", issues[0].Code)

    [<Fact>]
    let ``tool resolution returns no tools for an empty resolver list`` () =
        let context =
            ToolResolutionContext(RunId.New(), ValueNone, ValueNone, NullServiceProvider())

        let tools =
            (ToolResolution.resolveAllAsync
                (Array.empty<IToolResolver> :> IReadOnlyList<IToolResolver>)
                context
                CancellationToken.None)
                .Result

        Assert.Empty tools

    [<Fact>]
    let ``tool resolution rejects duplicate name and major version identities case insensitively`` () =
        let first =
            createTestTool "tool.read" "1.0.0" ApprovalMode.Never ValueNone
            |> ResolvedTool.Create

        let second =
            createTestTool "tool.read" "1.2.0" ApprovalMode.Never ValueNone
            |> ResolvedTool.Create

        let resolver = StaticToolResolver([| first; second |]) :> IToolResolver

        let context =
            ToolResolutionContext(RunId.New(), ValueNone, ValueNone, NullServiceProvider())

        let ex =
            Assert.Throws<AggregateException>(fun () ->
                (ToolResolution.resolveAllAsync
                    ([| resolver |] :> IReadOnlyList<IToolResolver>)
                    context
                    CancellationToken.None)
                    .Result
                |> ignore)

        Assert.Contains("Duplicate tool identity 'tool.read' with major version '1'", ex.InnerException.Message)

    [<Fact>]
    let ``static tool resolver snapshots the supplied tool list`` () =
        let first =
            createTestTool "tool.read" "1.0.0" ApprovalMode.Never ValueNone
            |> ResolvedTool.Create

        let second =
            createTestTool "tool.write" "1.0.0" ApprovalMode.Never ValueNone
            |> ResolvedTool.Create

        let mutable source = [| first |]
        let resolver = StaticToolResolver(source) :> IToolResolver
        source <- [| second |]

        let context =
            ToolResolutionContext(RunId.New(), ValueNone, ValueNone, NullServiceProvider())

        let tools = resolver.ResolveAsync(context, CancellationToken.None).Result

        Assert.Single tools |> ignore
        Assert.Equal("tool.read", tools[0].Name.Value)

    [<Fact>]
    let ``serialization fingerprints distinguish stable and unstable option graphs`` () =
        let stable =
            SerializationPolicy.tryGetSemanticFingerprint (CircuitJson.createOptions ())

        Assert.True(stable.IsSome)

        let customNaming = CircuitJson.createOptions ()
        customNaming.PropertyNamingPolicy <- PrefixNamingPolicy("x_")
        Assert.Equal(ValueNone, SerializationPolicy.tryGetSemanticFingerprint customNaming)

        let relaxedEncoder = CircuitJson.createOptions ()
        relaxedEncoder.Encoder <- System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping

        Assert.True(
            SerializationPolicy.tryGetSemanticFingerprint relaxedEncoder
            |> ValueOption.isSome
        )

        let customEncoder = CircuitJson.createOptions ()

        customEncoder.Encoder <-
            System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.BasicLatin)

        Assert.Equal(ValueNone, SerializationPolicy.tryGetSemanticFingerprint customEncoder)

        let customEnumConverter = CircuitJson.createOptions ()
        customEnumConverter.Converters.Clear()
        customEnumConverter.Converters.Add(JsonStringEnumConverter(PrefixNamingPolicy("enum_")))
        Assert.Equal(ValueNone, SerializationPolicy.tryGetSemanticFingerprint customEnumConverter)

    [<Fact>]
    let ``serialization fingerprints support stable built-in policy shapes and reject derived converters`` () =
        let nullOptions =
            Assert.Throws<ArgumentNullException>(fun () -> SerializationPolicy.tryGetSemanticFingerprint null |> ignore)

        Assert.Equal("options", nullOptions.ParamName)

        let noNamingPolicy = CircuitJson.createOptions ()
        noNamingPolicy.PropertyNamingPolicy <- null

        Assert.True(
            SerializationPolicy.tryGetSemanticFingerprint noNamingPolicy
            |> ValueOption.isSome
        )

        let noResolver = CircuitJson.createOptions ()
        noResolver.TypeInfoResolver <- null
        Assert.Equal(ValueNone, SerializationPolicy.tryGetSemanticFingerprint noResolver)

        let resolverChain = CircuitJson.createOptions ()
        resolverChain.TypeInfoResolver <- DefaultJsonTypeInfoResolver()
        resolverChain.TypeInfoResolverChain.Add(DefaultJsonTypeInfoResolver())

        Assert.True(
            SerializationPolicy.tryGetSemanticFingerprint resolverChain
            |> ValueOption.isSome
        )

        let derivedConverter = CircuitJson.createOptions ()
        derivedConverter.Converters.Clear()
        derivedConverter.Converters.Add(DerivedEnumConverter())
        Assert.Equal(ValueNone, SerializationPolicy.tryGetSemanticFingerprint derivedConverter)

    [<Fact>]
    let ``contracts validate constructor arguments and non object schema roots`` () =
        let nullSchema =
            Assert.Throws<ArgumentNullException>(fun () -> SchemaDocument(null) |> ignore)

        Assert.Equal("node", nullSchema.ParamName)

        let nullJsonOptions =
            Assert.Throws<ArgumentNullException>(fun () -> Contract<int>.Create(null, Seq.empty) |> ignore)

        Assert.Equal("jsonOptions", nullJsonOptions.ParamName)

        let nullValidators =
            Assert.Throws<ArgumentNullException>(fun () ->
                Contract<int>.Create(CircuitJson.createOptions (), null) |> ignore)

        Assert.Equal("validators", nullValidators.ParamName)

        let arrayContract = Contract<int[]>.Create(CircuitJson.createOptions (), Seq.empty)
        let issues = arrayContract.Validate [| 1; 2 |]
        Assert.Empty issues
