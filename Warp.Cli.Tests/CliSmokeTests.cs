using System.Diagnostics;
using Warp.Testing;
using Xunit;

namespace Warp.Cli.Tests;

public sealed class CliSmokeTests
{
    [Fact]
    public async Task Help_command_exposes_the_compiler_commands()
    {
        var root = FindWorkspaceRoot();
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("--project"); start.ArgumentList.Add("Warp.Cli/Warp.Cli.csproj"); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--"); start.ArgumentList.Add("--help");
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("js-compile", output, StringComparison.Ordinal);
        Assert.Contains("build", output, StringComparison.Ordinal);
        Assert.Contains("pack", output, StringComparison.Ordinal);
        Assert.Contains("create", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_generates_a_buildable_hello_world_project()
    {
        var root = FindWorkspaceRoot();
        var project = TestWorkspace.CreateDirectory("warp-created");
        try
        {
            var create = await RunCli(root, root, "create", project);
            Assert.True(create.ExitCode == 0, create.Error);
            Assert.True(File.Exists(Path.Combine(project, "manifest.yaml")));
            Assert.True(File.Exists(Path.Combine(project, "src", "pages", "home", "home.wxaml")));
            Assert.Contains("name: " + Path.GetFileName(project), await File.ReadAllTextAsync(Path.Combine(project, "manifest.yaml"), TestContext.Current.CancellationToken), StringComparison.Ordinal);

            var build = await RunCli(root, project, "build", "--project", project);
            Assert.True(build.ExitCode == 0, build.Error);
            Assert.True(File.Exists(Path.Combine(project, "build", "pages", "home", "home.jsc")));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Build_and_pack_support_the_same_custom_output_directory()
    {
        var root = FindWorkspaceRoot();
        var project = TestWorkspace.CreateDirectory("warp-cli");
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            Directory.CreateDirectory(page);
            await File.WriteAllTextAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.cli
                name: CLI
                versionCode: 1
                icon: /icon.png
                config:
                  logLevel: log
                  designWidth: device-width
                router:
                  entry: pages/home
                  pages:
                    pages/home:
                      component: home
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(page, "home.js"), "export default { data: { title: 'Hello' } };", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(page, "home.wxaml"), "<Page x:Class=\"Home\"><Text Text=\"{Binding title}\" /></Page>", TestContext.Current.CancellationToken);

            var build = await RunCli(root, project, "build", "--project", project, "--output", "device");
            Assert.True(build.ExitCode == 0, build.Error);
            Assert.Contains("Build succeeded: 1 page(s) compiled to bytecode.", build.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(project, "device", "app.jsc")));
            Assert.False(File.Exists(Path.Combine(project, "device", "app.js")));

            var pack = await RunCli(root, project, "pack", "--project", project, "--output", "device", "--rpk", "dist/sample.rpk");
            Assert.True(pack.ExitCode == 0, pack.Error);
            Assert.True(File.Exists(Path.Combine(project, "dist", "sample.rpk")));
            Assert.Contains("Packed", pack.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCli(string workspaceRoot, string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("run"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(Path.Combine(workspaceRoot, "Warp.Cli", "Warp.Cli.csproj")); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output, error);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Warp.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Warp.sln not found from test output directory");
    }
}
