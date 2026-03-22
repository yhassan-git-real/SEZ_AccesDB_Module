namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Describes row offsets for one Access file chunk.
/// </summary>
public class ChunkRange
{
    public int FileIndex { get; set; }       // 1-based index
    public long StartRow { get; set; }        // 0-based offset
    public long EndRow { get; set; }          // inclusive
    public long RowCount => EndRow - StartRow + 1;
}
