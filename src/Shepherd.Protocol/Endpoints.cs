namespace Shepherd.Protocol;

/// <summary>Where the broker lives on this machine (ADR-0004).</summary>
public static class Endpoints
{
    public const int ProtocolVersion = 1;

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
}
