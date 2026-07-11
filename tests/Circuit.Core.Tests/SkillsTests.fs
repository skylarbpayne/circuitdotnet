namespace Circuit.Core.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Serialization
open System.Threading
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
