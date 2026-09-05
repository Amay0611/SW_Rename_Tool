using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed record BackupResult(string BackupFolderPath, int FileCount, bool IntegrityOk);

/// <summary>
/// リネーム対象ファイルとその参照元（親）のみを差分バックアップする（Zip化なし、フォルダコピー）。
/// </summary>
public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(IReadOnlyList<FileNode> nodes, string targetFolder, CancellationToken ct = default);
}
