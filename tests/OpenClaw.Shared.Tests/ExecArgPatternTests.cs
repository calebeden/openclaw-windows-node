using System;
using System.Collections.Generic;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

// The durable argument binding is exchanged with the gateway and shared with the
// macOS node, so its written form is a wire contract rather than an internal detail.
// These tests pin the exact shapes both sides agree on.
public class ExecArgPatternTests
{
    [Fact]
    public void NoArguments_WritesTheEmptyArgumentForm()
    {
        // A command with no arguments is distinguishable from one with a single empty
        // argument, which is why the empty form is a pair of separators rather than
        // an empty string.
        Assert.Equal("^\0\0$", ExecArgPattern.BuildArgPattern([@"C:\Windows\System32\hostname.exe"]));
    }

    [Fact]
    public void Arguments_AreEscapedAndSeparatedByNul()
    {
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\python.exe", "script.py"]);
        Assert.Equal("^script\\.py\0$", pattern);
    }

    [Fact]
    public void RegexMetacharactersInAnArgument_AreMatchedLiterally()
    {
        // Without escaping, an approved argument containing regex syntax would widen
        // the rule to whatever that syntax happens to match.
        var argv = new[] { @"C:\tool.exe", "a.*b" };
        var pattern = ExecArgPattern.BuildArgPattern(argv);

        Assert.True(ExecArgPattern.Matches(pattern, argv));
        Assert.False(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "aXXXb"]));
    }

    [Fact]
    public void AnArgumentContainingASeparator_CannotImpersonateTwoArguments()
    {
        // A space-joined subject would render these two commands identically. The NUL
        // separator is what keeps one argument from spanning an argument boundary.
        var single = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "one two"]);

        Assert.False(ExecArgPattern.Matches(single, [@"C:\tool.exe", "one", "two"]));
        Assert.True(ExecArgPattern.Matches(single, [@"C:\tool.exe", "one two"]));
    }

    [Fact]
    public void ApprovedArguments_DoNotAuthorizeALongerCommandThatStartsWithThem()
    {
        // The matcher on the other side of the wire tests without anchoring, so the
        // written pattern has to carry its own anchors.
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "status"]);

        Assert.False(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "status", "--force"]));
    }

    [Fact]
    public void EitherSpellingOfAPathArgument_MatchesTheSameRule()
    {
        var pattern = ExecArgPattern.BuildArgPattern([@"C:\tool.exe", "dir/script.py"]);

        Assert.True(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", @"dir\script.py"]));
        Assert.True(ExecArgPattern.Matches(pattern, [@"C:\tool.exe", "dir/script.py"]));
    }

    [Fact]
    public void MalformedStoredPattern_FailsClosed()
    {
        // A stored pattern is remote-influenced input. An unparsable one must not
        // throw into the approval path, and must not authorize anything either.
        Assert.False(ExecArgPattern.Matches("^(unclosed", [@"C:\tool.exe", "x"]));
    }

    [Fact]
    public void HashedPatternWrittenByMacOs_IsMatchedByExactEquality()
    {
        var argv = new[] { "/usr/bin/tool", "--flag", "value" };
        var hashed = ExecArgPattern.BuildHashedArgPattern(argv);

        Assert.StartsWith("sha256:argv:", hashed, StringComparison.Ordinal);
        Assert.True(ExecArgPattern.Matches(hashed, argv));
        Assert.False(ExecArgPattern.Matches(hashed, ["/usr/bin/tool", "--flag", "other"]));
    }

    [Fact]
    public void HashedPattern_DistinguishesArgumentBoundaries()
    {
        // The digest covers a length-prefixed rendering, so no rearrangement of the
        // same characters across arguments produces the same pattern.
        var a = ExecArgPattern.BuildHashedArgPattern(["/bin/t", "ab", "c"]);
        var b = ExecArgPattern.BuildHashedArgPattern(["/bin/t", "a", "bc"]);

        Assert.NotEqual(a, b);
    }
}

// The rule that decides whether a stored entry authorizes a command. It is shared
// with the gateway and the macOS node, so the same allowlist file has to mean the
// same thing in all three places.
public class ExecAllowlistArgBindingTests
{
    private static ExecCommandResolution Resolution(string path)
        => ExecCommandResolver.Resolve([path], cwd: null, env: null)
            ?? throw new InvalidOperationException("resolution failed");

