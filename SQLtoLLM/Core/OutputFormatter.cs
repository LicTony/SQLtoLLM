using System.Text;
using SQLtoLLM.Core.Models;

namespace SQLtoLLM.Core;

public static class OutputFormatter
{
    private const string Separator = "================================================================================";

    public static string Format(IEnumerable<(string ObjectType, string ObjectName, string ContextText)> rows)
    {
        var sb = new StringBuilder();

        foreach (var row in rows)
        {
            sb.AppendLine(Separator);
            sb.AppendLine($"OBJECT TYPE : {row.ObjectType}");
            sb.AppendLine($"OBJECT NAME : {row.ObjectName}");
            sb.AppendLine(Separator);
            sb.AppendLine("CONTEXT:");
            sb.AppendLine(row.ContextText?.Trim());
            sb.AppendLine(Separator);
            sb.AppendLine();
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
