using System.IO;
using System.Text;
using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed class RenameLogger : IRenameLogger
{
    public async Task WriteAsync(IReadOnlyList<RenameLogEntry> entries, string logFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(logFolder);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(logFolder, $"rename_log_{stamp}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,OldPath,NewPath,Outcome,ErrorMessage");

        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(",",
                e.Timestamp.ToString("O"),
                Csv(e.OldPath),
                Csv(e.NewPath),
                e.Outcome.ToString(),
                Csv(e.ErrorMessage ?? string.Empty)));
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
