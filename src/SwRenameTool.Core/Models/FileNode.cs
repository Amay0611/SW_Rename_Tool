using System.IO;

namespace SwRenameTool.Core.Models;

/// <summary>
/// 参照グラフ上の1ファイルを表すノード。
/// アセンブリ選択を起点にGetDependencies2で辿った範囲のみが対象となる。
/// </summary>
public sealed class FileNode
{
    /// <summary>現在のフルパス。</summary>
    public required string FilePath { get; set; }

    public required SwFileType FileType { get; init; }

    /// <summary>このノードを参照している親（複数アセンブリから共有される場合は複数）。</summary>
    public List<FileNode> Parents { get; } = new();

    /// <summary>このノードが参照している子。</summary>
    public List<FileNode> Children { get; } = new();

    /// <summary>Toolbox（規格品）ファイルか。true の場合はリネーム対象外。</summary>
    public bool IsToolboxPart { get; set; }

    /// <summary>選択したアセンブリの依存関係範囲外からも参照されているか。
    /// リネーム自体は可能。true の場合、リネーム成功後にこの参照元（Parentsに含まれる
    /// 範囲外のアセンブリ）の参照を自動更新する対象になる（UI上は情報表示のみに使う）。</summary>
    public bool IsReferencedFromOutsideScope { get; set; }

    /// <summary>同名の図面（sldprt/sldasmに対応するslddrw）。</summary>
    public FileNode? LinkedDrawing { get; set; }

    /// <summary>ユーザーが入力した新ファイル名（拡張子なし）。未入力ならnull。</summary>
    public string? NewBaseName { get; set; }

    public string FileName => Path.GetFileName(FilePath);
    public string BaseName => Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>リネーム対象外（Toolbox）かどうか。
    /// 範囲外参照は、リネーム成功後に参照元を自動更新することで対応するため、
    /// 除外理由には含めない。</summary>
    public bool IsExcluded => IsToolboxPart;

    /// <summary>実際にリネームが指定されているか。</summary>
    public bool HasPendingRename =>
        !IsExcluded &&
        !string.IsNullOrWhiteSpace(NewBaseName) &&
        !string.Equals(NewBaseName, BaseName, StringComparison.OrdinalIgnoreCase);
}
