using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KHZ.Sheet.ManagedRunnerTests;

[TestClass]
public sealed class ManagedRunnerTests
{
    private static readonly string[] RunnerNames =
    [
        "KhzExactLiteral",
        "KhzFormulaCells",
        "KhzDependencyRewrite",
        "KhzOpcPackage",
        "KhzDesktopContracts",
        "KhzBoundaryContracts",
        "KhzSubsystemContracts"
    ];

    public static IEnumerable<object[]> Runners =>
        RunnerNames.Select(name => new object[] { name });

    [DataTestMethod]
    [DynamicData(nameof(Runners))]
    public void Console_runner_must_pass(string runner)
    {
        string repo = Environment.GetEnvironmentVariable("KHZ_REPO_ROOT")
            ?? FindRepoRoot();
        string project = Path.Combine(repo, "tests", "dotnet", runner, runner + ".csproj");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--no-build");

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet runner.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.AreEqual(0, process.ExitCode,
            runner + " failed.\nSTDOUT:\n" + stdout + "\nSTDERR:\n" + stderr);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CMakeLists.txt")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "dotnet")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("KHZ repository root not found.");
    }
}
