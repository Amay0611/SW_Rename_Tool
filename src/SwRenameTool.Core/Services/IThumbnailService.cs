using System.Windows.Media.Imaging;

namespace SwRenameTool.Core.Services;

/// <summary>
/// SolidWorksファイルのサムネイルを取得する。
///
/// 当初の設計ではSwDM APIのGetPreviewBitmapを想定していたが、SwDM API自体を開発者キーが
/// 取得できず断念した経緯があるため、代わりにWindows Shell API（IShellItemImageFactory）を
/// 使う。SolidWorksはエクスプローラー向けにサムネイルハンドラーを登録しているため、
/// この方式でSolidWorksを起動せずにサムネイルを取得できる。
///
/// 呼び出しはUIスレッド（STA）から行うこと。Shell APIの一部はSTAアパートメントを前提とする。
/// </summary>
public interface IThumbnailService
{
    /// <summary>
    /// 指定ファイルのサムネイルを取得する。取得できない場合はnullを返す（例外は投げない）。
    /// </summary>
    Task<BitmapSource?> GetThumbnailAsync(string filePath, int size = 64);
}
