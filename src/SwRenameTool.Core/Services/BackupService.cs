using System.IO;
using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed class BackupService : IBackupService
{
    public async Task<BackupResult> CreateBackupAsync(
        IReadOnlyList<FileNode> nodes, string targetFolder, CancellationToken ct = default)
    {
        // バックアップ対象 = リネーム対象ファイル本体 + それらを参照している親（アセンブリ・図面）
        var targets = nodes.Where(n => n.HasPendingRename).ToList();
        var affectedParents = targets.SelectMany(n => n.Parents).Distinct();
        var toBackUp = targets
            .Concat(affectedParents)
            .Concat(targets.Where(n => n.LinkedDrawing != null).Select(n => n.LinkedDrawing!))
            .DistinctBy(n => n.FilePath)
            .ToList();

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var backupFolder = Path.Combine(targetFolder, "_backup", stamp);
        Directory.CreateDirectory(backupFolder);

        long expectedBytes = 0;
        long copiedBytes = 0;

        foreach (var node in toBackUp)
        {
            ct.ThrowIfCancellationRequested();

            var destPath = Path.Combine(backupFolder, node.FileName);
            expectedBytes += new FileInfo(node.FilePath).Length;

            await using (var src = File.OpenRead(node.FilePath))
            await using (var dst = File.Create(destPath))
            {
                await src.CopyToAsync(dst, ct);
            }

            copiedBytes += new FileInfo(destPath).Length;
        }

        var integrityOk = expectedBytes == copiedBytes;

        await WriteRestoreGuideAsync(backupFolder, toBackUp, ct);

        return new BackupResult(backupFolder, toBackUp.Count, integrityOk);
    }

    private static async Task WriteRestoreGuideAsync(
        string backupFolder, IReadOnlyList<FileNode> files, CancellationToken ct)
    {
        var lines = new List<string>
        {
            "# 元に戻す手順",
            "",
            "このフォルダ内のファイルを、元のフォルダへ上書きコピーしてください。",
            "リネームは手動ロールバック運用のため、自動復元は行われません。",
            "",
            "対象ファイル一覧:"
        };

        lines.AddRange(files.Select(f => $"- {f.FilePath}"));

        var guidePath = Path.Combine(backupFolder, "元に戻す手順.txt");
        await File.WriteAllLinesAsync(guidePath, lines, ct);
    }
}
