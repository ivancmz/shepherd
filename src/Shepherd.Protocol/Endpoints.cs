namespace Shepherd.Protocol;

/// <summary>Where the broker lives on this machine (ADR-0004).</summary>
public static class Endpoints
{
    public const int ProtocolVersion = 1;

    /// <summary>Default TCP port for WSL nodes (ADR-0001). <c>SHEPHERD_TCP=addr:port</c> overrides.</summary>
    public const int DefaultTcpPort = 47321;
    public const string EndpointFileName = "wsl-endpoint.json";
    public const string TokenFileName = "node-token";

    /// <summary>True inside WSL: the shim talks TCP to the Windows broker instead of a local pipe.</summary>
    public static bool IsWsl => Wsl.IsWsl;

    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(45);

    /// <summary><c>SHEPHERD_HOME</c>, or <c>%LOCALAPPDATA%\shepherd</c> / <c>~/.local/share/shepherd</c>.</summary>
    public static string DataDir()
    {
        var home = Environment.GetEnvironmentVariable("SHEPHERD_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return home;
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, "shepherd");
    }

    /// <summary>Per-user pipe name. <c>SHEPHERD_PIPE</c> overrides it (tests use random names).</summary>
    public static string PipeName()
    {
        var p = Environment.GetEnvironmentVariable("SHEPHERD_PIPE");
        return string.IsNullOrWhiteSpace(p) ? "shepherd-" + Names.Sanitize(Environment.UserName, "user") : p;
    }

    /// <summary>Name shown for the human user (ADR-0006). <c>SHEPHERD_USER</c> overrides the OS user name.</summary>
    public static string HumanName()
    {
        var u = Environment.GetEnvironmentVariable("SHEPHERD_USER");
        return Names.IsValid(u) ? u! : Names.Sanitize(Environment.UserName, "user");
    }

    /// <summary>
    /// From WSL: the Windows broker's data dir as a Linux path (it holds the endpoint and the token).
    /// <c>SHEPHERD_WIN_HOME</c>, else <c>/mnt/c/Users/&lt;user&gt;/AppData/Local/shepherd</c> for the
    /// Linux user name, else the only Windows profile that has one.
    /// </summary>
    public static string? WindowsDataDirFromWsl()
    {
        var env = Environment.GetEnvironmentVariable("SHEPHERD_WIN_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        const string users = "/mnt/c/Users";
        var mine = Path.Combine(users, Environment.UserName, "AppData", "Local", "shepherd");
        if (File.Exists(Path.Combine(mine, EndpointFileName))) return mine;
        try
        {
            var found = Directory.GetDirectories(users)
                .Select(d => Path.Combine(d, "AppData", "Local", "shepherd"))
                .Where(d => File.Exists(Path.Combine(d, EndpointFileName)))
                .ToList();
            return found.Count == 1 ? found[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
