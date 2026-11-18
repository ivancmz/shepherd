using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Shepherd.Broker;

/// <summary>Append-only monthly JSONL audit trail (ADR-0004). Full content: these are the user's own agents.</summary>
internal sealed class AuditLog
{
    private readonly string _dir;
    private readonly Lock _lock = new();

    public AuditLog(string dir) => _dir = dir;

    public void Write(string evt, JsonObject data)
    {
        data["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        data["event"] = evt;
        var path = Path.Combine(_dir, $"audit-{DateTime.UtcNow:yyyy-MM}.jsonl");
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, data.ToJsonString() + "\n", NoBom);
            }
            catch (IOException)
            {
                // Auditing must never break message delivery.
            }
        }
    }

    // Encoding.UTF8 writes a BOM at the start of a new file, which breaks JSONL readers.
    private static readonly UTF8Encoding NoBom = new(false);

    /// <summary>
    /// Deletes each monthly file once its whole month is older than <paramref name="retention"/>
    /// (ADR-0011). Returns how many were deleted.
    /// </summary>
    public int Purge(TimeSpan retention)
    {
        var cut = DateTime.UtcNow - retention;
        var deleted = 0;
        lock (_lock)
        {
            foreach (var file in Directory.GetFiles(_dir, "audit-*.jsonl"))
            {
                var month = Path.GetFileNameWithoutExtension(file)["audit-".Length..];
                if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)) continue;
                if (start.AddMonths(1) > cut) continue;
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try again at the next maintenance run.
                }
            }
        }
        return deleted;
    }
}
