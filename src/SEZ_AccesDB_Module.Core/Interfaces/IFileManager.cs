using SEZ_AccesDB_Module.Core.Models;
using SEZ_AccesDB_Module.Core.Configuration;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// Generates file paths and manages output file metadata.
/// </summary>
public interface IFileManager
{
    /// <summary>
    /// Generates the full output file path for single-file output.
    /// </summary>
    string GetSingleFilePath(string outputDir, string prefix, DateTime date, string extension);

    /// <summary>
    /// Generates the full output file path for a named chunk file.
    /// </summary>
    string GetChunkFilePath(string outputDir, string prefix, int chunkIndex, DateTime date, string extension);

    /// <summary>
    /// Computes a list of chunk ranges based on total row count and split config.
    /// </summary>
    List<ChunkRange> ComputeChunks(long totalRows, long threshold, long chunkSize);

    /// <summary>
    /// Ensures the output directory exists.
    /// </summary>
    void EnsureOutputDirectory(string path);
}
