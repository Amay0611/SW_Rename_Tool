using System.Windows;
using System.Windows.Threading;

namespace SwRenameTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 無言でクラッシュする代わりに、例外内容を表示してから終了する。
        // 原因調査のための一時的な対応（本実装ではログファイルへの出力等に置き換える）。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "未処理の例外（UIスレッド）",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true; // ここでは終了させず、内容確認を優先する
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "不明な例外",
            "未処理の例外（バックグラウンドスレッド）",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "未観測のTask例外",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.SetObserved();
    }
}
