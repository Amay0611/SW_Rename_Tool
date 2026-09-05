using SolidWorks.Interop.sldworks;

namespace SwRenameTool.Core.Services;

/// <summary>
/// SolidWorksをバックグラウンド起動し、ISldWorksインスタンスを管理する。
///
/// 重要: SolidWorksへのCOM呼び出しはすべて単一の専用STAスレッドから行う必要がある。
/// 複数のThreadPoolスレッド（Task.Runのたびに別スレッドになりうる）から交互に
/// 呼び出すと、WPFアプリがクラッシュすることを実機で確認した
/// （PackAndGoPocが単一スレッドの構造で一貫して安定動作していたことからも、
/// 複数スレッドからのアクセスが問題の所在と判断した）。
/// そのため、呼び出し側は Connect() で直接 ISldWorks を取得するのではなく、
/// 必ず InvokeAsync 経由でSolidWorksとのやり取りを行うこと。
/// </summary>
public interface ISolidWorksSession : IDisposable
{
    /// <summary>専用スレッド上でactionを実行し、結果を返す。</summary>
    Task<T> InvokeAsync<T>(Func<ISldWorks, T> action, CancellationToken ct = default);

    /// <summary>専用スレッド上でactionを実行する（戻り値なし）。</summary>
    Task InvokeAsync(Action<ISldWorks> action, CancellationToken ct = default);

    bool IsConnected { get; }
}
