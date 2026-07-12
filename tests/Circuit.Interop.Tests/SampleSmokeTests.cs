using System.Diagnostics;
using Xunit;

namespace Circuit.Interop.Tests;

public sealed class SampleSmokeTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [Trait("Category", "Package")]
    [InlineData("samples/TicketTriage.CSharp/TicketTriage.CSharp.csproj")]
    [InlineData("samples/TicketTriage.FSharp/TicketTriage.FSharp.fsproj")]
    public async Task offline_ticket_triage_samples_exit_zero(string projectPath)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" --no-restore",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Sample '{projectPath}' exited {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{stderr}");
    }
}
