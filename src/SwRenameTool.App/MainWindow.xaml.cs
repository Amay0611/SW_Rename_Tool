using System.Windows;
using Microsoft.Win32;
using SwRenameTool.App.ViewModels;
using SwRenameTool.Core.Services;

namespace SwRenameTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // 簡易コンポジションルート。
        // プロジェクトが大きくなる場合はMicrosoft.Extensions.DependencyInjectionの導入を検討する。
        // ※デバッグ中は visible: true にして、SolidWorksプロセスの状態を目視確認できるようにしている。
        //   本実装では設定画面の「バックグラウンドで起動」トグルに応じて切り替える想定。
        var session = new SolidWorksSession(visible: true);
        var toolboxDetector = new ToolboxDetector();
        var graphBuilder = new ReferenceGraphBuilder(session, toolboxDetector);
        var dryRunValidator = new DryRunValidator();
        var backupService = new BackupService();
        var logger = new RenameLogger();
        var executionService = new RenameExecutionService(session, logger);
        var thumbnailService = new ThumbnailService();

        _viewModel = new MainViewModel(
            graphBuilder, dryRunValidator, backupService, executionService, thumbnailService);
        DataContext = _viewModel;

        Closed += (_, _) => session.Dispose();
    }

    private void OnSelectAssemblyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SolidWorks アセンブリ (*.sldasm)|*.sldasm",
            Title = "対象アセンブリを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.SelectedAssemblyPath = dialog.FileName;
            _viewModel.SelectAssemblyCommand.Execute(null);
        }
    }
}
