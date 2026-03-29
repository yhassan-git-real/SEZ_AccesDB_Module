namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Row count and threshold status for a single split table.
/// </summary>
public class SplitTableInfo
{
    public string TableName { get; set; } = string.Empty;
    public long RowCount { get; set; }
    public bool ExceedsThreshold { get; set; }
}

/// <summary>
/// Aggregated result of threshold validation across all split tables
/// produced by a stored procedure execution.
/// </summary>
public class ThresholdValidationResult
{
    public string SpName { get; set; } = string.Empty;
    public long Threshold { get; set; }
    public List<SplitTableInfo> Tables { get; set; } = new();

    /// <summary>True if at least one split table exceeds the threshold.</summary>
    public bool HasViolations => Tables.Any(t => t.ExceedsThreshold);

    /// <summary>Count of tables that exceed the threshold.</summary>
    public int ViolationCount => Tables.Count(t => t.ExceedsThreshold);
}
