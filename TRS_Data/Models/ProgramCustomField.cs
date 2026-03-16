namespace TRS_Data.Models;
public partial class ProgramCustomField
{
    public int CustomFieldId { get; set; }
    public int ProgramId { get; set; }
    public string Label { get; set; } = null!;
    public string FieldType { get; set; } = "text";   // text | select | checkbox
    public bool IsRequired { get; set; }
    public string? Options { get; set; }
    public int SortOrder { get; set; }
    public virtual TrsProgram Program { get; set; } = null!;
}
