using System;
using System.IO;

namespace OpenClaw.Shared.ExecApprovals;

// Utility helpers for command token classification.
internal static class ExecCommandToken
{
    // Returns the lowercased last path component (basename) of a token, without extension.
    internal static string BasenameLower(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            trimmed = trimmed[1..^1];
        var name = Path.GetFileName(trimmed.Replace('\\', '/'));
        if (name.Length == 0) name = trimmed;
        return name.ToLowerInvariant();
    }

    // Returns the basename without a directly-executable image suffix (lowercased).
    //
    // .exe is stripped because that is how command identities are ordinarily spelled.
    // .com is stripped for one narrow reason only: IsLegacyQuarantinedHost below has
    // to recognize a provenance-less legacy entry that names `powershell.com`, which
    // the old catalog would have refused, exactly as it recognizes `powershell.exe`.
    // Without this, that entry would quietly become more permissive than it was when
    // it was written. This is a classification detail of the transitional quarantine
    // and says nothing about which images may be bound durably; that is decided
    // solely by ExecReusableCommandBinder.IsBindableExecutable, which is .exe only.
    internal static string NormalizedBasename(string token)
    {
        var b = BasenameLower(token);
        return b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || b.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
            ? b[..^4]
            : b;
    }

    internal static bool IsEnv(string token) =>
        NormalizedBasename(token) == "env";

    // NOTE: an earlier revision kept a catalog of interpreter and script-host
    // basenames here and refused durable approval for each. That control has been
    // removed rather than expanded. It failed in both directions: the list could
    // never enumerate every binary that can proxy execution, and a renamed copy
    // defeated a basename lookup entirely.
    //
    // Its job is now done structurally and unconditionally. Every rule this node
    // generates pins the command's arguments (see ExecArgPattern), so a rule for an
    // interpreter authorizes one script rather than the interpreter itself, and
    // wrapper invocations are classified by shape rather than by name
    // (ExecShellWrapperNormalizer, CanonicalCmdCarrier). Do not reintroduce a
    // name-based list as the security boundary.
    //
    // IsLegacyQuarantinedHost below is NOT that boundary and must not become it. It
    // applies to exactly one thing: an allowlist entry already on disk that carries no
    // provenance and no argument binding, written when this catalog was the rule. Such
    // an entry cannot be reasoned about, because we cannot tell a deliberate operator
    // rule from one this node generated under the old model. For an ordinary program
    // it keeps working. For a name this node once refused outright it goes inert and
    // prompts, so the previously denied case is not silently upgraded to allowed by
    // the change in model. New rules never reach this path; they always carry an
    // argument binding, which is the real control.
    private static readonly System.Collections.Generic.HashSet<string> s_legacyQuarantinedHosts =
        new(StringComparer.Ordinal)
        {
            "sh", "bash", "zsh", "dash", "ash", "ksh", "fish",
            "cmd", "powershell", "pwsh",
            "wsl", "cscript", "wscript",
            "py", "pyw", "python", "pythonw", "pypy",
            "node", "nodejs", "deno", "bun", "qjs",
            "ruby", "jruby", "perl", "php", "lua", "luajit",
            "java", "javaw", "jshell", "dotnet", "csi", "fsi", "fsharpi",
            "r", "rscript", "tclsh", "wish", "groovy",
        };

    /// <summary>
    /// True when a token names a program that the previous model refused to approve
    /// durably. Read the note above before using this: it exists only to keep a
    /// provenance-less legacy allowlist entry from becoming more permissive than it was
    /// when it was written, and it is not a security boundary on its own.
    /// </summary>
    internal static bool IsLegacyQuarantinedHost(string token)
    {
        var name = NormalizedBasename(token);
        if (name.Length == 0) return false;
        if (s_legacyQuarantinedHosts.Contains(name)) return true;

        // Versioned interpreters (python3, python3.12, pypy3.10) were covered by the
        // old catalog too, so they stay covered here.
        return IsVersionedInterpreter(name, "python")
            || IsVersionedInterpreter(name, "pythonw")
            || IsVersionedInterpreter(name, "pypy");
    }

    private static bool IsVersionedInterpreter(string name, string prefix)
    {
        if (name.Length <= prefix.Length
            || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var sawDigit = false;
        for (var i = prefix.Length; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch == '.') continue;
            if (ch is < '0' or > '9') return false;
            sawDigit = true;
        }

        return sawDigit;
    }

    // Extracts the first shell-tokenized word from a command pattern. Quoted paths
    // remain one token, and a suffix after the closing quote is preserved so
    // `"git".exe` is classified as git.exe.
    internal static string? ParseFirstToken(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return null;
        var first = trimmed[0];
        if (first == '"' || first == '\'')
        {
            var rest = trimmed.AsSpan(1);
            var end = rest.IndexOf(first);
            if (end < 0) return null;
            var inner = rest[..end].ToString();
            if (inner.Length == 0) return null;
            var afterClose = rest[(end + 1)..];
            var suffixEnd = afterClose.IndexOfAny(' ', '\t');
            var suffix = suffixEnd >= 0 ? afterClose[..suffixEnd].ToString() : afterClose.ToString();
            return suffix.Length > 0 ? inner + suffix : inner;
        }

        var space = trimmed.AsSpan().IndexOfAny(' ', '\t');
        return space >= 0 ? trimmed[..space] : trimmed;
    }
}
