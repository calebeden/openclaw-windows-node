using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenClaw.Shared.Commands;

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

        if (CanonicalCmdCarrier.TryGetCanonicalPayload(command, out var payload))
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
            || !IsBindableExecutable(resolvedPath)
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

    /// <summary>
    /// Durable binding is restricted to real PE executables.
    ///
    /// PATH resolution probes every PATHEXT entry, which by default also includes
    /// .COM, .VBS, .VBE, .JS, .JSE, .WSF, .WSH, and .MSC. Those targets are all
    /// interpreted content whose meaning can change without any change to the path
    /// that was approved, so an allowlist of extensions is used here rather than a
    /// denylist of the two batch extensions.
    /// </summary>
    internal static bool IsBindableExecutable(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}
