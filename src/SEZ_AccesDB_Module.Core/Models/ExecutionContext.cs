using SEZ_AccesDB_Module.Core.Configuration;

namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Carries all runtime context for a single ETL execution run.
/// </summary>
public class ExecutionContext
{
    public string ProcessId { get; set; } = GenerateId();
    public DateTime StartTime { get; set; } = DateTime.Now;
    public StoredProcedureDefinition SpDefinition { get; set; } = null!;
    public List<SpParameter> Parameters { get; set; } = new();
    public string OutputFilePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated string of all parameters for logging/audit.
    /// </summary>
    public string ParameterSummary => string.Join(", ", Parameters.Select(p => p.ToString()));

    /// <summary>
    /// Generates a short 10-char alphanumeric Process ID, e.g. "SEZ4A2F9B1".
    /// Format: SEZ + 2-char base-36 minute-of-day + 5 random uppercase alphanum chars.
    /// </summary>
    private static string GenerateId()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
        var rng    = Random.Shared;
        var stamp  = DateTime.Now.ToString("HHmm");          // e.g. "0312"
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        return $"SEZ{stamp}{suffix}";
    }
}
