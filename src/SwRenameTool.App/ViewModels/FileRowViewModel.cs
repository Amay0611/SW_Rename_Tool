using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SwRenameTool.Core.Models;

namespace SwRenameTool.App.ViewModels;

/// <summary>
/// データグリッドの1行に対応するViewModel。FileNodeをUIバインディング用にラップする。
/// </summary>
public partial class FileRowViewModel : ObservableObject
{
    public FileNode Node { get; }

    public FileRowViewModel(FileNode node)
    {
        Node = node;
        _newBaseName = node.NewBaseName ?? node.BaseName;
    }

    public string FileName => Node.FileName;
    public SwFileType FileType => Node.FileType;

    /// <summary>種別の略称表示（Assembly→Assy, Part→Part, Drawing→Draw）。</summary>
    public string FileTypeDisplay => Node.FileType switch
    {
        SwFileType.Assembly => "Assy",
        SwFileType.Part => "Part",
        SwFileType.Drawing => "Draw",
        _ => Node.FileType.ToString()
    };

    public bool IsExcluded => Node.IsExcluded;

    /// <summary>Toolbox対象外の理由、または範囲外参照ありの情報表示。
    /// 後者はリネーム自体は可能で、成功後に参照元アセンブリの参照を自動更新する対象であることを示す。</summary>
    public string ExclusionReason =>
        Node.IsToolboxPart ? "Toolbox部品" :
        Node.IsReferencedFromOutsideScope ? "外部参照あり" : string.Empty;

    public int ReferenceCount => Node.Parents.Count;

    public bool IsLinkedDrawingRow => Node.FileType == SwFileType.Drawing;

    /// <summary>ツリー表示時の階層の深さ（0=最上位アセンブリ）。MainViewModelが並び替え時に設定する。</summary>
    [ObservableProperty]
    private int _indentLevel;

    /// <summary>IndentLevelに応じた左マージン。「変更前」列の表示インデントに使う。</summary>
    public Thickness IndentMargin => new(IndentLevel * 16, 0, 0, 0);

    partial void OnIndentLevelChanged(int value) => OnPropertyChanged(nameof(IndentMargin));

    /// <summary>サムネイル（小、一覧表示用）。非同期・遅延ロードで後から設定される。未取得の間はnull。</summary>
    [ObservableProperty]
    private BitmapSource? _thumbnail;

    /// <summary>サムネイル（大、拡大プレビューパネル用）。選択されたときに初めて取得する。</summary>
    [ObservableProperty]
    private BitmapSource? _largeThumbnail;

    /// <summary>ユーザーがこの行の変更後ファイル名を編集したときに発火する。
    /// MainViewModelがこれを購読し、紐付いた図面への自動追従を行う。</summary>
    public event EventHandler? NewBaseNameEdited;

    [ObservableProperty]
    private string _newBaseName;

    partial void OnNewBaseNameChanged(string value)
    {
        Node.NewBaseName = value;
        NewBaseNameEdited?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    private bool _isNameSyncLocked;

    partial void OnIsNameSyncLockedChanged(bool value)
    {
        Node.IsNameSyncOverridden = value;
    }

    /// <summary>
    /// 図面の自動追従用に、コード側からNewBaseNameを更新する（イベント再発火なし）。
    /// ユーザー入力によるOnNewBaseNameChangedと区別するため直接フィールドを操作する。
    /// </summary>
    public void ApplySyncedName(string baseName)
    {
        NewBaseName = baseName; // Node.NewBaseNameも連動して更新される
    }
}
