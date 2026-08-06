using System;
using System.IO;
using OpenClaw.Shared.Commands;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Ledger guard for `canonical-cmd-carrier`.
///
/// The Windows node originates `cmd.exe /d /s /c &lt;command&gt;` when it forwards a
/// shell command. Two layers act on that shape: the exec approvals binder decides
/// whether the inner command may be durably authorized, and the MXC command-line
/// builder decides how to render it for execution. When those two disagree about
/// which argv shapes are the carrier, one layer can authorize a shape the other
/// refuses to run. Both must therefore route through CanonicalCmdCarrier.
/// </summary>
public class CanonicalCmdCarrierTests
{
    public static TheoryData<string[], bool> CarrierShapes() => new()
    {
        { ["cmd.exe", "/d", "/s", "/c", "hostname.exe"], true },
        { ["cmd", "/d", "/s", "/c", "hostname.exe"], true },
        { ["CMD.EXE", "/D", "/S", "/C", "hostname.exe"], true },
        { [@"C:\Windows\System32\cmd.exe", "/d", "/s", "/c", "hostname.exe"], true },
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello"], true },
        // Not the canonical carrier.
        { ["cmd.exe", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/s", "/k", "hostname.exe"], false },
        { ["cmd.exe", " /d ", "/s", "/c", "hostname.exe"], false },
        { ["cmd.exe", "/d", "/s", "/c"], false },
        { ["hostname.exe"], false },
        // Recognized prefix but a tail a space join cannot reconstruct.
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello world"], false },
        { ["cmd.exe", "/d", "/s", "/c", "where.exe", "\"quoted\""], false },
    };

    [Theory]
    [MemberData(nameof(CarrierShapes))]
    public void BinderAndMxcBuilder_AgreeOnCarrierRecognition(string[] argv, bool isCarrier)
    {
        Assert.Equal(isCarrier, CanonicalCmdCarrier.TryGetCanonicalPayload(argv, out _));

        // The MXC builder must not carry its own competing definition. It detects
        // cmd command mode and then defers to the shared helper for recognition and
        // payload extraction, so any argv the helper rejects is rejected there too.
        var mxcSource = ReadMxcConfigBuilderSource();
        Assert.Contains("CanonicalCmdCarrier.TryGetCanonicalPayload", mxcSource);
        Assert.Contains("CanonicalCmdCarrier.IsCmdExecutable", mxcSource);
        Assert.DoesNotContain("private static bool IsCmdExecutable", mxcSource);
    }

    [Theory]
    [MemberData(nameof(CarrierShapes))]
    public void BinderNeverBindsCarrierItself(string[] argv, bool isCarrier)
    {
        _ = isCarrier;
        var bound = ExecReusableCommandBinder.TryBind(argv, cwd: null, env: null);

        // Whatever the binder decides, it must never return cmd.exe as the durably
        // authorized executable: cmd selects the code it runs from its arguments.
        if (bound is not null)
        {
            Assert.False(
                CanonicalCmdCarrier.IsCmdExecutable(bound.Argv[0]),
                $"Binder durably authorized the cmd carrier itself: {bound.Argv[0]}");
        }
    }

    [Fact]
    public void MultiElementTail_ReconstructsSpaceJoinedPayload()
    {
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            ["cmd.exe", "/d", "/s", "/c", "where.exe", "hello", "there"],
            out var payload));
        Assert.Equal("where.exe hello there", payload);
    }

    [Fact]
    public void SingleElementTail_IsUsedVerbatim()
    {
        Assert.True(CanonicalCmdCarrier.TryGetCanonicalPayload(
            ["cmd.exe", "/d", "/s", "/c", "where.exe   hello"],
            out var payload));
        Assert.Equal("where.exe   hello", payload);
    }

    private static string ReadMxcConfigBuilderSource()
    {
        var path = Path.Combine(
            ProductionSourceFiles.FindRepoRoot(),
            "src",
            "OpenClaw.Shared",
            "Mxc",
            "MxcConfigBuilder.cs");
        Assert.True(File.Exists(path), $"MxcConfigBuilder source not found: {path}");
        return File.ReadAllText(path);
    }
}
