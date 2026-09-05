using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

/// <summary>
/// 実行ログをCSVで出力する。実行ごとに新規ファイル。
/// バックアップフォルダと同じ場所に書き込むため、書き込み先はコンストラクタ固定ではなく
/// 呼び出し時に渡す（実機検証で、固定フォルダ設定のミスによりログが意図しない場所
/// [アプリのカレントディレクトリ] に出力されてしまう問題が見つかったため、この形にした）。
/// </summary>
public interface IRenameLogger
{
    Task WriteAsync(IReadOnlyList<RenameLogEntry> entries, string logFolder, CancellationToken ct = default);
}