    [Fact]
    public void GeneratedEntryWithNoArgumentBinding_IsNotHonored()
    {
        // Generated entries have pinned their arguments since argument binding was
        // introduced. One that lacks a binding is an older record whose arguments were
        // never captured, so honoring it would let a rule approved for one command
        // authorize every later command that reuses the executable.
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            Source = "allow-always",
        };

        Assert.Null(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    [Fact]
    public void HandWrittenEntryWithNoArgumentBinding_IsHonored()
    {
        // A rule with no source was written by a human who chose to authorize the
        // executable itself. That is a deliberate decision, not a stale record.
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
        };

        Assert.NotNull(ExecAllowlistMatcher.Match(
            [entry],
            Resolution(@"C:\Windows\System32\hostname.exe"),
            [@"C:\Windows\System32\hostname.exe", "--anything"]));
    }

    [Fact]
    public void BoundEntry_AuthorizesOnlyTheApprovedArguments()
    {
        var argv = new[] { @"C:\Windows\System32\hostname.exe", "--fqdn" };
        var entry = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            ArgPattern = ExecArgPattern.BuildArgPattern(argv),
            Source = "allow-always",
        };
        var resolution = Resolution(@"C:\Windows\System32\hostname.exe");

        Assert.NotNull(ExecAllowlistMatcher.Match([entry], resolution, argv));
        Assert.Null(ExecAllowlistMatcher.Match(
            [entry], resolution, [@"C:\Windows\System32\hostname.exe", "--other"]));
    }

    [Fact]
    public void ABoundEntryIsPreferredOverAHandWrittenPathOnlyEntry()
    {
        // Order in the file must not decide which rule applies, or an audit of the
        // file could not tell what authorized a command.
        var argv = new[] { @"C:\Windows\System32\hostname.exe", "--fqdn" };
        var pathOnly = new ExecAllowlistEntry { Pattern = @"**/hostname.exe" };
        var bound = new ExecAllowlistEntry
        {
            Pattern = @"C:\Windows\System32\hostname.exe",
            ArgPattern = ExecArgPattern.BuildArgPattern(argv),
            Source = "allow-always",
        };

        var match = ExecAllowlistMatcher.Match(
            [pathOnly, bound], Resolution(@"C:\Windows\System32\hostname.exe"), argv);

        Assert.Same(bound, match);
    }
}

// rawCommand is the text an operator is shown. If it could disagree with the argv
// that runs, a request could describe one command and execute another.
public class ExecRawCommandConsistencyTests
{
    [Fact]
    public void AbsentRawCommand_ImposesNoConstraint()
        => Assert.True(ExecRawCommandConsistency.IsConsistent(null, ["hostname.exe"]));

    [Fact]
    public void GatewayFormattedArgv_IsAccepted()
    {
        // The gateway quotes only on whitespace, a double quote, or an empty string.
        // Anything it produced has to be accepted here or valid traffic breaks.
        Assert.True(ExecRawCommandConsistency.IsConsistent(
            "cmd.exe /d /s /c echo SAFE&&whoami",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));
    }

    [Fact]
    public void InlineShellPayloadOfACarrier_IsAccepted()
    {
        // The gateway also accepts the text after /c for a wrapper invocation, so a
        // real carrier request legitimately carries only its payload as rawCommand.
        Assert.True(ExecRawCommandConsistency.IsConsistent(
            "echo SAFE&&whoami",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));
    }

    [Fact]
    public void TextThatDescribesADifferentCommand_IsRejected()
    {
        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "echo",
            ["cmd.exe", "/d", "/s", "/c", "echo", "SAFE&&whoami"]));

        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "hostname.exe",
            ["whoami.exe"]));
    }

    [Fact]
    public void PayloadFormIsNotAcceptedForANonCarrier()
    {
        // Only a cmd invocation carries an inline payload. Accepting the tail of an
        // ordinary command would let rawCommand omit the executable being run.
        Assert.False(ExecRawCommandConsistency.IsConsistent(
            "SAFE&&whoami",
            ["hostname.exe", "SAFE&&whoami"]));
    }
}
