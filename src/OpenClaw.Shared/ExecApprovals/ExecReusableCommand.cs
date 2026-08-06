using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// A single executable whose resolved identity, reusable policy pattern, and
/// direct execution argv are derived together and cannot drift apart.
/// </summary>
public sealed class ExecReusableCommand
{
    public IReadOnlyList<string> Argv { get; }
    public ExecCommandResolution Resolution { get; }
    public string Pattern { get; }

    internal ExecReusableCommand(
        IReadOnlyList<string> argv,
        ExecCommandResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var resolvedPath = resolution.ResolvedPath;
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(resolvedPath))
            throw new ArgumentException("Reusable command requires a resolved executable.", nameof(argv));
        if (!Path.IsPathFullyQualified(resolvedPath))
            throw new ArgumentException("Reusable command executable must be fully qualified.", nameof(resolution));
        if (!string.Equals(argv[0], resolvedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Reusable command argv must start with its resolved executable.", nameof(argv));

        Argv = new ReadOnlyCollection<string>(argv.ToArray());
        Resolution = resolution;
        Pattern = resolvedPath;
    }
}
