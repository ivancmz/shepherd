using System.Text.RegularExpressions;

namespace Shepherd.Broker;

/// <summary>
/// Translates file references between Windows and WSL nodes (ADR-0006):
/// <c>C:\x\y</c> ↔ <c>/mnt/c/x/y</c>, and Linux-only paths → <c>\\wsl.localhost\&lt;distro&gt;\…</c>.
/// Paths between different PCs are left untouched (federation is a later phase).
/// </summary>
public static partial class PathMap
{
    [GeneratedRegex(@"^([A-Za-z]):[\\/](.*)$")]
    private static partial Regex WindowsDrive();

    [GeneratedRegex(@"^\\\\wsl(?:\.localhost|\$)\\([^\\]+)\\?(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex WslUnc();

    [GeneratedRegex("^/mnt/([a-z])(?:/(.*))?$")]
    private static partial Regex MntDrive();

    public static bool IsWslNode(string node) => node.StartsWith("wsl-", StringComparison.Ordinal);

    public static string Translate(string path, string fromNode, string? fromDistro, string toNode, string? toDistro)
    {
        if (fromNode == toNode || string.IsNullOrWhiteSpace(path)) return path;
        var fromWsl = IsWslNode(fromNode);
        var toWsl = IsWslNode(toNode);

        if (!fromWsl && toWsl)
        {
            if (WindowsDrive().Match(path) is { Success: true } d)
                return $"/mnt/{char.ToLowerInvariant(d.Groups[1].Value[0])}/{d.Groups[2].Value.Replace('\\', '/')}".TrimEnd('/');
            if (WslUnc().Match(path) is { Success: true } u &&
                (toDistro is null || u.Groups[1].Value.Equals(toDistro, StringComparison.OrdinalIgnoreCase)))
                return "/" + u.Groups[2].Value.Replace('\\', '/');
            return path;
        }

        if (fromWsl && !toWsl)
        {
            if (MntDrive().Match(path) is { Success: true } m)
                return $"{char.ToUpperInvariant(m.Groups[1].Value[0])}:\\{m.Groups[2].Value.Replace('/', '\\')}";
            if (path.StartsWith('/') && fromDistro is not null)
                return $@"\\wsl.localhost\{fromDistro}{path.Replace('/', '\\')}";
            return path;
        }

        // WSL ↔ WSL (another distro) and PC ↔ PC: /mnt/c paths are shared; anything else is left as is.
        return path;
    }
}
