namespace Shepherd.Protocol;

/// <summary>
/// WSL detection. <c>WSL_DISTRO_NAME</c> is not always visible: Codex starts MCP servers with a
/// filtered environment. So the distro is also looked up in the ancestors' environment
/// (<c>/proc/&lt;pid&gt;/environ</c>, same user), and WSL itself is detected from the kernel.
/// </summary>
public static class Wsl
{
    private static readonly Lazy<string?> DistroLazy = new(DetectDistro);
    private static readonly Lazy<bool> KernelLazy = new(DetectKernel);

    /// <summary>Exact distro name (e.g. <c>Ubuntu</c>, <c>Ubuntu-24.04</c>), or null outside WSL.</summary>
    public static string? Distro => OperatingSystem.IsLinux() ? DistroLazy.Value : null;

    /// <summary>True when running inside WSL (env or kernel says so).</summary>
    public static bool IsWsl => OperatingSystem.IsLinux() && (DistroLazy.Value is not null || KernelLazy.Value);

    private static string? DetectDistro()
    {
        var env = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (!string.IsNullOrEmpty(env)) return env;
        if (!OperatingSystem.IsLinux()) return null;
        var pid = Environment.ProcessId;
        for (var depth = 0; depth < 8 && pid > 1; depth++)
        {
            try
            {
                var environ = File.ReadAllText($"/proc/{pid}/environ");
                foreach (var entry in environ.Split('\0'))
                    if (entry.StartsWith("WSL_DISTRO_NAME=", StringComparison.Ordinal) && entry.Length > 16)
                        return entry[16..];
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                pid = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static bool DetectKernel()
    {
        try
        {
            return File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
