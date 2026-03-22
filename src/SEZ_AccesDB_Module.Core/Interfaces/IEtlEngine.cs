using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// High-level ETL engine: executes a stored procedure and exports data to Access files.
/// </summary>
public interface IEtlEngine
{
    Task<ExecutionResult> RunAsync(Models.ExecutionContext context, CancellationToken ct = default);
}
