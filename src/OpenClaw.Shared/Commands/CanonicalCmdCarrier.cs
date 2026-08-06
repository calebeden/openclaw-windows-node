using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenClaw.Shared.Commands;

/// <summary>
/// Single owner of the canonical <c>cmd.exe /d /s /c &lt;command&gt;</c> carrier shape.
///
/// The Windows node originates this argv when it forwards a shell command, and the
/// gateway validates it against the approval record's rawCommand. Both the exec
/// approvals binder and the MXC command-line builder must agree on exactly which
/// argv shapes are that carrier and what command text it carries, otherwise one
/// layer can authorize a shape the other refuses to run (or vice versa).
/// </summary>
internal static class CanonicalCmdCarrier
{
    /// <summary>
    /// True when the token names cmd by basename, so both bare <c>cmd</c>/<c>cmd.exe</c>
    /// and a fully-qualified <c>C:\Windows\System32\cmd.exe</c> are recognized.
    ///
    /// This is the SERIALIZATION predicate. cmd parses its own raw command line
    /// instead of going through CommandLineToArgvW, so any argv that cmd will
    /// receive must be built with the cmd-aware serializer no matter where the
    /// image lives. Being permissive here fails safe: the worst case is correct
    /// quoting for an untrusted image. Do not use it to decide trust.
    /// </summary>
    internal static bool IsCmdExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        var fileName = Path.GetFileName(executable.Trim());
        return string.Equals(fileName, "cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only for a carrier we are willing to look THROUGH when deciding
    /// approval identity.
    ///
    /// Looking through a carrier means the operator is shown, and may durably
    /// approve, the inner executable while the outer image is what actually runs
    /// for a one-time allow. That is only sound when the outer image is the real
    /// system cmd. A copy of cmd.exe in a writable directory can ignore its
    /// arguments entirely and run arbitrary code, so it must never be looked
    /// through: an unrecognized carrier falls through to the indirect-host
    /// rejection and stays unbindable.
    ///
    /// The canonical gateway carrier uses the bare name. A fully-qualified path
    /// is accepted only when it points into the Windows system directories.
    /// </summary>
    internal static bool IsTrustedCarrierExecutable(string? executable)
    {
        if (!IsCmdExecutable(executable))
            return false;

        var trimmed = executable!.Trim();
        var directory = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(directory))
            return true;

        if (!Path.IsPathFullyQualified(trimmed))
            return false;

        foreach (var systemDirectory in SystemDirectories())
        {
            if (string.IsNullOrEmpty(systemDirectory))
                continue;
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemDirectory)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SystemDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    }

    /// <summary>
    /// Recognizes <c>cmd[.exe] /d /s /c &lt;tail...&gt;</c> and reconstructs the single
    /// command-line string cmd.exe receives.
    ///
    /// The tail may be a single pre-joined element or several already-tokenized
    /// elements; upstream approval fixtures use both. A multi-element tail is only
    /// reconstructible when every element is free of whitespace and quotes, because
    /// otherwise the process-creation quoting is not recoverable by a plain space
    /// join. Non-reconstructible tails return false so callers fail closed rather
    /// than guessing at a different command than the one that will run.
    /// </summary>
    internal static bool TryGetCanonicalPayload(IReadOnlyList<string> argv, out string payload) =>
        TryGetCanonicalPayload(argv, requireTrustedCarrier: false, out payload);

    /// <summary>
    /// Trust-side variant: only looks through a carrier that
    /// <see cref="IsTrustedCarrierExecutable"/> accepts.
    /// </summary>
    internal static bool TryGetTrustedCanonicalPayload(IReadOnlyList<string> argv, out string payload) =>
        TryGetCanonicalPayload(argv, requireTrustedCarrier: true, out payload);

    private static bool TryGetCanonicalPayload(
        IReadOnlyList<string> argv,
        bool requireTrustedCarrier,
        out string payload)
    {
        payload = "";
        if (argv is null
            || argv.Count < 5
            || !(requireTrustedCarrier
                ? IsTrustedCarrierExecutable(argv[0])
                : IsCmdExecutable(argv[0]))
            || !IsSwitch(argv[1], "/d")
            || !IsSwitch(argv[2], "/s")
            || !IsSwitch(argv[3], "/c"))
        {
            return false;
        }

        if (argv.Count == 5)
        {
            payload = argv[4];
            return true;
        }

        var builder = new StringBuilder();
        for (var i = 4; i < argv.Count; i++)
        {
            var token = argv[i];
            if (string.IsNullOrEmpty(token) || !IsSpaceJoinable(token))
                return false;
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(token);
        }

        payload = builder.ToString();
        return true;
    }

    private static bool IsSwitch(string token, string expected) =>
        string.Equals(token, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsSpaceJoinable(string token)
    {
        foreach (var ch in token)
        {
            if (char.IsWhiteSpace(ch) || ch == '"' || ch == '\'')
                return false;
        }

        return true;
    }
}
