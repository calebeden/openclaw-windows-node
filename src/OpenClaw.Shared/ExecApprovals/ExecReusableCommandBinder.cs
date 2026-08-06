using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

internal static class ExecReusableCommandBinder
{
    private static readonly HashSet<string> s_cmdBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy",
        "date", "del", "dir", "echo", "endlocal", "erase", "exit", "for",
        "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path",
        "pause", "popd", "prompt", "pushd", "rd", "rem", "ren", "rename",
        "rmdir", "set", "setlocal", "shift", "start", "time", "title", "type",
        "ver", "verify", "vol",
    };

    internal static ExecReusableCommand? TryBind(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        if (command.Count == 0)
            return null;

        if (TryGetCanonicalCmdPayload(command, out var payload))
        {
            if (!TryTokenizeStaticCmdPayload(payload, out var payloadArgv))
                return null;
            if (payloadArgv.Count == 0
                || ExecCommandToken.IsEnv(payloadArgv[0])
                || s_cmdBuiltins.Contains(ExecCommandToken.NormalizedBasename(payloadArgv[0])))
            {
                return null;
            }
            return BindDirect(payloadArgv, cwd, env);
        }

        if (ExecShellWrapperNormalizer.Extract(command).IsWrapper)
        {
            return null;
        }

        return BindDirect(command, cwd, env);
    }

    private static ExecReusableCommand? BindDirect(
        IReadOnlyList<string> argv,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
    {
        if (argv.Count == 0)
            return null;

        if (ExecEnvInvocationUnwrapper.AnyWrapperHasModifiers(argv))
            return null;
        var effectiveArgv = ExecEnvInvocationUnwrapper.UnwrapForResolution(argv);
        if (effectiveArgv.Count == 0
            || ExecCommandToken.IsEnv(effectiveArgv[0]))
        {
            return null;
        }

        var resolution = ExecCommandResolver.Resolve(effectiveArgv, cwd, env);
        var resolvedPath = resolution?.ResolvedPath;
        if (resolution is null
            || string.IsNullOrWhiteSpace(resolvedPath)
            || !Path.IsPathFullyQualified(resolvedPath)
            || !File.Exists(resolvedPath)
            || IsBatchFile(resolvedPath)
            || ExecCommandToken.IsIndirectCommandHost(resolvedPath))
        {
            return null;
        }

        var boundArgv = new string[effectiveArgv.Count];
        boundArgv[0] = resolvedPath;
        for (var i = 1; i < effectiveArgv.Count; i++)
            boundArgv[i] = effectiveArgv[i];
        return new ExecReusableCommand(boundArgv, resolution.Value);
    }

    private static bool TryGetCanonicalCmdPayload(
        IReadOnlyList<string> command,
        out string payload)
    {
        payload = "";
        if (command.Count != 5
            || (!string.Equals(command[0], "cmd", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command[0], "cmd.exe", StringComparison.OrdinalIgnoreCase))
            || !string.Equals(command[1], "/d", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command[2], "/s", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(command[3], "/c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        payload = command[4];
        return true;
    }

    internal static bool TryTokenizeStaticCmdPayload(
        string payload,
        out IReadOnlyList<string> argv)
    {
        argv = [];
        var trimmedStart = payload.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmedStart)
            || trimmedStart[0] == '@'
            || payload.Contains('"')
            || payload.TrimEnd(' ', '\t').EndsWith('\\'))
            return false;

        var tokens = new List<string>();
        var current = new StringBuilder();
        var tokenStarted = false;

        for (var i = 0; i < payload.Length; i++)
        {
            var ch = payload[i];
            if ((char.IsControl(ch) && ch != '\t')
                || (char.IsWhiteSpace(ch) && !IsCmdWhitespace(ch))
                || IsForbiddenCmdSyntax(ch))
                return false;

            if (IsCmdWhitespace(ch))
            {
                if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
                continue;
            }

            tokenStarted = true;
            current.Append(ch);
        }

        if (tokenStarted)
            tokens.Add(current.ToString());
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            return false;

        argv = tokens;
        return true;
    }

    private static bool IsForbiddenCmdSyntax(char ch) =>
        ch is '&' or '|' or '<' or '>' or '^' or '%' or '!' or '(' or ')';

    private static bool IsCmdWhitespace(char ch) => ch is ' ' or '\t';

    private static bool IsBatchFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }
}
