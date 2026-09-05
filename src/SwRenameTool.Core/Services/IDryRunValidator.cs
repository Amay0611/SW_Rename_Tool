using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

/// <summary>
/// リネーム実行前のDry Run検証（ファイルロック、重複名、範囲外参照の除外など）を行う。
/// </summary>
public interface IDryRunValidator
{
    DryRunResult Validate(IReadOnlyList<FileNode> nodes);
}
