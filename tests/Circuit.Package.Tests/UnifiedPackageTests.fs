module Circuit.Package.Tests.UnifiedPackageTests

open System
open System.Diagnostics
open System.IO
open Circuit.Core
open Xunit

[<Fact>]
let ``published assemblies expose unified Circuit model`` () =
    let core = typeof<ICircuitRuntime>.Assembly
    Assert.NotNull(core.GetType("Circuit.Core.Circuit`2"))
    Assert.NotNull(core.GetType("Circuit.Core.Response`1"))
    Assert.NotNull(core.GetType("Circuit.Core.CircuitRun`1"))
    Assert.Null(core.GetType("Circuit.Core.I" + "Workflow" + "Runtime"))
    Assert.Null(core.GetType("Circuit.Core.IInteractive" + "CircuitRuntime"))
    Assert.Null(core.GetType("Circuit.Core.Workflow" + "Definition`2"))

[<Fact>]
let ``MAF package has no workflow engine dependency`` () =
    let references =
        typeof<Circuit.MicrosoftAgentFramework.MafRuntime>.Assembly.GetReferencedAssemblies()

    Assert.DoesNotContain(references, fun reference -> reference.Name.Contains("Work" + "flows"))

let private run (fileName: string) (arguments: string) (workingDirectory: string) =
    let startInfo = ProcessStartInfo(fileName, arguments)
    startInfo.WorkingDirectory <- workingDirectory
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    use startedProcess = Process.Start(startInfo)

    let output =
        startedProcess.StandardOutput.ReadToEnd()
        + startedProcess.StandardError.ReadToEnd()

    startedProcess.WaitForExit()
    Assert.True(startedProcess.ExitCode = 0, output)

[<Fact; Trait("Category", "Package")>]
let ``packed CSharp and FSharp consumers compile unified API`` () =
    let packageDirectory = Environment.GetEnvironmentVariable("PackageDirectory")

    if not (String.IsNullOrWhiteSpace packageDirectory) then
        let packages =
            Directory.GetFiles(packageDirectory, "*.nupkg")
            |> Array.filter (fun path -> not (path.EndsWith(".snupkg")))

        Assert.Equal(5, packages.Length)

        let primary =
            packages
            |> Array.find (fun path ->
                Path.GetFileName(path).StartsWith("CircuitDotNet.")
                && not (Path.GetFileName(path).StartsWith("CircuitDotNet.Core."))
                && not (Path.GetFileName(path).StartsWith("CircuitDotNet.FSharp."))
                && not (Path.GetFileName(path).StartsWith("CircuitDotNet.MicrosoftAgentFramework."))
                && not (Path.GetFileName(path).StartsWith("CircuitDotNet.Testing.")))

        let version =
            Path.GetFileNameWithoutExtension(primary).Substring("CircuitDotNet.".Length)

        let root =
            Path.Combine(Path.GetTempPath(), "circuit-package-smoke-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(root) |> ignore

        try
            let csharp = Path.Combine(root, "csharp")
            Directory.CreateDirectory(csharp) |> ignore

            File.WriteAllText(
                Path.Combine(csharp, "Smoke.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include=\"CircuitDotNet\" Version=\"{version}\" /><PackageReference Include=\"CircuitDotNet.MicrosoftAgentFramework\" Version=\"{version}\" /><PackageReference Include=\"CircuitDotNet.Testing\" Version=\"{version}\" /></ItemGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(csharp, "Program.cs"),
                """
                using System;
                using System.Threading.Tasks;
                using Circuit;
                using Circuit.MicrosoftAgentFramework;
                using Circuit.Testing;

                _ = new MafRuntimeOptions();
                var runtime = new ScriptedRuntime(Array.Empty<ScriptedResponse>());
                var client = new CircuitClientBuilder().UseRuntime(runtime).Build();
                var graph = CircuitDefinition<string,string>.FromCode("echo", "1.0.0", (_, value, _) => Task.FromResult(value));
                var result = await client.RunAsync(graph, "packed");
                if (!result.IsSuccess || result.Value != "packed") return 1;

                var approval = CircuitDefinition<string, ApprovalResponse>.Approval(
                    "approval", "1.0.0", value => new ApprovalPrompt("Review", value));
                await using var run = await client.StartAsync(approval, "packed-approval");
                CircuitCheckpoint<ApprovalResponse>? checkpoint = null;
                await foreach (var item in run.Events)
                {
                    if (item.Kind == CircuitEventKind.ApprovalRequested)
                    {
                        var saved = await run.CreateCheckpointAsync();
                        if (!saved.IsSuccess) return 2;
                        checkpoint = saved.Value;
                        var accepted = await run.RespondAsync(new ApprovalResponse(item.Approval!.RequestId, true));
                        if (!accepted.IsSuccess) return 3;
                    }
                    if (item.Kind == CircuitEventKind.RunCompleted) break;
                }
                if (checkpoint is null) return 4;
                var roundTrip = CircuitCheckpoint<ApprovalResponse>.Deserialize(checkpoint.Serialize());
                await using var resumed = await client.ResumeAsync(approval, roundTrip);
                Console.WriteLine(result.Metadata.NodePath);
                return 0;
                """
            )

            let csharpPackages = Path.Combine(csharp, ".packages")

            run
                "dotnet"
                $"restore --no-cache --packages \"{csharpPackages}\" --source \"{packageDirectory}\" --source https://api.nuget.org/v3/index.json"
                csharp

            run "dotnet" "run --no-restore" csharp

            let fsharp = Path.Combine(root, "fsharp")
            Directory.CreateDirectory(fsharp) |> ignore

            File.WriteAllText(
                Path.Combine(fsharp, "Smoke.fsproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Compile Include=\"Program.fs\" /><PackageReference Include=\"CircuitDotNet.FSharp\" Version=\"{version}\" /><PackageReference Include=\"CircuitDotNet.Testing\" Version=\"{version}\" /></ItemGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(fsharp, "Program.fs"),
                "open System\nopen System.Threading\nopen Circuit.Core\nopen Circuit.FSharp\nopen Circuit.Testing\nlet runtime = ScriptedRuntime(Array.empty) :> ICircuitRuntime\nlet graph : Circuit<unit,int> = Circuit.value 1\nlet result = Circuit.run runtime graph () RunOptions.Default CancellationToken.None |> _.Result\nif not result.IsSuccess || result.Value <> 1 then failwith \"packed run failed\"\nprintfn \"%s\" result.Metadata.NodePath\n"
            )

            let fsharpPackages = Path.Combine(fsharp, ".packages")

            run
                "dotnet"
                $"restore --no-cache --packages \"{fsharpPackages}\" --source \"{packageDirectory}\" --source https://api.nuget.org/v3/index.json"
                fsharp

            run "dotnet" "run --no-restore" fsharp
        finally
            Directory.Delete(root, true)
