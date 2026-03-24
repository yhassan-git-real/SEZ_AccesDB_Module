using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Services.FileManagement;

/// <summary>
/// Implements file naming, chunk computation, and output directory management.
/// Naming conventions (from Requirement.md §4.2):
///   Single file : {prefix}_{ddMMyyy}.accdb
///   Multi  file : {prefix}_{N}_{ddMMyyy}.accdb
/// </summary>
public class FileManagerService : IFileManager
{
    /// <inheritdoc/>
    public string GetSingleFilePath(string outputDir, string prefix, DateTime date, string extension)
    {
        var datePart = date.ToString("ddMMyyyy");
        var fileName = $"{prefix}_{datePart}{extension}";
        return Path.Combine(outputDir, fileName);
    }

    /// <inheritdoc/>
    public string GetChunkFilePath(string outputDir, string prefix, int chunkIndex, DateTime date, string extension)
    {
        var datePart = date.ToString("ddMMyyyy");
        var fileName = $"{prefix}_{chunkIndex}_{datePart}{extension}";
        return Path.Combine(outputDir, fileName);
    }

    /// <inheritdoc/>
    public List<ChunkRange> ComputeChunks(long totalRows, long threshold, long chunkSize)
    {
        var chunks = new List<ChunkRange>();
        if (totalRows == 0) return chunks;

        if (totalRows <= threshold)
        {
            chunks.Add(new ChunkRange { FileIndex = 1, StartRow = 0, EndRow = totalRows - 1 });
        }
        else
        {
            long remaining = totalRows;
            long currentStart = 0;
            int fileIndex = 1;

            while (remaining > 0)
            {
                long count = Math.Min(chunkSize, remaining);
                chunks.Add(new ChunkRange
                {
                    FileIndex = fileIndex++,
                    StartRow  = currentStart,
                    EndRow    = currentStart + count - 1
                });
                currentStart += count;
                remaining    -= count;
            }
        }

        return chunks;
    }

    /// <inheritdoc/>
    public void EnsureOutputDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
