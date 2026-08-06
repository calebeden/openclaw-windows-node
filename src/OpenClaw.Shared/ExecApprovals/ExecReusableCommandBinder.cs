using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenClaw.Shared.Commands;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Derives the single identity that is eligible for durable allowlist authorization.
///
/// Two properties matter here and are easy to conflate. The identity is what the
/// operator sees and what durable policy describes; the transport is what actually
/// runs. For a canonical cmd carrier those differ, and the binder is what keeps them
/// consistent: it looks through the carrier for identity while preserving the carrier
/// for execution.
/// </summary>
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

    /// <summary>
    /// Why a command could not be bound to a durable identity. The command may still
    /// be approved as a one-time operation; this only explains why no reusable rule
    /// is offered, so the reason can be surfaced instead of a silent null.
    /// </summary>
    internal enum BindFailure
    {
        None = 0,
        EmptyCommand,
        NonCanonicalCmdCarrier,
        UntrustedCarrierImage,
        CarrierPayloadNotStatic,
        CarrierPayloadIsBuiltin,
        ShellWrapper,
        EnvWrapperHasModifiers,
        ExecutableNotResolved,
        ExecutableNotFound,
        ExecutableNotBindable,
        ExecutableOnNetworkPath,
        ArgumentContainsNul,
        CarrierPayloadExecutableAmbiguous,
    }

    internal static ExecReusableCommand? TryBind(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
        => TryBind(command, cwd, env, out _);

    internal static ExecReusableCommand? TryBind(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        out BindFailure failure)
    {
        failure = BindFailure.None;
        if (command.Count == 0)
        {
            failure = BindFailure.EmptyCommand;
            return null;
        }

        if (CanonicalCmdCarrier.IsCmdExecutable(command[0]))
        {
            if (!CanonicalCmdCarrier.TryGetTrustedCanonicalPayload(command, out var payload))
            {
                // Distinguish the two ways a cmd-shaped argv can fail so the operator
                // and the logs can tell "we do not understand this shape" apart from
                // "this cmd image is not the system one".
                failure = CanonicalCmdCarrier.TryGetCanonicalPayload(command, out _)
                    ? BindFailure.UntrustedCarrierImage
                    : BindFailure.NonCanonicalCmdCarrier;
                return null;
            }

            if (!TryTokenizeStaticCmdPayload(payload, out var payloadArgv))
            {
                failure = BindFailure.CarrierPayloadNotStatic;
                return null;
            }
            if (payloadArgv.Count == 0
                || ExecCommandToken.IsEnv(payloadArgv[0])
                || s_cmdBuiltins.Contains(ExecCommandToken.NormalizedBasename(payloadArgv[0])))
            {
                failure = BindFailure.CarrierPayloadIsBuiltin;
                return null;
            }

            // The carrier is preserved verbatim for transport, so cmd.exe re-resolves the
            // payload executable at launch and it searches the current directory before
            // PATH. Our resolver never searches the current directory, so a cwd-local file
            // of the same name means we would authorize one binary and run another. Refuse
            // the durable binding rather than resolve it two different ways.
            if (ExecCommandResolver.HasCurrentDirectoryCandidate(payloadArgv[0], cwd, env))
            {
                failure = BindFailure.CarrierPayloadExecutableAmbiguous;
                return null;
            }

            // Identity looks through the carrier; transport stays the original argv,
            // except that argv[0] is pinned to the resolved system cmd.exe so Windows
            // cannot re-resolve a bare "cmd.exe" against PATH at launch time.
            var carrierPath = CanonicalCmdCarrier.ResolveTrustedCarrierPath(command[0]);
            if (carrierPath is null)
            {
                failure = BindFailure.UntrustedCarrierImage;
                return null;
            }
            var executionArgv = new string[command.Count];
            executionArgv[0] = carrierPath;
            for (var i = 1; i < command.Count; i++)
                executionArgv[i] = command[i];

            return BindDirect(payloadArgv, cwd, env, executionArgv, out failure);
        }

        if (ExecShellWrapperNormalizer.Extract(command).IsWrapper)
        {
            failure = BindFailure.ShellWrapper;
            return null;
        }

        return BindDirect(command, cwd, env, executionArgv: null, out failure);
    }

    private static ExecReusableCommand? BindDirect(
        IReadOnlyList<string> argv,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyList<string>? executionArgv,
        out BindFailure failure)
    {
        failure = BindFailure.None;
        if (argv.Count == 0)
        {
            failure = BindFailure.EmptyCommand;
            return null;
        }

        // NUL is the argument separator in the persisted argPattern, so an argument
        // containing one is ambiguous: "a\0b" renders identically to the two arguments
        // "a","b" and would let a stored rule match a differently segmented argv. It is
        // also not representable in a Windows command line, so rejecting is fail-closed.
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i].IndexOf('\0') >= 0)
            {
                failure = BindFailure.ArgumentContainsNul;
                return null;
            }
        }

        if (ExecEnvInvocationUnwrapper.AnyWrapperHasModifiers(argv))
        {
            failure = BindFailure.EnvWrapperHasModifiers;
            return null;
        }
        var effectiveArgv = ExecEnvInvocationUnwrapper.UnwrapForResolution(argv);
        if (effectiveArgv.Count == 0
            || ExecCommandToken.IsEnv(effectiveArgv[0]))
        {
            failure = BindFailure.ExecutableNotResolved;
            return null;
        }

        var resolution = ExecCommandResolver.Resolve(effectiveArgv, cwd, env);
        var resolvedPath = resolution?.ResolvedPath;
        if (resolution is null
            || string.IsNullOrWhiteSpace(resolvedPath)
            || !Path.IsPathFullyQualified(resolvedPath))
        {
            failure = BindFailure.ExecutableNotResolved;
            return null;
        }
        if (IsNetworkPath(resolvedPath))
        {
            failure = BindFailure.ExecutableOnNetworkPath;
            return null;
        }
        if (!File.Exists(resolvedPath))
        {
            failure = BindFailure.ExecutableNotFound;
            return null;
        }
        if (!IsBindableExecutable(resolvedPath))
        {
            failure = BindFailure.ExecutableNotBindable;
            return null;
        }

        var boundArgv = new string[effectiveArgv.Count];
        boundArgv[0] = resolvedPath;
        for (var i = 1; i < effectiveArgv.Count; i++)
            boundArgv[i] = effectiveArgv[i];

        return new ExecReusableCommand(boundArgv, resolution.Value, executionArgv);
    }

    /// <summary>
    /// True for a UNC path or a path on a network-mapped drive.
    ///
    /// A durable rule records a path, and the content behind a network path is
    /// controlled by whoever serves the share rather than by the local machine, so a
    /// remote executable is never eligible for reuse. Allow-once still works.
    /// </summary>
    internal static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            return false;

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            // An unavailable drive cannot be shown to be local, so refuse durable reuse.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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

    /// <summary>Stable diagnostic token for logs and prompts.</summary>
    internal static string DescribeFailure(BindFailure failure) => failure switch
    {
        BindFailure.None => "bound",
        BindFailure.EmptyCommand => "empty-command",
        BindFailure.NonCanonicalCmdCarrier => "non-canonical-cmd-carrier",
        BindFailure.UntrustedCarrierImage => "untrusted-cmd-carrier-image",
        BindFailure.CarrierPayloadNotStatic => "carrier-payload-not-static",
        BindFailure.CarrierPayloadIsBuiltin => "carrier-payload-is-shell-builtin",
        BindFailure.ShellWrapper => "shell-wrapper",
        BindFailure.EnvWrapperHasModifiers => "env-wrapper-has-modifiers",
        BindFailure.ExecutableNotResolved => "executable-not-resolved",
        BindFailure.ExecutableNotFound => "executable-not-found",
        BindFailure.ExecutableNotBindable => "executable-not-bindable",
        BindFailure.ExecutableOnNetworkPath => "executable-on-network-path",
        BindFailure.ArgumentContainsNul => "argument-contains-nul",
        BindFailure.CarrierPayloadExecutableAmbiguous => "carrier-payload-executable-ambiguous",
        _ => "unknown",
    };
}
