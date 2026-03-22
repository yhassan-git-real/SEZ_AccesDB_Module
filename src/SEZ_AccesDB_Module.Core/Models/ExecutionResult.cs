namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Captures the outcome of an ETL execution run.
/// </summary>
public class ExecutionResult
{
    public bool Success { get; set; }
    public long TotalRowsRead { get; set; }
    public int TotalFilesCreated { get; set; }
    public int SuccessFileCount { get; set; }
    public int ErrorFileCount { get; set; }
    public List<string> OutputFilePaths { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();

    public string FileNamesSummary => string.Join("; ", OutputFilePaths.Select(Path.GetFileName));
}
