using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// Validates split table row counts against the configured threshold
/// after stored procedure execution and before data loading into Access.
/// </summary>
public interface IThresholdValidator
{
    /// <summary>
    /// Queries row counts for each split table and evaluates them
    /// against the threshold defined in the SP configuration.
    /// </summary>
    /// <param name="spDef">Stored procedure definition containing the threshold.</param>
    /// <param name="splitTables">List of split table names to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result with per-table status.</returns>
    Task<ThresholdValidationResult> ValidateAsync(
        StoredProcedureDefinition spDef,
        List<string> splitTables,
        CancellationToken ct = default);
}
