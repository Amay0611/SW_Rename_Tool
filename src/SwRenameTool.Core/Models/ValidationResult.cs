namespace SwRenameTool.Core.Models;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Dry Runで検出した1件の検証結果（ファイルロック、範囲外参照、重複名など）。
/// </summary>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Message,
    FileNode? RelatedFile = null);

/// <summary>
/// Dry Runフェーズ全体の結果。
/// </summary>
public sealed class DryRunResult
{
    public List<ValidationIssue> Issues { get; } = new();

    public int TargetCount { get; init; }
    public int WarningCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning);
    public int ErrorCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);

    /// <summary>エラーが0件であれば実行に進める（警告は確認チェックボックスで許容）。</summary>
    public bool CanProceed => ErrorCount == 0;
}
