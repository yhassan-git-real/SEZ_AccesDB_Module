using System.Data;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// Writes data from an IDataReader into an Access .accdb file.
/// The reader is shared (sequential, forward-only) across chunks — 
/// the caller must pass a reader already positioned at the start of the chunk.
/// </summary>
public interface IAccessDbWriter
{
    /// <summary>
    /// Creates a new .accdb file and bulk-inserts the next <paramref name="rowCount"/> rows
    /// from the reader (from its current position). 
    /// Schema is inferred from the reader on first call; pass it in for subsequent chunks.
    /// </summary>
    /// <param name="filePath">Full path to the .accdb file to create.</param>
    /// <param name="tableName">Name of the table to create inside Access.</param>
    /// <param name="reader">Open, forward-only IDataReader positioned at the correct row.</param>
    /// <param name="schema">Column schema (from GetSchemaTable) — read once and passed in.</param>
    /// <param name="rowCount">Maximum rows to read and write from current reader position.</param>
    /// <param name="onProgress">Optional: invoked every 1000 rows with rows-written-so-far count.</param>
    /// <param name="onRowError">Callback invoked on row-level failure (non-fatal).</param>
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
