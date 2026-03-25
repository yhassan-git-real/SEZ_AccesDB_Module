
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
    /// Ensures the output directory exists.
    /// </summary>
    void EnsureOutputDirectory(string path);
}
