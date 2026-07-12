namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Circuit.Core
open Xunit

module SkillsTests =
    let private createTempSkillRoot () =
        let root =
            Path.Combine(Path.GetTempPath(), $"circuit-core-skill-{Guid.NewGuid():N}")

        Directory.CreateDirectory(root) |> ignore

        File.WriteAllText(
            Path.Combine(root, "SKILL.md"),
            "---\nname: test-skill\ndescription: Test skill\n---\nUse the skill.\n"
        )

        root

    let private deleteDirectory (path: string) =
        if Directory.Exists path then
            Directory.Delete(path, true)

    [<Fact>]
    let ``file skill sources canonicalize existing roots that contain SKILL md`` () =
        let parent =
            Path.Combine(Path.GetTempPath(), $"circuit-core-parent-{Guid.NewGuid():N}")

        Directory.CreateDirectory(parent) |> ignore

        let skillRoot = Path.Combine(parent, "skills", "test-skill")
        Directory.CreateDirectory(skillRoot) |> ignore
        File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), "---\nname: test-skill\ndescription: Test skill\n---\n")

        try
            let source =
                SkillSource.CreateFile(Path.Combine(parent, "skills", ".", "test-skill", "..", "test-skill"))

            Assert.Equal(SkillSourceKind.File, source.Kind)
            Assert.Single(source.FileRoots) |> ignore
            Assert.Equal(Path.GetFullPath(skillRoot), source.FileRoots[0])
        finally
            deleteDirectory parent

    [<Fact>]
    let ``file skill sources collapse symlink root aliases to one filesystem identity`` () =
        let parent =
            Path.Combine(Path.GetTempPath(), $"circuit-core-alias-parent-{Guid.NewGuid():N}")

        let actualRoot = Path.Combine(parent, "actual-skill")
        let aliasRoot = Path.Combine(parent, "alias-skill")
        Directory.CreateDirectory(actualRoot) |> ignore

        File.WriteAllText(
            Path.Combine(actualRoot, "SKILL.md"),
            "---\nname: alias-skill\ndescription: Alias skill\n---\n"
        )

        Directory.CreateSymbolicLink(aliasRoot, actualRoot) |> ignore

        try
            let source = SkillSource.CreateFile(aliasRoot)
            Assert.Equal(Path.GetFullPath(actualRoot), source.FileRoots[0])

            let ex =
                Assert.Throws<ArgumentException>(fun () ->
                    SkillSource.CreateFile(
                        seq {
                            actualRoot
                            aliasRoot
                        }
                    )
                    |> ignore)

            Assert.Equal("fileRoots", ex.ParamName)
        finally
            deleteDirectory parent

    [<Fact>]
    let ``file skill sources dedupe case aliases on windows`` () =
        if not (OperatingSystem.IsWindows()) then
            ()
        else
            let parent =
                Path.Combine(Path.GetTempPath(), $"circuit-core-case-parent-{Guid.NewGuid():N}")

            let skillRoot = Path.Combine(parent, "CaseSkill")
            Directory.CreateDirectory(skillRoot) |> ignore

            File.WriteAllText(
                Path.Combine(skillRoot, "SKILL.md"),
                "---\nname: case-skill\ndescription: Case skill\n---\n"
            )

            let caseAlias = Path.Combine(parent.ToUpperInvariant(), "caseskill")

            try
                let ex =
                    Assert.Throws<ArgumentException>(fun () ->
                        SkillSource.CreateFile(
                            seq {
                                skillRoot
                                caseAlias
                            }
                        )
                        |> ignore)

                Assert.Equal("fileRoots", ex.ParamName)
            finally
                deleteDirectory parent

    [<Fact>]
    let ``file skill sources reject roots without SKILL md`` () =
        let root =
            Path.Combine(Path.GetTempPath(), $"circuit-core-empty-skill-{Guid.NewGuid():N}")

        Directory.CreateDirectory(root) |> ignore

        try
            let ex =
                Assert.Throws<ArgumentException>(fun () -> SkillSource.CreateFile(root) |> ignore)

            Assert.Equal("fileRoots", ex.ParamName)
        finally
            deleteDirectory root

    [<Fact>]
    let ``file skill sources reject symlinked SKILL md targets that escape the root`` () =
        let root =
            Path.Combine(Path.GetTempPath(), $"circuit-core-unsafe-skill-{Guid.NewGuid():N}")

        let outsideRoot =
            Path.Combine(Path.GetTempPath(), $"circuit-core-unsafe-outside-{Guid.NewGuid():N}")

        Directory.CreateDirectory(root) |> ignore
        Directory.CreateDirectory(outsideRoot) |> ignore

        let outsideSkill = Path.Combine(outsideRoot, "outside-skill.md")
        File.WriteAllText(outsideSkill, "---\nname: outside\ndescription: Outside\n---\n")
        File.CreateSymbolicLink(Path.Combine(root, "SKILL.md"), outsideSkill) |> ignore

        try
            let ex =
                Assert.Throws<ArgumentException>(fun () -> SkillSource.CreateFile(root) |> ignore)

            Assert.Equal("fileRoots", ex.ParamName)
        finally
            deleteDirectory root
            deleteDirectory outsideRoot

    [<Fact>]
    let ``file skill sources preserve in-root symlinked SKILL md targets`` () =
        let root =
            Path.Combine(Path.GetTempPath(), $"circuit-core-safe-skill-{Guid.NewGuid():N}")

        Directory.CreateDirectory(root) |> ignore

        let actualSkill = Path.Combine(root, "skill-body.md")
        File.WriteAllText(actualSkill, "---\nname: safe\ndescription: Safe\n---\n")
        File.CreateSymbolicLink(Path.Combine(root, "SKILL.md"), actualSkill) |> ignore

        try
            let source = SkillSource.CreateFile(root)
            Assert.Equal(Path.GetFullPath(root), source.FileRoots[0])
        finally
            deleteDirectory root

    [<Fact>]
    let ``resolved skill properties are marked to stay out of public json serialization`` () =
        let propertyInfo = typeof<ResolvedSkill>.GetProperty("Properties")
        let attribute = propertyInfo.GetCustomAttributes(typeof<JsonIgnoreAttribute>, true)
        Assert.Single attribute |> ignore

    [<Fact>]
    let ``skill resolution rejects duplicate identities`` () =
        let skillRoot1 = createTempSkillRoot ()
        let skillRoot2 = createTempSkillRoot ()

        try
            let reference1 =
                SkillReference.Create("skill.test", "1.0.0", "First", SkillSource.CreateFile(skillRoot1))

            let reference2 =
                SkillReference.Create("skill.test", "1.0.0", "Second", SkillSource.CreateFile(skillRoot2))

            let resolver1 =
                StaticSkillResolver([| ResolvedSkill.Create(reference1) |]) :> ISkillResolver

            let resolver2 =
                StaticSkillResolver([| ResolvedSkill.Create(reference2) |]) :> ISkillResolver

            let context =
                SkillResolutionContext(RunId.New(), ValueNone, ValueNone, RunOptions.Default.Services)

            let ex =
                Assert.Throws<InvalidOperationException>(fun () ->
                    SkillResolution.resolveAllAsync [| resolver1; resolver2 |] context CancellationToken.None
                    |> fun task -> task.GetAwaiter().GetResult() |> ignore)

            Assert.Contains("Duplicate skill identity 'skill.test@1.0.0' was resolved.", ex.Message)
        finally
            deleteDirectory skillRoot1
            deleteDirectory skillRoot2

    [<Fact>]
    let ``inline and custom skill sources preserve resources scripts and instructions`` () =
        let glossary =
            SkillResource.Create("glossary", box "vip = high-touch customer", "Glossary")

        let script =
            SkillScriptDescriptor.Create(
                "normalize-contact",
                "Normalize a contact.",
                seq { KeyValuePair("engine", "pwsh") }
            )

        let inlineSource =
            SkillSource.CreateInline("Use the inline skill.", [ glossary ], [ script ])

        let customSource = SkillSource.CreateCustom()

        Assert.Equal(SkillSourceKind.Inline, inlineSource.Kind)
        Assert.Equal("Use the inline skill.", inlineSource.Instructions)
        Assert.Single(inlineSource.Resources) |> ignore
        Assert.Single(inlineSource.Scripts) |> ignore
        Assert.Equal(SkillSourceKind.Custom, customSource.Kind)
        Assert.Empty(customSource.FileRoots)
        Assert.Empty(customSource.Resources)
        Assert.Empty(customSource.Scripts)

        let blankInstructions =
            Assert.Throws<ArgumentException>(fun () -> SkillSource.CreateInline(" ") |> ignore)

        Assert.Equal("instructions", blankInstructions.ParamName)

        let duplicateResources =
            Assert.Throws<ArgumentException>(fun () ->
                SkillSource.CreateInline("Use the inline skill.", [ glossary; glossary ], Seq.empty)
                |> ignore)

        Assert.Equal("resources", duplicateResources.ParamName)

        let duplicateScripts =
            Assert.Throws<ArgumentException>(fun () ->
                SkillSource.CreateInline("Use the inline skill.", Seq.empty, [ script; script ])
                |> ignore)

        Assert.Equal("scripts", duplicateScripts.ParamName)

        let noFileRoots =
            Assert.Throws<ArgumentException>(fun () -> SkillSource.CreateFile(Seq.empty<string>) |> ignore)

        Assert.Equal("fileRoots", noFileRoots.ParamName)

    [<Fact>]
    let ``skill resources validate static and dynamic configuration and read values`` () =
        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let context =
            SkillResourceContext(RunId.New(), ValueSome("tenant-1"), ValueSome("user-1"), services)

        let staticResource = SkillResource.Create("guide.txt", box "hello", "Guide")
        let mutable dynamicCalls = 0

        let dynamicResource =
            SkillResource.CreateDynamic(
                "archive.bin",
                Func<SkillResourceContext, CancellationToken, Task<obj>>(fun innerContext ct ->
                    dynamicCalls <- dynamicCalls + 1
                    Assert.Equal(context.RunId, innerContext.RunId)
                    Assert.False(ct.IsCancellationRequested)
                    Task.FromResult(box [| 1uy; 2uy |])),
                "Archive"
            )

        Assert.False(staticResource.IsDynamic)
        Assert.Equal("hello", staticResource.ReadAsync(context, CancellationToken.None).Result :?> string)
        Assert.True(dynamicResource.IsDynamic)

        Assert.Equal<byte[]>(
            [| 1uy; 2uy |],
            dynamicResource.ReadAsync(context, CancellationToken.None).Result :?> byte[]
        )

        Assert.Equal(1, dynamicCalls)

        let bothConfigured =
            Assert.Throws<ArgumentException>(fun () ->
                SkillResource(
                    "guide.txt",
                    "Guide",
                    ValueSome(box "value"),
                    ValueSome(
                        Func<SkillResourceContext, CancellationToken, Task<obj>>(fun _ _ ->
                            Task.FromResult(box "other"))
                    )
                )
                |> ignore)

        Assert.Equal("dynamicReader", bothConfigured.ParamName)

        let neitherConfigured =
            Assert.Throws<ArgumentException>(fun () ->
                SkillResource("guide.txt", "Guide", ValueNone, ValueNone) |> ignore)

        Assert.Equal("staticValue", neitherConfigured.ParamName)

        let nullStatic =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillResource("guide.txt", "Guide", ValueSome null, ValueNone) |> ignore)

        Assert.Equal("staticValue", nullStatic.ParamName)

        let nullDynamic =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillResource("guide.txt", "Guide", ValueNone, ValueSome null) |> ignore)

        Assert.Equal("dynamicReader", nullDynamic.ParamName)

    [<Fact>]
    let ``skill scripts references and resolved properties validate metadata and types`` () =
        let script =
            SkillScriptDescriptor.Create(
                "normalize-contact",
                "Normalize a contact.",
                seq { KeyValuePair("engine", "pwsh") }
            )

        Assert.Equal("normalize-contact", script.Name)
        Assert.Equal("pwsh", script.Metadata["engine"])

        let duplicateMetadata =
            Assert.Throws<ArgumentException>(fun () ->
                SkillScriptDescriptor.Create(
                    "normalize-contact",
                    "Normalize a contact.",
                    seq {
                        KeyValuePair("engine", "pwsh")
                        KeyValuePair("engine", "bash")
                    }
                )
                |> ignore)

        Assert.Equal("metadata", duplicateMetadata.ParamName)

        let source = SkillSource.CreateCustom()

        let reference =
            SkillReference.Create("skill.inline", "1.0.0", null, source, seq { KeyValuePair("team", "core") })

        let resolved =
            ResolvedSkill.Create(
                reference,
                seq {
                    KeyValuePair("count", box 3)
                    KeyValuePair("label", box "ok")
                }
            )

        Assert.Equal(String.Empty, reference.Description)
        Assert.Equal(ValueSome 3, resolved.TryGetProperty<int>("count"))
        Assert.Equal(ValueSome "ok", resolved.TryGetProperty<string>("label"))
        Assert.Equal(ValueNone, resolved.TryGetProperty<int>("label"))

        let duplicateProperties =
            Assert.Throws<ArgumentException>(fun () ->
                ResolvedSkill.Create(
                    reference,
                    seq {
                        KeyValuePair("count", box 1)
                        KeyValuePair("count", box 2)
                    }
                )
                |> ignore)

        Assert.Equal("properties", duplicateProperties.ParamName)

        let nullMetadata =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillReference.Create("skill.inline", "1.0.0", "desc", source, null) |> ignore)

        Assert.Equal("metadata", nullMetadata.ParamName)

    [<Fact>]
    let ``skill contexts requests and delegate resolvers validate constructor arguments`` () =
        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let runId = RunId.New()
        let source = SkillSource.CreateInline("Use the inline skill.")
        let reference = SkillReference.Create("skill.inline", "1.0.0", "Inline", source)
        let script = SkillScriptDescriptor.Create("normalize-contact")

        let context =
            SkillResolutionContext(runId, ValueSome("tenant-1"), ValueSome("user-1"), services)

        let request =
            SkillScriptRequest(
                runId,
                ValueSome("tenant-1"),
                ValueSome("user-1"),
                services,
                reference,
                script,
                Nullable(),
                ValueNone,
                ValueNone
            )

        Assert.Equal(reference, request.Skill)
        Assert.Equal(script, request.Script)
        Assert.Equal(runId, context.RunId)

        let nullContextServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillResolutionContext(runId, ValueNone, ValueNone, null) |> ignore)

        Assert.Equal("services", nullContextServices.ParamName)

        let nullRequestServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillScriptRequest(
                    runId,
                    ValueNone,
                    ValueNone,
                    null,
                    reference,
                    script,
                    Nullable(),
                    ValueNone,
                    ValueNone
                )
                |> ignore)

        Assert.Equal("services", nullRequestServices.ParamName)

        let nullSkill =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillScriptRequest(
                    runId,
                    ValueNone,
                    ValueNone,
                    services,
                    Unchecked.defaultof<SkillReference>,
                    script,
                    Nullable(),
                    ValueNone,
                    ValueNone
                )
                |> ignore)

        Assert.Equal("skill", nullSkill.ParamName)

        let nullScript =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillScriptRequest(
                    runId,
                    ValueNone,
                    ValueNone,
                    services,
                    reference,
                    Unchecked.defaultof<SkillScriptDescriptor>,
                    Nullable(),
                    ValueNone,
                    ValueNone
                )
                |> ignore)

        Assert.Equal("script", nullScript.ParamName)

        let mutable invoked = false

        let resolver =
            DelegateSkillResolver(
                Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>
                    (fun innerContext ct ->
                        invoked <- true
                        Assert.Equal(runId, innerContext.RunId)
                        Assert.False(ct.IsCancellationRequested)

                        ValueTask<IReadOnlyList<ResolvedSkill>>(
                            [| ResolvedSkill.Create(reference) |] :> IReadOnlyList<ResolvedSkill>
                        ))
            )
            :> ISkillResolver

        let resolved = resolver.ResolveAsync(context, CancellationToken.None).Result

        Assert.True(invoked)
        Assert.Single(resolved) |> ignore

        let nullResolver =
            Assert.Throws<ArgumentNullException>(fun () -> DelegateSkillResolver(null) |> ignore)

        Assert.Equal("resolver", nullResolver.ParamName)

        let nullSkills =
            Assert.Throws<ArgumentNullException>(fun () -> StaticSkillResolver(null) |> ignore)

        Assert.Equal("skills", nullSkills.ParamName)

    [<Fact>]
    let ``skill resolution surfaces canceled and faulting resolver tasks`` () =
        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let context = SkillResolutionContext(RunId.New(), ValueNone, ValueNone, services)
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let canceledResolver =
            DelegateSkillResolver(
                Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedSkill>>(
                        Task.FromCanceled<IReadOnlyList<ResolvedSkill>>(cancelled.Token)
                    ))
            )
            :> ISkillResolver

        let canceled =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| canceledResolver |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.IsType<TaskCanceledException>(canceled.InnerException) |> ignore

        let failingResolver =
            DelegateSkillResolver(
                Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedSkill>>(
                        Task.FromException<IReadOnlyList<ResolvedSkill>>(InvalidOperationException("resolver failed"))
                    ))
            )
            :> ISkillResolver

        let ex =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| failingResolver |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("resolver failed", ex.InnerException.Message)

    [<Fact>]
    let ``skill resolution handles empty and invalid resolver outputs`` () =
        let services =
            { new IServiceProvider with
                member _.GetService(_serviceType) = null }

        let context = SkillResolutionContext(RunId.New(), ValueNone, ValueNone, services)
        let source = SkillSource.CreateInline("Use the inline skill.")
        let reference = SkillReference.Create("skill.valid", "1.0.0", "Valid", source)

        let validResolver =
            StaticSkillResolver([| ResolvedSkill.Create(reference) |]) :> ISkillResolver

        let empty =
            SkillResolution.resolveAllAsync [||] context CancellationToken.None |> _.Result

        Assert.Empty(empty)

        let nullResolvers =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync null context CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("resolvers", (nullResolvers.InnerException :?> ArgumentNullException).ParamName)

        let nullContext =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| validResolver |] :> IReadOnlyList<ISkillResolver>)
                    Unchecked.defaultof<SkillResolutionContext>
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Equal("context", (nullContext.InnerException :?> ArgumentNullException).ParamName)

        let nullResolverEntry =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| Unchecked.defaultof<ISkillResolver> |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot contain null entries", nullResolverEntry.InnerException.Message)

        let nullSkillListResolver =
            DelegateSkillResolver(
                Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedSkill>>(Unchecked.defaultof<IReadOnlyList<ResolvedSkill>>))
            )
            :> ISkillResolver

        let nullSkillList =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| nullSkillListResolver |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot return null skill lists", nullSkillList.InnerException.Message)

        let nullSkillEntryResolver =
            DelegateSkillResolver(
                Func<SkillResolutionContext, CancellationToken, ValueTask<IReadOnlyList<ResolvedSkill>>>(fun _ _ ->
                    ValueTask<IReadOnlyList<ResolvedSkill>>(
                        [| Unchecked.defaultof<ResolvedSkill> |] :> IReadOnlyList<ResolvedSkill>
                    ))
            )
            :> ISkillResolver

        let nullSkillEntry =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| nullSkillEntryResolver |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("cannot return null skill entries", nullSkillEntry.InnerException.Message)

        let malformedReference =
            SkillReference(
                DefinitionId.Create("skill.malformed"),
                SemanticVersion.Parse("1.0.0"),
                null,
                source,
                reference.Metadata
            )

        let malformedResolver =
            StaticSkillResolver([| ResolvedSkill.Create(malformedReference) |]) :> ISkillResolver

        let malformed =
            Assert.Throws<AggregateException>(fun () ->
                SkillResolution.resolveAllAsync
                    ([| malformedResolver |] :> IReadOnlyList<ISkillResolver>)
                    context
                    CancellationToken.None
                |> _.Result
                |> ignore)

        Assert.Contains("Resolved skill descriptions cannot be null.", malformed.InnerException.Message)

    [<Fact>]
    let ``skill path security resolves in root paths and rejects traversal`` () =
        let root = createTempSkillRoot ()
        let refsDir = Path.Combine(root, "refs")
        let guidePath = Path.Combine(refsDir, "guide.md")
        Directory.CreateDirectory(refsDir) |> ignore
        File.WriteAllText(guidePath, "guide")

        try
            let resolvedFile =
                SkillPathSecurity.resolveExistingRelativeFileWithinRoot root "refs/guide.md"

            let resolvedDirectory =
                SkillPathSecurity.resolveExistingRelativeDirectoryWithinRoot root "refs"

            Assert.Equal(Path.GetFullPath(guidePath), resolvedFile)
            Assert.Equal(Path.GetFullPath(refsDir), resolvedDirectory)

            let traversal =
                Assert.Throws<InvalidOperationException>(fun () ->
                    SkillPathSecurity.resolveExistingRelativeFileWithinRoot root "../outside.md"
                    |> ignore)

            Assert.Contains("could not be safely resolved", traversal.Message)

            let blankRoot =
                Assert.Throws<ArgumentException>(fun () -> SkillPathSecurity.validateSkillRootPath (" ") |> ignore)

            Assert.Equal("fileRoots", blankRoot.ParamName)
        finally
            deleteDirectory root

    [<Fact>]
    let ``skill sources reject null resource and script entries`` () =
        let nullResource =
            Assert.Throws<ArgumentException>(fun () ->
                SkillSource.CreateInline("Use the inline skill.", [ Unchecked.defaultof<SkillResource> ], Seq.empty)
                |> ignore)

        Assert.Equal("resources", nullResource.ParamName)

        let nullScript =
            Assert.Throws<ArgumentException>(fun () ->
                SkillSource.CreateInline(
                    "Use the inline skill.",
                    Seq.empty,
                    [ Unchecked.defaultof<SkillScriptDescriptor> ]
                )
                |> ignore)

        Assert.Equal("scripts", nullScript.ParamName)

    [<Fact>]
    let ``skill constructors reject invalid names metadata properties and null collections`` () =
        let validSource = SkillSource.CreateInline("Use the inline skill.")

        let validReference =
            SkillReference.Create("skill.valid", "1.0.0", "Valid", validSource)

        let invalidResourceName =
            Assert.Throws<ArgumentException>(fun () -> SkillResource.Create("UPPER", box "value") |> ignore)

        Assert.Equal("name", invalidResourceName.ParamName)

        let invalidScriptName =
            Assert.Throws<ArgumentException>(fun () -> SkillScriptDescriptor.Create("UPPER") |> ignore)

        Assert.Equal("name", invalidScriptName.ParamName)

        let nullScriptMetadata =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillScriptDescriptor(
                    "normalize-contact",
                    "desc",
                    Unchecked.defaultof<IReadOnlyDictionary<string, string>>
                )
                |> ignore)

        Assert.Equal("metadata", nullScriptMetadata.ParamName)

        let whitespaceDescription =
            Assert.Throws<ArgumentException>(fun () -> SkillResource.Create("guide.txt", box "value", " ") |> ignore)

        Assert.Equal("description", whitespaceDescription.ParamName)

        let nullResourceContextServices =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillResourceContext(RunId.New(), ValueNone, ValueNone, null) |> ignore)

        Assert.Equal("services", nullResourceContextServices.ParamName)

        let metadataKeyTooLong =
            Assert.Throws<ArgumentException>(fun () ->
                SkillScriptDescriptor.Create(
                    "normalize-contact",
                    "desc",
                    seq { KeyValuePair(String.replicate 65 "k", "value") }
                )
                |> ignore)

        Assert.Equal("metadata", metadataKeyTooLong.ParamName)

        let metadataNullValue =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillScriptDescriptor.Create("normalize-contact", "desc", seq { KeyValuePair("key", null) })
                |> ignore)

        Assert.Equal("metadata", metadataNullValue.ParamName)

        let propertyBlankKey =
            Assert.Throws<ArgumentException>(fun () ->
                ResolvedSkill.Create(validReference, seq { KeyValuePair(" ", box 1) }) |> ignore)

        Assert.Equal("properties", propertyBlankKey.ParamName)

        let propertyLongKey =
            Assert.Throws<ArgumentException>(fun () ->
                ResolvedSkill.Create(validReference, seq { KeyValuePair(String.replicate 129 "k", box 1) })
                |> ignore)

        Assert.Equal("properties", propertyLongKey.ParamName)

        let propertyNullValue =
            Assert.Throws<ArgumentNullException>(fun () ->
                ResolvedSkill.Create(validReference, seq { KeyValuePair("count", null) })
                |> ignore)

        Assert.Equal("properties", propertyNullValue.ParamName)

        let nullFileRoots =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillSource(SkillSourceKind.Custom, null, String.Empty, validSource.Resources, validSource.Scripts)
                |> ignore)

        Assert.Equal("fileRoots", nullFileRoots.ParamName)

        let nullResources =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillSource(SkillSourceKind.Custom, validSource.FileRoots, String.Empty, null, validSource.Scripts)
                |> ignore)

        Assert.Equal("resources", nullResources.ParamName)

        let nullScripts =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillSource(SkillSourceKind.Custom, validSource.FileRoots, String.Empty, validSource.Resources, null)
                |> ignore)

        Assert.Equal("scripts", nullScripts.ParamName)

        let nullInlineResources =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillSource.CreateInline("Use the inline skill.", null, Seq.empty) |> ignore)

        Assert.Equal("resources", nullInlineResources.ParamName)

        let nullInlineScripts =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillSource.CreateInline("Use the inline skill.", Seq.empty, null) |> ignore)

        Assert.Equal("scripts", nullInlineScripts.ParamName)

        let nullSource =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillReference(
                    DefinitionId.Create("skill.valid"),
                    SemanticVersion.Parse("1.0.0"),
                    "desc",
                    Unchecked.defaultof<SkillSource>,
                    validReference.Metadata
                )
                |> ignore)

        Assert.Equal("source", nullSource.ParamName)

        let nullMetadata =
            Assert.Throws<ArgumentNullException>(fun () ->
                SkillReference(
                    DefinitionId.Create("skill.valid"),
                    SemanticVersion.Parse("1.0.0"),
                    "desc",
                    validSource,
                    Unchecked.defaultof<IReadOnlyDictionary<string, string>>
                )
                |> ignore)

        Assert.Equal("metadata", nullMetadata.ParamName)

        let nullReference =
            Assert.Throws<ArgumentNullException>(fun () ->
                ResolvedSkill(
                    Unchecked.defaultof<SkillReference>,
                    Unchecked.defaultof<IReadOnlyDictionary<string, obj>>
                )
                |> ignore)

        Assert.Equal("reference", nullReference.ParamName)

        let nullProperties =
            Assert.Throws<ArgumentNullException>(fun () ->
                ResolvedSkill(validReference, Unchecked.defaultof<IReadOnlyDictionary<string, obj>>)
                |> ignore)

        Assert.Equal("properties", nullProperties.ParamName)

    [<Fact>]
    let ``skill getters expose static and dynamic resource members`` () =
        let staticResource = SkillResource.Create("guide.txt", box "hello")

        let dynamicReader =
            Func<SkillResourceContext, CancellationToken, Task<obj>>(fun _ _ -> Task.FromResult(box "world"))

        let dynamicResource = SkillResource.CreateDynamic("guide.bin", dynamicReader)

        Assert.Equal("hello", staticResource.StaticValue :?> string)
        Assert.Null(staticResource.DynamicReader)
        Assert.Null(dynamicResource.StaticValue)
        Assert.Same(dynamicReader, dynamicResource.DynamicReader)
