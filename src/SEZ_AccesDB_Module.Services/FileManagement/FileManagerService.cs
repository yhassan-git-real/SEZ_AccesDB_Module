using SEZ_AccesDB_Module.Core.Interfaces;

namespace SEZ_AccesDB_Module.Services.FileManagement;

/// <summary>
/// Implements file naming and output directory management.
/// Naming conventions:
///   Single file : {prefix}_{ddMMyyyy}.accdb
///   Multi  file : {prefix}_{N}_{ddMMyyyy}.accdb
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
    public void EnsureOutputDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
