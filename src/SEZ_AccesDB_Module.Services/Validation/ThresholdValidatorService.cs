using Microsoft.Extensions.Logging;
using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Services.Validation;

/// <summary>
/// Validates that SP-created split tables do not exceed the configured row threshold.
/// Runs after SP execution and before data is loaded into Access files.
/// </summary>
public class ThresholdValidatorService : IThresholdValidator
{
    private readonly ISqlDataAccess _sql;
    private readonly ILogger<ThresholdValidatorService> _logger;

    public ThresholdValidatorService(ISqlDataAccess sql, ILogger<ThresholdValidatorService> logger)
    {
        _sql = sql;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ThresholdValidationResult> ValidateAsync(
        StoredProcedureDefinition spDef,
        List<string> splitTables,
        CancellationToken ct = default)
    {
        var result = new ThresholdValidationResult
        {
            SpName = spDef.Name,
            Threshold = spDef.Threshold
        };

        _logger.LogInformation(
            "Threshold validation: SP={SP}, Threshold={Threshold:N0}, Tables={Count}",
            spDef.Name, spDef.Threshold, splitTables.Count);

        foreach (var table in splitTables)
        {
            ct.ThrowIfCancellationRequested();

            var rowCount = await _sql.GetRowCountAsync(table, ct);
            var exceeds = rowCount > spDef.Threshold;

            result.Tables.Add(new SplitTableInfo
            {
                TableName = table,
                RowCount = rowCount,
                ExceedsThreshold = exceeds
            });

            if (exceeds)
            {
                _logger.LogWarning(
                    "Threshold EXCEEDED: Table [{Table}] has {Rows:N0} rows (threshold: {Threshold:N0})",
                    table, rowCount, spDef.Threshold);
            }
            else
            {
                _logger.LogInformation(
                    "Threshold OK: Table [{Table}] has {Rows:N0} rows (threshold: {Threshold:N0})",
                    table, rowCount, spDef.Threshold);
            }
        }

        if (result.HasViolations)
        {
            _logger.LogWarning(
                "Threshold validation completed: {Violations}/{Total} table(s) exceed threshold for SP {SP}",
                result.ViolationCount, result.Tables.Count, spDef.Name);
        }
        else
        {
            _logger.LogInformation(
                "Threshold validation passed: all {Total} table(s) within threshold for SP {SP}",
                result.Tables.Count, spDef.Name);
        }

        return result;
    }
}
