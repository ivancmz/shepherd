using System.Text;
using System.Text.RegularExpressions;

namespace Shepherd.Protocol;

/// <summary>Agent names and addresses (ADR-0003): <c>name</c> or <c>name@node</c>.</summary>
public static partial class Names
{
    public const int MaxLength = 32;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$")]
    private static partial Regex NamePattern();

    public static bool IsValid(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Turns arbitrary text (folder, machine, distro) into a valid name fragment.</summary>
    public static string Sanitize(string? text, string fallback = "agent")
    {
        var sb = new StringBuilder();
        foreach (var raw in (text ?? "").ToLowerInvariant())
        {
            // InvariantGlobalization: string.Normalize cannot strip accents, so fold Latin ones by hand.
            var ch = Fold(raw);
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > MaxLength) s = s[..MaxLength].TrimEnd('-');
        return s.Length >= 2 ? s : fallback;
    }

    /// <summary>Node of the current process: <c>wsl-&lt;distro&gt;</c> inside WSL, otherwise <c>pc-&lt;machine&gt;</c>.</summary>
    public static string LocalNode()
    {
        var overrideNode = Environment.GetEnvironmentVariable("SHEPHERD_NODE");
        if (IsValid(overrideNode)) return overrideNode!;
        var distro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (!string.IsNullOrEmpty(distro)) return Truncate("wsl-" + Sanitize(distro, "linux"));
        var prefix = OperatingSystem.IsWindows() ? "pc-" : "host-";
        return Truncate(prefix + Sanitize(Environment.MachineName, "local"));
    }

    public static string Address(string name, string node) => $"{name}@{node}";

    /// <summary>Parses <c>name</c> or <c>name@node</c>; the node defaults to <paramref name="defaultNode"/>.</summary>
    public static bool TryParseAddress(string? text, string defaultNode, out string name, out string node)
    {
        name = node = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('@').ToLowerInvariant();
        var at = t.IndexOf('@');
        name = at < 0 ? t : t[..at];
        node = at < 0 ? defaultNode : t[(at + 1)..];
        return IsValid(name) && IsValid(node);
    }

    private static char Fold(char c) => c switch
    {
        'á' or 'à' or 'ä' or 'â' or 'ã' or 'å' => 'a',
        'é' or 'è' or 'ë' or 'ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' or 'õ' or 'ø' => 'o',
        'ú' or 'ù' or 'ü' or 'û' => 'u',
        'ñ' => 'n',
        'ç' => 'c',
        'ý' or 'ÿ' => 'y',
        _ => c,
    };

    private static string Truncate(string s) => s.Length > MaxLength ? s[..MaxLength].TrimEnd('-') : s;
}
