namespace Circuit.Package.Tests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text
open System.Text.RegularExpressions
open System.Xml.Linq
open Xunit

module PackageSmokeTests =
    type private TemporaryDirectory() =
        let path =
            Path.Combine(Path.GetTempPath(), $"circuit-package-tests-{Guid.NewGuid():N}")

        do Directory.CreateDirectory(path) |> ignore

        member _.Path = path

        interface IDisposable with
            member _.Dispose() =
                if Directory.Exists(path) then
                    Directory.Delete(path, true)

    type private PackageExpectation =
        { PackageId: string
          AssemblyName: string }

    type private PackageArtifact =
        { PackageId: string
          Version: string
          NupkgPath: string
          SnupkgPath: string
          AssemblyName: string }

    let private packageExpectations =
        [| { PackageId = "CircuitDotNet"
             AssemblyName = "Circuit" }
           { PackageId = "CircuitDotNet.Core"
             AssemblyName = "Circuit.Core" }
           { PackageId = "CircuitDotNet.FSharp"
             AssemblyName = "Circuit.FSharp" }
           { PackageId = "CircuitDotNet.MicrosoftAgentFramework"
             AssemblyName = "Circuit.MicrosoftAgentFramework" }
           { PackageId = "CircuitDotNet.Testing"
             AssemblyName = "Circuit.Testing" } |]

    let private projectRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))

    let private packageDirectory =
        let explicitPackageDirectory =
            Environment.GetEnvironmentVariable("PackageDirectory")

        let resolvedPackageDirectory =
            match explicitPackageDirectory with
            | null
            | "" -> Path.GetFullPath(Path.Combine(projectRoot, "artifacts", "packages"))
            | value -> Path.GetFullPath value

        if not (Directory.Exists resolvedPackageDirectory) then
            let guidance =
                if String.IsNullOrWhiteSpace explicitPackageDirectory then
                    "Run dotnet pack first or pass -p:PackageDirectory=<packed artifacts folder>."
                else
                    "Check the PackageDirectory value passed to the test run."

            failwith $"Package directory '{resolvedPackageDirectory}' does not exist. {guidance}"

        resolvedPackageDirectory

    let private nuspecNamespace =
        XNamespace.Get("http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd")

    let private semanticVersionPattern =
        Regex(
            "^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\+[0-9A-Za-z][0-9A-Za-z.-]*)?$",
            RegexOptions.CultureInvariant
        )

    let private commitPattern = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

    let private bannedEntryPattern =
        Regex(
            "(^|/)(tests?|testresults?|samples?|secrets?)(/|$)",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
        )

    let private bannedSecretFilePattern =
        Regex(
            "(\.env($|\.)|\.key$|\.pem$|\.pfx$|secrets?\.json$)",
            RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant
        )

    let private bannedPathFragmentPattern =
        Regex("(/home/|/tmp/)", RegexOptions.CultureInvariant)

    let private bannedSourceDocumentPattern =
        Regex("(/tests?/|/samples?/|/home/|/tmp/)", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let private packageSource = "https://api.nuget.org/v3/index.json"

    let private xmlValue (parent: XElement) name =
        let element = parent.Element(nuspecNamespace + name)
        if isNull element then None else Some element.Value

    let private escapeXml (value: string) = value.Replace("&", "&amp;")

    let private runProcess (workingDirectory: string) (fileName: string) (arguments: string) =
        task {
            let startInfo = new ProcessStartInfo(fileName, arguments)
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.Environment["PATH"] <- Environment.GetEnvironmentVariable("PATH")

            let dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")

            if not (String.IsNullOrWhiteSpace dotnetRoot) then
                startInfo.Environment["DOTNET_ROOT"] <- dotnetRoot

            use startedProcess =
                match Process.Start(startInfo) with
                | null -> failwith $"Failed to start process '{fileName}'."
                | started -> started

            let! stdout = startedProcess.StandardOutput.ReadToEndAsync()
            let! stderr = startedProcess.StandardError.ReadToEndAsync()
            do! startedProcess.WaitForExitAsync()

            if startedProcess.ExitCode <> 0 then
                raise (
                    Xunit.Sdk.XunitException(
                        $"Command failed: {fileName} {arguments}{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}"
                    )
                )

            return stdout + stderr
        }

    let private readTextEntries (archive: ZipArchive) =
        archive.Entries
        |> Seq.filter (fun entry ->
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        |> Seq.map (fun entry ->
            use stream = entry.Open()
            use reader = new StreamReader(stream, Encoding.UTF8, true)
            entry.FullName, reader.ReadToEnd())
        |> Seq.toArray

    let private readPackageId (packagePath: string) =
        use archive = ZipFile.OpenRead(packagePath)

        let nuspecEntry =
            Assert.Single(
                archive.Entries
                |> Seq.filter (fun entry -> entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            )

        use nuspecStream = nuspecEntry.Open()
        let document = XDocument.Load(nuspecStream)
        document.Root.Element(nuspecNamespace + "metadata").Element(nuspecNamespace + "id").Value

    let private loadPackages () =
        Assert.True(Directory.Exists packageDirectory, $"Package directory '{packageDirectory}' does not exist.")

        let allNupkgs =
            Directory.GetFiles(packageDirectory, "*.nupkg")
            |> Array.filter (fun path -> not (path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)))

        let allSnupkgs = Directory.GetFiles(packageDirectory, "*.snupkg")
        Assert.Equal(packageExpectations.Length, allNupkgs.Length)
        Assert.Equal(packageExpectations.Length, allSnupkgs.Length)

        let nupkgById =
            allNupkgs |> Array.map (fun path -> readPackageId path, path) |> Map.ofArray

        let snupkgById =
            allSnupkgs |> Array.map (fun path -> readPackageId path, path) |> Map.ofArray

        let actualIds = nupkgById |> Map.toArray |> Array.map fst |> Array.sort
        let expectedIds = packageExpectations |> Array.map _.PackageId |> Array.sort
        Assert.Equal<string>(expectedIds, actualIds)
        Assert.Equal<string>(expectedIds, snupkgById |> Map.toArray |> Array.map fst |> Array.sort)

        packageExpectations
        |> Array.map (fun expectation ->
            let nupkgPath = nupkgById[expectation.PackageId]

            { PackageId = expectation.PackageId
              Version = Path.GetFileNameWithoutExtension(nupkgPath)[expectation.PackageId.Length + 1 ..]
              NupkgPath = nupkgPath
              SnupkgPath = snupkgById[expectation.PackageId]
              AssemblyName = expectation.AssemblyName })

    let private extractPdbPath (artifact: PackageArtifact) =
        use temp = new TemporaryDirectory()
        ZipFile.ExtractToDirectory(artifact.SnupkgPath, temp.Path)

        let pdbPath =
            Path.Combine(temp.Path, "lib", "net10.0", $"{artifact.AssemblyName}.pdb")

        Assert.True(
            File.Exists pdbPath,
            $"Symbol package '{artifact.SnupkgPath}' did not contain '{artifact.AssemblyName}.pdb'."
        )

        let persistentPath =
            Path.Combine(Path.GetTempPath(), $"{artifact.PackageId}-{Guid.NewGuid():N}.pdb")

        File.Copy(pdbPath, persistentPath, true)
        persistentPath

    let private createCSharpSmokeProject packageVersion =
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <RestoreSources>{{escapeXml packageDirectory}};{{packageSource}}</RestoreSources>
            <RestorePackagesPath>$(MSBuildThisFileDirectory).packages</RestorePackagesPath>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="CircuitDotNet" Version="{{packageVersion}}" />
            <PackageReference Include="CircuitDotNet.MicrosoftAgentFramework" Version="{{packageVersion}}" />
            <PackageReference Include="CircuitDotNet.Testing" Version="{{packageVersion}}" />
            <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.9" />
          </ItemGroup>
        </Project>
        """

    let private createCSharpSmokeProgram () =
        """
        using System.ComponentModel.DataAnnotations;
        using Circuit;
        using Circuit.Testing;
        using Microsoft.Extensions.DependencyInjection;

        var runtime = new ScriptedRuntime(
        [
            ScriptedResponses.OutputJson("{\"message\":\"pong\"}"),
            ScriptedResponses.Stream(["{\"message\":\"po", "ng\"}"])
        ]);

        var services = new ServiceCollection();
        services.AddSingleton<Circuit.Core.ICircuitRuntime>(runtime);
        services.AddCircuit(_ => { });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ICircuitClient>();
        var agent = new AgentDefinition("smoke.agent", "1.0.0", "Smoke", "Return pong.");
        var signature = new AgentSignature<SmokeInput, SmokeOutput>("smoke.signature", "1.0.0", "Smoke", "Return pong.");

        var result = await client.RunAsync(agent, signature, new SmokeInput { Message = "ping" });
        if (!result.Result.IsSuccess || result.Result.Value?.Message != "pong")
        {
            throw new System.Exception("C# package smoke failed to run the typed agent.");
        }

        var events = new List<AgentRunEvent<SmokeOutput>>();
        await foreach (var @event in client.RunStreamingAsync(agent, signature, new SmokeInput { Message = "stream" }))
        {
            events.Add(@event);
        }

        var terminalCount = events.Count(@event => @event.Kind is AgentRunEventKind.RunCompleted or AgentRunEventKind.RunFailed);
        if (terminalCount != 1 || events.Count == 0 || events[^1].Kind != AgentRunEventKind.RunCompleted)
        {
            throw new System.Exception("C# package smoke did not produce one terminal streaming event.");
        }

        for (var index = 1; index < events.Count; index++)
        {
            if (events[index].Sequence <= events[index - 1].Sequence)
            {
                throw new System.Exception("C# package smoke emitted non-monotonic event sequences.");
            }
        }

        if (runtime.Calls.Count != 2 || runtime.RemainingResponses != 0)
        {
            throw new System.Exception("C# package smoke failed to execute the offline scripted scenario.");
        }

        Console.WriteLine(result.Result.Value!.Message);

        sealed class SmokeInput
        {
            [Required]
            public string Message { get; set; } = string.Empty;
        }

        sealed class SmokeOutput
        {
            [Required]
            public string Message { get; set; } = string.Empty;
        }
        """

    let private createFSharpSmokeProject packageVersion =
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <RestoreSources>{{escapeXml packageDirectory}};{{packageSource}}</RestoreSources>
            <RestorePackagesPath>$(MSBuildThisFileDirectory).packages</RestorePackagesPath>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Program.fs" />
          </ItemGroup>
          <ItemGroup>
            <PackageReference Include="CircuitDotNet" Version="{{packageVersion}}" />
            <PackageReference Include="CircuitDotNet.Core" Version="{{packageVersion}}" />
            <PackageReference Include="CircuitDotNet.FSharp" Version="{{packageVersion}}" />
            <PackageReference Include="CircuitDotNet.Testing" Version="{{packageVersion}}" />
          </ItemGroup>
        </Project>
        """

    let private createFSharpSmokeProgram () =
        """
open System.ComponentModel.DataAnnotations
open System.Threading
open Circuit.Core
open Circuit.Testing

[<AllowNullLiteral>]
type SmokeInput() =
    [<property: Required>]
    member val Message = "" with get, set

[<AllowNullLiteral>]
type SmokeOutput() =
    [<property: Required>]
    member val Message = "" with get, set

let runtime =
    ScriptedRuntime([| ScriptedResponses.OutputJson "{\"message\":\"pong\"}" |]) :> ICircuitRuntime

let agent =
    AgentDefinition.Create("smoke.agent", "1.0.0", "Smoke", "Return pong.", ValueNone, Seq.empty, Seq.empty, Seq.empty)

let signature =
    Signature<SmokeInput, SmokeOutput>.Create(
        "smoke.signature",
        "1.0.0",
        "Smoke",
        "Return pong.",
        CircuitJson.createOptions (),
        Seq.empty,
        Seq.empty
    )

let result =
    runtime.RunAsync(agent, signature, SmokeInput(Message = "ping"), RunOptions.Default, CancellationToken.None)
    |> _.Result

if not result.Result.IsSuccess then
    failwith "F# package smoke failed to run the typed agent scenario."

if result.Result.Value.Message <> "pong" then
    failwith "F# package smoke returned the wrong typed output."

printfn "%s" result.Result.Value.Message
        """

    [<Fact>]
    [<Trait("Category", "Package")>]
    let ``packed artifacts contain exactly the expected metadata and files`` () =
        let packages = loadPackages ()

        let versions = packages |> Array.map _.Version |> Array.distinct

        let version = Assert.Single versions
        Assert.Matches(semanticVersionPattern, version)

        let ids = packages |> Array.map _.PackageId |> Array.distinct
        Assert.Equal(packageExpectations.Length, ids.Length)

        for artifact in packages do
            use archive = ZipFile.OpenRead(artifact.NupkgPath)
            let entries = archive.Entries |> Seq.map _.FullName |> Seq.toArray
            let entrySet = Set.ofArray entries

            let nuspecEntry =
                Assert.Single(
                    entries
                    |> Array.filter (fun entry -> entry.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                )

            use nuspecStream = archive.GetEntry(nuspecEntry).Open()
            let document = XDocument.Load(nuspecStream)
            let metadata = document.Root.Element(nuspecNamespace + "metadata")

            Assert.Equal(artifact.PackageId, xmlValue metadata "id" |> Option.defaultValue null)
            Assert.Equal(version, xmlValue metadata "version" |> Option.defaultValue null)
            Assert.Equal("LICENSE", xmlValue metadata "license" |> Option.defaultValue null)
            Assert.Equal("icon.png", xmlValue metadata "icon" |> Option.defaultValue null)
            Assert.Equal("README.md", xmlValue metadata "readme" |> Option.defaultValue null)

            let repository = metadata.Element(nuspecNamespace + "repository")
            Assert.Equal("git", repository.Attribute(XName.Get "type").Value)
            Assert.Equal("https://github.com/skylarbpayne/circuitdotnet", repository.Attribute(XName.Get "url").Value)
            Assert.Matches(commitPattern, repository.Attribute(XName.Get "commit").Value)
            Assert.False(String.IsNullOrWhiteSpace(xmlValue metadata "description" |> Option.defaultValue String.Empty))
            Assert.False(String.IsNullOrWhiteSpace(xmlValue metadata "tags" |> Option.defaultValue String.Empty))
            Assert.Contains($"lib/net10.0/{artifact.AssemblyName}.dll", entrySet)
            Assert.Contains($"lib/net10.0/{artifact.AssemblyName}.xml", entrySet)
            Assert.Contains("README.md", entrySet)
            Assert.Contains("LICENSE", entrySet)
            Assert.Contains("icon.png", entrySet)
            Assert.True(File.Exists(artifact.SnupkgPath), $"Missing symbol package for '{artifact.PackageId}'.")

            use symbolArchive = ZipFile.OpenRead(artifact.SnupkgPath)
            let symbolEntries = symbolArchive.Entries |> Seq.map _.FullName |> Set.ofSeq
            Assert.Contains($"lib/net10.0/{artifact.AssemblyName}.pdb", symbolEntries)

            for entry in entries do
                Assert.DoesNotMatch(bannedEntryPattern, entry)
                Assert.DoesNotMatch(bannedSecretFilePattern, entry)
                Assert.DoesNotContain("packages.lock.json", entry, StringComparison.OrdinalIgnoreCase)
                Assert.DoesNotContain("/home/", entry, StringComparison.OrdinalIgnoreCase)
                Assert.DoesNotContain("/tmp/", entry, StringComparison.OrdinalIgnoreCase)

                Assert.False(
                    entry.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected package entry '{entry}'."
                )

                Assert.False(
                    entry.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected package entry '{entry}'."
                )

            for entryName, text in readTextEntries archive do
                Assert.DoesNotMatch(bannedPathFragmentPattern, text)
                Assert.DoesNotContain("packages.lock.json", text, StringComparison.OrdinalIgnoreCase)
                Assert.DoesNotContain("/home/", text, StringComparison.OrdinalIgnoreCase)
                Assert.DoesNotContain("/tmp/", text, StringComparison.OrdinalIgnoreCase)
                Assert.False(String.IsNullOrWhiteSpace text, $"Text entry '{entryName}' should not be empty.")

    [<Fact>]
    [<Trait("Category", "Package")>]
    let ``source-linked package documents stay repo-relative and do not leak local paths`` () =
        let packages = loadPackages ()

        for artifact in packages do
            let pdbPath = extractPdbPath artifact

            try
                let documents =
                    runProcess projectRoot "dotnet" $"sourcelink print-documents \"{pdbPath}\""
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> fun output ->
                        output.Split([| Environment.NewLine |], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun line -> line.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.last)

                Assert.NotEmpty documents

                for document in documents do
                    Assert.StartsWith("/_/", document)
                    Assert.DoesNotMatch(bannedSourceDocumentPattern, document)
            finally
                if File.Exists pdbPath then
                    File.Delete pdbPath

    [<Fact>]
    [<Trait("Category", "Package")>]
    let ``fresh C# console app restores from the local package feed and runs warnings as errors`` () =
        let packageVersion =
            loadPackages ()
            |> Array.find (fun artifact -> artifact.PackageId = "CircuitDotNet")
            |> _.Version

        use temp = new TemporaryDirectory()
        File.WriteAllText(Path.Combine(temp.Path, "Smoke.csproj"), createCSharpSmokeProject packageVersion)
        File.WriteAllText(Path.Combine(temp.Path, "Program.cs"), createCSharpSmokeProgram ())

        runProcess temp.Path "dotnet" "restore Smoke.csproj"
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        runProcess temp.Path "dotnet" "build Smoke.csproj -c Release --no-restore -warnaserror"
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        let output =
            runProcess temp.Path "dotnet" "run --project Smoke.csproj -c Release --no-build"
            |> fun task -> task.GetAwaiter().GetResult()

        Assert.Contains("pong", output, StringComparison.Ordinal)

    [<Fact>]
    [<Trait("Category", "Package")>]
    let ``fresh F# console app restores from the local package feed and runs warnings as errors`` () =
        let packageVersion =
            loadPackages ()
            |> Array.find (fun artifact -> artifact.PackageId = "CircuitDotNet.FSharp")
            |> _.Version

        use temp = new TemporaryDirectory()
        File.WriteAllText(Path.Combine(temp.Path, "Smoke.fsproj"), createFSharpSmokeProject packageVersion)
        File.WriteAllText(Path.Combine(temp.Path, "Program.fs"), createFSharpSmokeProgram ())

        runProcess temp.Path "dotnet" "restore Smoke.fsproj"
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        runProcess temp.Path "dotnet" "build Smoke.fsproj -c Release --no-restore -warnaserror"
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        let output =
            runProcess temp.Path "dotnet" "run --project Smoke.fsproj -c Release --no-build"
            |> fun task -> task.GetAwaiter().GetResult()

        Assert.Contains("pong", output, StringComparison.Ordinal)
