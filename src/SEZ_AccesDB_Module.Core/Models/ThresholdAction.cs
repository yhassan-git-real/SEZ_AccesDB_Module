namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Represents the user's chosen action when split tables exceed the defined threshold.
/// </summary>
public enum ThresholdAction
{
    /// <summary>Process all split tables regardless of threshold violations.</summary>
    ProcessAll = 1,

    /// <summary>Process only tables whose row count is within the threshold.</summary>
    ProcessValidOnly = 2,

    /// <summary>Cancel the entire ETL operation.</summary>
    CancelOperation = 3
}
