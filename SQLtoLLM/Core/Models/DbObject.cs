namespace SQLtoLLM.Core.Models;

public class DbObject
{
    public string ObjectName { get; set; } = string.Empty;
    public ObjectType? DetectedType { get; set; }
    public ObjectType? EditableType { get; set; }
    public ObjectStatus Status { get; set; } = ObjectStatus.NotFound;

    public string StatusDisplay => Status switch
    {
        ObjectStatus.Resolved => "✅ Resolved",
        ObjectStatus.Ambiguous => "⚠️ Ambiguous",
        ObjectStatus.NotFound => "❌ Not Found",
        _ => "—"
    };
}
