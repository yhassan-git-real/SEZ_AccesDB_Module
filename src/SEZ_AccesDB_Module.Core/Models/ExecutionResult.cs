namespace SEZ_AccesDB_Module.Core.Models;

public record OutputFileDetail(string Path, string TableName, long Rows, long TableSizeBytes, long FileSizeBytes);

/// <summary>
/// Captures the outcome of an ETL execution run.
/// </summary>
public class ExecutionResult
{
    public bool    Success          { get; set; }
    public long    TotalRowsRead    { get; set; }
    public int     TotalFilesCreated { get; set; }
    public int     SuccessFileCount  { get; set; }
    public int     ErrorFileCount    { get; set; }
    public List<OutputFileDetail> OutputFiles { get; set; } = new();
    public TimeSpan Duration        { get; set; }
    public string? ErrorMessage     { get; set; }
    public List<string> Warnings    { get; set; } = new();

    // ── Phase timings ──────────────────────────────────────────────────────────
    /// <summary>Time spent executing the stored procedure.</summary>
    public TimeSpan SpExecutionTime { get; set; }

    /// <summary>Time spent reading rows from SQL Server + writing to Access files.</summary>
    public TimeSpan WriteTime       { get; set; }

    // ── Derived ────────────────────────────────────────────────────────────────
    public string FileNamesSummary => string.Join("; ", OutputFiles.Select(f => Path.GetFileName(f.Path)));

    public long TotalFileSizeBytes => OutputFiles.Sum(f => f.FileSizeBytes);
    public long TotalTableSizeBytes => OutputFiles.Sum(f => f.TableSizeBytes);

    /// <summary>Overall rows written per second (across all files).</summary>
    public double RowsPerSecond => WriteTime.TotalSeconds > 0
        ? TotalRowsRead / WriteTime.TotalSeconds
        : 0;

    /// <summary>Duration formatted as "Xm Ys" (>= 1 min) or "X.Xs" (< 1 min).</summary>
    public string DurationFormatted => Duration.TotalMinutes >= 1
        ? $"{(int)Duration.TotalMinutes}m {Duration.Seconds:D2}s"
        : $"{Duration.TotalSeconds:F1}s";

    public string TotalFileSizeBytesFormatted => FormatBytes(TotalFileSizeBytes);
    public string TotalTableSizeBytesFormatted => FormatBytes(TotalTableSizeBytes);

    public static string FormatBytes(long bytes)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
        if (bytes == 0) return "0 B";
        long bytesAbs = Math.Abs(bytes);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytesAbs, 1024)));
        double num = Math.Round(bytesAbs / Math.Pow(1024, place), 2);
        return $"{(Math.Sign(bytes) * num)} {suf[place]}";
    }
}
