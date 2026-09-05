using System.Runtime.InteropServices;
using System.Windows.Threading;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwRenameTool.Core.Services;

/// <summary>
/// SolidWorksとのCOM通信を、起動時に生成した1本の専用STAスレッド上に固定して行う。
/// System.Windows.Threading.Dispatcher（WindowsBase）のメッセージポンプを利用し、
/// 呼び出し元（WPFのUIスレッドやTask.Runのワーカースレッド）がどのスレッドであっても、
/// 実際のSolidWorks API呼び出しは常に同じスレッドから行われるようにする。
/// </summary>
public sealed class SolidWorksSession : ISolidWorksSession
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _dispatcherReady = new(false);
    private Dispatcher? _dispatcher;
    private ISldWorks? _sldWorks;
    private readonly bool _visible;

    public SolidWorksSession(bool visible = false)
    {
        _visible = visible;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "SolidWorks-STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        _dispatcherReady.Wait();
    }

    public bool IsConnected => _sldWorks != null;

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcherReady.Set();
        Dispatcher.Run();
    }

    public Task<T> InvokeAsync<T>(Func<ISldWorks, T> action, CancellationToken ct = default)
    {
        if (_dispatcher == null)
        {
            throw new ObjectDisposedException(nameof(SolidWorksSession));
        }

        return _dispatcher
            .InvokeAsync(() => action(EnsureConnected()), DispatcherPriority.Normal, ct)
            .Task;
    }

    public Task InvokeAsync(Action<ISldWorks> action, CancellationToken ct = default)
    {
        if (_dispatcher == null)
        {
            throw new ObjectDisposedException(nameof(SolidWorksSession));
        }

        return _dispatcher
            .InvokeAsync(() => action(EnsureConnected()), DispatcherPriority.Normal, ct)
            .Task;
    }

    /// <summary>専用スレッド上で呼ばれる前提。初回呼び出し時にSolidWorksを起動する。</summary>
    private ISldWorks EnsureConnected()
    {
        if (_sldWorks != null)
        {
            return _sldWorks;
        }

        var type = Type.GetTypeFromProgID("SldWorks.Application")
            ?? throw new InvalidOperationException("SolidWorksが見つかりません。インストールされているか確認してください。");

        _sldWorks = (ISldWorks)Activator.CreateInstance(type)!;
        _sldWorks.Visible = _visible;

        // 重要: 軽量読み込み（Lightweight Components）が有効だと、Component2.GetModelDoc2()が
        // nullを返すことがある（公式仕様。サスペンド/軽量状態のコンポーネントはモデルドキュメントを
        // 持たない）。実機で、サブアセンブリ配下のパーツがWalkDesignTreeで検出されない不具合が
        // 発生したため、このツールで開くアセンブリは常に完全解決（非軽量）で読み込むようにする。
        // swUserPreferenceToggle_e.swAutoLoadPartsLightweight = 6 (バージョンにより値は同じはずだが、
        // 型ライブラリのenumを直接使うことで数値のズレを避ける)。
        try
        {
            _sldWorks.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swAutoLoadPartsLightweight, false);
        }
        catch
        {
            // 設定に失敗しても致命的ではないため続行する（フォールバックのOpenDoc6は別途機能する）。
        }

        return _sldWorks;
    }

    public void Dispose()
    {
        if (_dispatcher == null)
        {
            return;
        }

        // SolidWorksの終了処理も同じ専用スレッド上で行う。
        _dispatcher.Invoke(() =>
        {
            if (_sldWorks != null)
            {
                _sldWorks.ExitApp();
                Marshal.ReleaseComObject(_sldWorks);
                _sldWorks = null;
            }
        });

        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
        _dispatcher = null;
    }
}
