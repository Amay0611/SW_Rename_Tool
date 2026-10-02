using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwRenameTool.Core.Models;
using SwRenameTool.Core.Services;

namespace SwRenameTool.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IReferenceGraphBuilder _graphBuilder;
    private readonly IDryRunValidator _dryRunValidator;
    private readonly IBackupService _backupService;
    private readonly IRenameExecutionService _renameExecutionService;
    private readonly IThumbnailService _thumbnailService;

    // FileNode -> それを表示している行ViewModel。図面自動追従で相手の行を引くために使う。
    private readonly Dictionary<FileNode, FileRowViewModel> _rowByNode = new();

    private CancellationTokenSource? _executionCts;
    private CancellationTokenSource? _thumbnailLoadCts;

    public MainViewModel(
        IReferenceGraphBuilder graphBuilder,
        IDryRunValidator dryRunValidator,
        IBackupService backupService,
        IRenameExecutionService renameExecutionService,
        IThumbnailService thumbnailService)
    {
        _graphBuilder = graphBuilder;
        _dryRunValidator = dryRunValidator;
        _backupService = backupService;
        _renameExecutionService = renameExecutionService;
        _thumbnailService = thumbnailService;
    }

    [ObservableProperty]
    private string? _selectedAssemblyPath;

    public ObservableCollection<FileRowViewModel> Rows { get; } = new();

    /// <summary>データグリッドで選択中の行。拡大プレビューパネルの表示元。</summary>
    [ObservableProperty]
    private FileRowViewModel? _selectedRow;

    // Windows エクスプローラーの「特大アイコン」相当の解像度。
    private const int LargeThumbnailSize = 256;

    partial void OnSelectedRowChanged(FileRowViewModel? value)
    {
        // 拡大プレビュー用の高解像度サムネイルは、選択されたときに初めて取得する
        // （全行分を先読みすると無駄が多いため）。取得済みならスキップする。
        if (value == null || value.LargeThumbnail != null)
        {
            return;
        }

        _ = LoadLargeThumbnailAsync(value);
    }

    private async Task LoadLargeThumbnailAsync(FileRowViewModel row)
    {
        var thumbnail = await _thumbnailService.GetThumbnailAsync(row.Node.FilePath, LargeThumbnailSize);
        if (thumbnail != null)
        {
            row.LargeThumbnail = thumbnail;
        }
    }

    /// <summary>Dry Runで検出した警告・エラーの一覧（UI表示用に文字列化）。</summary>
    public ObservableCollection<string> DryRunMessages { get; } = new();

    [ObservableProperty]
    private DryRunResult? _dryRunResult;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private double _executionProgress;

    [ObservableProperty]
    private string? _currentProcessingFileName;

    [RelayCommand]
    private async Task SelectAssemblyAsync()
    {
        // 実際のファイル選択ダイアログはView側（コードビハインド）で表示し、
        // 選択結果のパスをこのコマンドに渡す構成に変更してもよい。
        // ここではSelectedAssemblyPathが既にセットされている前提で解析を進める。
        if (string.IsNullOrWhiteSpace(SelectedAssemblyPath))
        {
            return;
        }

        // 前回の解析で走らせていたサムネイル読み込みが残っていれば止める。
        _thumbnailLoadCts?.Cancel();

        IsBusy = true;
        StatusMessage = "参照関係を解析しています...";
        DryRunResult = null;
        DryRunMessages.Clear();

        try
        {
            var nodes = await _graphBuilder.BuildAsync(SelectedAssemblyPath);

            // 既存の購読を解除してから作り直す（再解析時の多重購読防止）。
            foreach (var row in _rowByNode.Values)
            {
                row.NewBaseNameEdited -= OnRowNewBaseNameEdited;
            }

            _rowByNode.Clear();
            Rows.Clear();
            SelectedRow = null;

            foreach (var node in nodes)
            {
                var row = new FileRowViewModel(node);
                row.NewBaseNameEdited += OnRowNewBaseNameEdited;
                _rowByNode[node] = row;
            }

            // 親子関係が分かるツリー順（深さ優先探索）に並べ替えてから表示する。
            var topNode = nodes.FirstOrDefault(n =>
                string.Equals(n.FilePath, SelectedAssemblyPath, StringComparison.OrdinalIgnoreCase));

            foreach (var row in BuildTreeOrderedRows(topNode, nodes))
            {
                Rows.Add(row);
            }

            StatusMessage = $"{nodes.Count}件のファイルを検出しました。";
        }
        finally
        {
            IsBusy = false;
        }

        // サムネイルは一覧表示をブロックしないよう、後から非同期・遅延で1件ずつ読み込む。
        _thumbnailLoadCts = new CancellationTokenSource();
        _ = LoadThumbnailsAsync(Rows.ToList(), _thumbnailLoadCts.Token);
    }

    /// <summary>
    /// 選択アセンブリを起点に深さ優先探索し、親子関係が罫線文字（├─/└─/│）で分かる
    /// 表示順に並べ替える。図面(LinkedDrawing)は対応する3Dモデルと同じ階層（同じ
    /// プレフィックス）でその直後に配置する。複数の親を持つ共有部品は、最初に
    /// 辿り着いた親の下に1回だけ表示する（参照元数は「参照」列で別途確認できるため、
    /// 重複表示はしない）。
    /// </summary>
    private List<FileRowViewModel> BuildTreeOrderedRows(FileNode? root, IReadOnlyList<FileNode> allNodes)
    {
        var ordered = new List<FileRowViewModel>();
        var scopeSet = new HashSet<FileNode>(allNodes);
        var visited = new HashSet<FileNode>();

        // ownPrefix: この行自体の先頭に付ける罫線（例: "│　├─ "）。ルートは空文字。
        // continuationPrefix: この行の「子」を描画する際、先頭に引き継ぐ縦線部分
        //   （自分が兄弟内で最後なら空白、まだ続くなら "│　" で下に線を伸ばす）。
        void Visit(FileNode node, string ownPrefix, string continuationPrefix)
        {
            if (!visited.Add(node) || !_rowByNode.TryGetValue(node, out var row))
            {
                return;
            }

            row.TreePrefix = ownPrefix;
            ordered.Add(row);

            // 図面はモデルと同じ階層（同じプレフィックス）で直後に表示する
            // （ツリー上の「子」ではなく、モデルに付随する行という位置づけのため）。
            if (node.LinkedDrawing != null &&
                scopeSet.Contains(node.LinkedDrawing) &&
                visited.Add(node.LinkedDrawing) &&
                _rowByNode.TryGetValue(node.LinkedDrawing, out var drawingRow))
            {
                drawingRow.TreePrefix = ownPrefix;
                ordered.Add(drawingRow);
            }

            var children = node.Children.Where(scopeSet.Contains).ToList();
            for (int i = 0; i < children.Count; i++)
            {
                var isLast = i == children.Count - 1;
                var childOwnPrefix = continuationPrefix + (isLast ? "└─ " : "├─ ");
                var childContinuationPrefix = continuationPrefix + (isLast ? "　　" : "│　");
                Visit(children[i], childOwnPrefix, childContinuationPrefix);
            }
        }

        if (root != null)
        {
            Visit(root, string.Empty, string.Empty);
        }

        // 走査で辿り着かなかったノードがあれば（想定外のケース）末尾に追加しておく。
        foreach (var node in allNodes)
        {
            if (visited.Add(node) && _rowByNode.TryGetValue(node, out var row))
            {
                row.TreePrefix = string.Empty;
                ordered.Add(row);
            }
        }

        return ordered;
    }

    /// <summary>
    /// サムネイルを1件ずつ非同期に読み込む。await Task.Yield() を挟むことで、
    /// 1件読み込むごとにUIスレッドへ制御を返し、一覧の操作をブロックしないようにする。
    /// </summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<FileRowViewModel> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            row.Thumbnail = await _thumbnailService.GetThumbnailAsync(row.Node.FilePath);
            await Task.Yield();
        }
    }

    /// <summary>
    /// 行の変更後ファイル名が編集されたとき、紐付いた図面の名前を自動追従させる。
    /// </summary>
    private void OnRowNewBaseNameEdited(object? sender, EventArgs e)
    {
        if (sender is not FileRowViewModel row)
        {
            return;
        }

        var drawingNode = row.Node.LinkedDrawing;
        if (drawingNode == null || !_rowByNode.TryGetValue(drawingNode, out var drawingRow))
        {
            return;
        }

        drawingRow.ApplySyncedName(row.NewBaseName);
    }

    [RelayCommand]
    private void RunDryRun()
    {
        var nodes = Rows.Select(r => r.Node).ToList();
        var result = _dryRunValidator.Validate(nodes);
        DryRunResult = result;

        DryRunMessages.Clear();
        foreach (var issue in result.Issues)
        {
            var prefix = issue.Severity switch
            {
                ValidationSeverity.Error => "[エラー] ",
                ValidationSeverity.Warning => "[警告] ",
                _ => "[情報] "
            };
            DryRunMessages.Add(prefix + issue.Message);
        }

        StatusMessage = result.CanProceed
            ? $"Dry Run完了：変更対象 {result.TargetCount}件 / 警告 {result.WarningCount}件"
            : $"Dry Run完了：エラー {result.ErrorCount}件のため実行できません。";
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (DryRunResult is not { CanProceed: true })
        {
            StatusMessage = "先にDry Runを実行し、エラーを解消してください。";
            return;
        }

        var nodes = Rows.Select(r => r.Node).ToList();
        var targetFolder = Path.GetDirectoryName(SelectedAssemblyPath) ?? string.Empty;

        IsBusy = true;
        _executionCts = new CancellationTokenSource();

        try
        {
            StatusMessage = "バックアップを作成しています...";
            var backup = await _backupService.CreateBackupAsync(nodes, targetFolder, _executionCts.Token);

            if (!backup.IntegrityOk)
            {
                StatusMessage = "バックアップの整合性チェックに失敗しました。処理を中止しました。";
                return;
            }

            StatusMessage = "リネームを実行しています...（開始後はファイル単位の途中キャンセルはできません）";
            var progress = new Progress<RenameProgress>(p =>
            {
                ExecutionProgress = p.Total == 0 ? 0 : (double)p.Completed / p.Total * 100;
                CurrentProcessingFileName = p.CurrentFileName;
            });

            await _renameExecutionService.ExecuteAsync(
                nodes, SelectedAssemblyPath!, backup.BackupFolderPath, progress, _executionCts.Token);

            StatusMessage = "完了しました。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "安全なタイミングでキャンセルしました。";
        }
        catch (RenameExecutionFailedException ex)
        {
            StatusMessage = ex.Message + " バックアップから手動で復元してください。";
        }
        finally
        {
            IsBusy = false;
            _executionCts = null;
        }
    }

    [RelayCommand]
    private void CancelExecution()
    {
        // 安全なタイミング（Pack and Go実行前まで）でのみキャンセルが反映される。
        _executionCts?.Cancel();
    }
}
