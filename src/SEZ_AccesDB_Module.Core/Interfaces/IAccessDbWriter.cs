using System.Data;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// Writes data from a SQL Server IDataReader into a new .accdb file.
/// </summary>
public interface IAccessDbWriter
{
    /// <summary>
    /// Creates a new .accdb file containing <paramref name="tableName"/>
    /// and bulk-inserts all rows from <paramref name="reader"/>.
    /// Schema is obtained from <paramref name="schema"/>.
    /// </summary>
    /// <param name="filePath">Full path to the .accdb file to create.</param>
    /// <param name="tableName">Name of the table to create inside Access. Should match the file prefix.</param>
    /// <param name="reader">Open, forward-only IDataReader positioned at the first row.</param>
    /// <param name="schema">Column schema from reader.GetSchemaTable().</param>
    /// <param name="rowCount">Expected total row count (used for progress reporting).</param>
    /// <param name="onProgress">Optional: invoked periodically with current rows-written count.</param>
    /// <param name="onRowError">Callback for row-level failures (non-fatal — row is skipped).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of rows successfully written.</returns>
    Task<long> WriteChunkAsync(
        string filePath,
        string tableName,
        IDataReader reader,
        DataTable schema,
        long rowCount,
        Action<long>? onProgress = null,
        Action<long, Exception>? onRowError = null,
        CancellationToken ct = default);
}
