namespace SwRenameTool.Core.Models;

public enum RenameOutcome
{
    Success,
    Failed,
    Skipped
}

/// <summary>
/// リネーム実行ログの1行分。CSVとしてバックアップフォルダ内に出力する。
/// </summary>
public sealed record RenameLogEntry(
    DateTimeOffset Timestamp,
    string OldPath,
    string NewPath,
    RenameOutcome Outcome,
    string? ErrorMessage = null);
