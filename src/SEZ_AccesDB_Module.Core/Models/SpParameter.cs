namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Represents a collected user-supplied SP parameter (name and integer value).
/// </summary>
public class SpParameter
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }

    public SpParameter() { }

    public SpParameter(string name, int value)
    {
        Name = name;
        Value = value;
    }

    public override string ToString() => $"@{Name}={Value}";
}
