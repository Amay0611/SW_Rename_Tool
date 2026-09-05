namespace SwRenameTool.Core.Services;

/// <summary>
/// SolidWorksのToolbox（規格品）パスをレジストリから取得し、
/// 対象ファイルがToolbox配下かどうかを判定する。
/// </summary>
public interface IToolboxDetector
{
    /// <summary>レジストリから検出したToolboxルートパス（複数登録されている場合あり）。</summary>
    IReadOnlyList<string> ToolboxRootPaths { get; }

    bool IsToolboxFile(string filePath);
}
