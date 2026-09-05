using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed record RenameProgress(int Completed, int Total, string CurrentFileName);

/// <summary>
/// 末端パーツ→サブアセンブリ→上位アセンブリ→図面の順でリネームを実行する。
/// IPackAndGoを使い、SolidWorksをバックグラウンド起動した状態で
/// ファイル名変更と参照パスの書き換えを一括で行う。
/// </summary>
public interface IRenameExecutionService
{
    /// <summary>
    /// リネームを実行する。安全なタイミング（Pack and Go実行前まで）でのみキャンセルを受け付ける。
    /// 失敗した場合はその時点で中断し、例外に処理結果を含めて返す。
    /// </summary>
    /// <param name="topAssemblyPath">
    /// ユーザーが選択した最上位アセンブリのフルパス。
    /// 「Parentsが0件のノード」から推測する方式は、選択アセンブリ自身が外部アセンブリからの
    /// 参照を持つケース（範囲外の親アセンブリの参照更新機能）と衝突するため、
    /// 呼び出し元から明示的に渡す。
    /// </param>
    /// <param name="logFolder">実行ログCSVの出力先（通常はバックアップフォルダと同じ場所）。</param>
    Task ExecuteAsync(
        IReadOnlyList<FileNode> nodes,
        string topAssemblyPath,
        string logFolder,
        IProgress<RenameProgress> progress,
        CancellationToken ct = default);
}
