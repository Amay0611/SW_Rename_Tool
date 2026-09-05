using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed class RenameExecutionFailedException : Exception
{
    public IReadOnlyList<RenameLogEntry> PartialResults { get; }

    public RenameExecutionFailedException(string message, IReadOnlyList<RenameLogEntry> partialResults)
        : base(message)
    {
        PartialResults = partialResults;
    }
}

/// <summary>
/// IPackAndGo は「1ファイルずつ処理する」API ではなく、
/// 最上位アセンブリを開いた時点で確定する依存ファイル一覧全体に対して
/// 新しい名前の配列を一括で渡し、1回の SavePackAndGo で実行する構造になっている。
/// そのため、ここではノードごとのループではなく、アセンブリ単位で1回だけ実行する。
///
/// SolidWorks.Interop.sldworks.dll / swconst.dll への早期バインディングを使う。
/// GetPackAndGoは後期バインディング(dynamic / Type.InvokeMember)では正しく動作しない
/// ことを実機検証で確認したため、この方式に統一している。
///
/// 進捗表示・キャンセルは「ファイル単位の安全なタイミング」という要件だったが、
/// SavePackAndGoの呼び出し自体は分割できないため、
/// キャンセルの安全なタイミングは「PackAndGo実行前まで」に限定される。
/// </summary>
public sealed class RenameExecutionService : IRenameExecutionService
{
    private readonly ISolidWorksSession _session;
    private readonly IRenameLogger _logger;

    public RenameExecutionService(ISolidWorksSession session, IRenameLogger logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        IReadOnlyList<FileNode> nodes,
        string topAssemblyPath,
        string logFolder,
        IProgress<RenameProgress> progress,
        CancellationToken ct = default)
    {
        var topAssembly = nodes.FirstOrDefault(n =>
            string.Equals(n.FilePath, topAssemblyPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("最上位アセンブリが見つかりません。");

        // キャンセルの最終確認ポイント。ここから先は中断できない。
        ct.ThrowIfCancellationRequested();
        progress.Report(new RenameProgress(0, nodes.Count, "SolidWorksでアセンブリを開いています..."));

        // SolidWorksとのCOM通信は専用スレッド固定（ISolidWorksSession.InvokeAsync）で行う。
        var results = await _session.InvokeAsync(
            sldWorks => RunPackAndGo(sldWorks, topAssembly, nodes, progress), CancellationToken.None);
        // 実行後のログ書き込みはキャンセルされても行う（部分結果を残すため CancellationToken.None）
        await _logger.WriteAsync(results, logFolder, CancellationToken.None);

        if (results.Any(r => r.Outcome == RenameOutcome.Failed))
        {
            throw new RenameExecutionFailedException(
                "一部のファイルの処理に失敗しました。バックアップから手動で復元してください。", results);
        }
    }

    private List<RenameLogEntry> RunPackAndGo(
        ISldWorks sldWorks, FileNode topAssembly, IReadOnlyList<FileNode> allNodes, IProgress<RenameProgress> progress)
    {
        var results = new List<RenameLogEntry>();

        ModelDoc2? model = null;
        try
        {
            int errors = 0, warnings = 0;
            model = sldWorks.OpenDoc6(
                topAssembly.FilePath,
                (int)swDocumentTypes_e.swDocASSEMBLY,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "",
                ref errors,
                ref warnings);

            if (model == null)
            {
                throw new InvalidOperationException(
                    $"アセンブリを開けませんでした。errors={errors}, warnings={warnings}");
            }

            var ext = model.Extension;
            var packAndGo = ext.GetPackAndGo();

            // 重要: IncludeDrawings等の設定は、GetDocumentNamesを呼ぶ「前」に行う必要がある。
            // 後で設定すると、GetDocumentNamesが返す一覧（＝newPaths配列のベース）と、
            // 実際の保存対象一覧（図面を含む）の件数が食い違い、SetDocumentSaveToNamesが
            // 正しく機能しなくなる（PoCでの実機検証で確認）。
            packAndGo.IncludeDrawings = true;
            packAndGo.IncludeSimulationResults = false;

            // --- 依存ファイル一覧の取得 ---
            packAndGo.GetDocumentNames(out object namesObj);
            var originalPaths = (string[])namesObj;

            // --- FileNode との対応付け（パス一致で紐付け） ---
            var nodeByPath = allNodes.ToDictionary(n => n.FilePath, StringComparer.OrdinalIgnoreCase);

            // --- 新しい名前の配列を、GetDocumentNames と同じ順序・件数で構築 ---
            // 対象外（Toolbox）や未編集のファイルは元の名前のまま渡す。
            // 範囲外参照のあるファイルは対象外にしない（リネームを許可し、
            // 後段で参照元アセンブリの参照を自動更新する）。
            var newPaths = new string[originalPaths.Length];
            for (int i = 0; i < originalPaths.Length; i++)
            {
                var original = originalPaths[i];

                if (nodeByPath.TryGetValue(original, out var node) && node.HasPendingRename)
                {
                    var dir = Path.GetDirectoryName(original)!;
                    var extName = Path.GetExtension(original);
                    newPaths[i] = Path.Combine(dir, node.NewBaseName! + extName);
                }
                else
                {
                    newPaths[i] = original; // 変更なし
                }
            }

            progress.Report(new RenameProgress(1, 4, "新しい名前を設定しています..."));
            packAndGo.SetDocumentSaveToNames(newPaths);

            // --- 保存前の最終確認（Duplicate等のユーザー入力エラーを事前検知） ---
            // 重要: GetDocumentSaveToNamesが返す配列の並び順は、GetDocumentNamesと一致する
            // ことがドキュメントで明記されているが、SavePackAndGoの戻り値（ステータス配列のみ、
            // ファイル名は返さない）については順序の保証がドキュメントに見当たらない。
            // 実機で「別のファイルの結果を取り違える」不具合が実際に発生したため、
            // ここでは confirmNames/confirmStatuses を「自前のnewPathsとの一致」で
            // 逆引きする方式にし、インデックスの暗黙の対応に頼らないようにする。
            packAndGo.GetDocumentSaveToNames(out object confirmNamesObj, out object confirmStatusObj);
            var confirmNames = (string[])confirmNamesObj;
            var confirmStatuses = (int[])confirmStatusObj;

            var statusByNewPath = new Dictionary<string, swPackAndGoSaveStatus_e>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < confirmNames.Length && i < confirmStatuses.Length; i++)
            {
                statusByNewPath[confirmNames[i]] = (swPackAndGoSaveStatus_e)confirmStatuses[i];
            }

            var preCheckErrors = new List<string>();
            for (int i = 0; i < newPaths.Length; i++)
            {
                if (statusByNewPath.TryGetValue(newPaths[i], out var status) &&
                    status != swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed)
                {
                    preCheckErrors.Add($"{Path.GetFileName(newPaths[i])}: {status}");
                }
            }

            if (preCheckErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    "保存前チェックでエラーが検出されました: " + string.Join(" / ", preCheckErrors));
            }

            // --- 実行 ---
            progress.Report(new RenameProgress(2, 4, "Pack and Go を実行しています..."));
            ext.SavePackAndGo(packAndGo);

            // 重要: SavePackAndGoが返すステータス配列は、順序保証がドキュメントに
            // 明記されておらず、実機でインデックスのズレによる誤判定が発生した。
            // そのため、この戻り値は使わず、「自前で把握しているoriginalPaths[i]→newPaths[i]の
            // ペアごとに、実際に新ファイルがディスク上に作成されたか」を成否の判定基準にする。
            // これはSolidWorks側の内部処理順序に依存しないため確実。
            for (int i = 0; i < originalPaths.Length; i++)
            {
                var original = originalPaths[i];
                var newPath = newPaths[i];

                if (string.Equals(original, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 対象外・未編集のファイルはログに残さない
                }

                var succeeded = File.Exists(newPath);

                results.Add(new RenameLogEntry(
                    DateTimeOffset.Now,
                    original,
                    newPath,
                    succeeded ? RenameOutcome.Success : RenameOutcome.Failed,
                    succeeded ? null : "新ファイルの作成が確認できませんでした。"));
            }

            // --- 重要: Pack and Go実行後も、開いていたアセンブリ(model)はSolidWorks上で
            // 開いたままになる（Marshal.ReleaseComObjectは.NET側の参照カウントを減らすだけで、
            // SolidWorks側でウィンドウを閉じる指示にはならない）。開いたままだと旧ファイルが
            // ロックされ続け、後続の旧ファイル削除・参照更新が失敗する原因になる（実機で確認）。
            // このタイミングで明示的に閉じる。
            try
            {
                sldWorks.CloseDoc(model.GetTitle());
            }
            catch
            {
                // 閉じられなくても後続処理は継続する。
            }

            // Pack and Go処理中、SolidWorksが内部的にスコープ内の他のドキュメント
            // （サブアセンブリ等）も開いたままにしている可能性があるため、念のため
            // スコープ内の全ファイルに対してもクローズを試みる（開いていなければ
            // 何もしないので安全）。ReplaceReferencedDocumentは参照元が閉じている
            // ことが前提のため、これを怠ると参照更新が失敗する原因になる。
            foreach (var n in allNodes)
            {
                try
                {
                    sldWorks.CloseDoc(n.FileName);
                }
                catch
                {
                    // 元々開いていない・閉じられない場合は無視する。
                }
            }

            // --- 参照元の参照更新 ---
            // 選択アセンブリの範囲外から参照されているケース（MarkOutOfScopeReferencesが
            // 検出したもの）に加えて、範囲「内」であっても、その親ファイル自体が今回
            // リネームされていない場合は同じ問題が起きることが実機で判明した。
            // Pack and Goは SetDocumentSaveToNames で新旧同名を指定したファイルについては
            // 実際には保存し直さず、内部の参照テーブルも更新しない（実機で確認）。
            // そのため、「リネームされた側」だけでなく「参照している親がリネームされたか」
            // で判定し、親がリネームされていない場合は範囲の内外を問わず
            // ReplaceReferencedDocumentで更新する。
            // ReplaceReferencedDocumentは「参照元ドキュメントが閉じている」ことが前提のAPIのため、
            // 旧ファイルをまだ削除していない、この段階で呼び出す。
            var renamedOldPaths = new HashSet<string>(
                results.Where(r => r.Outcome == RenameOutcome.Success &&
                                    !string.Equals(r.OldPath, r.NewPath, StringComparison.OrdinalIgnoreCase))
                       .Select(r => r.OldPath),
                StringComparer.OrdinalIgnoreCase);

            var renamedResultsNeedingParentUpdate =
                from entry in results
                where entry.Outcome == RenameOutcome.Success
                where renamedOldPaths.Contains(entry.OldPath)
                where nodeByPath.TryGetValue(entry.OldPath, out _)
                let node = nodeByPath[entry.OldPath]
                select (Entry: entry, Node: node);

            var referenceUpdateTargets = renamedResultsNeedingParentUpdate.ToList();

            if (referenceUpdateTargets.Count > 0)
            {
                progress.Report(new RenameProgress(3, 4, "参照元アセンブリの参照を更新しています..."));

                foreach (var (entry, node) in referenceUpdateTargets)
                {
                    // 親のうち、その親自身がリネームされていないものだけを対象にする。
                    // 親自身もリネームされている場合は、Pack and Goが同じ処理の中で
                    // 内部参照も正しく更新済みのはず（範囲内・範囲外を問わない）。
                    var parentsNeedingUpdate = node.Parents
                        .Where(p => !renamedOldPaths.Contains(p.FilePath))
                        .ToList();

                    foreach (var parent in parentsNeedingUpdate)
                    {
                        try
                        {
                            bool ok = sldWorks.ReplaceReferencedDocument(
                                parent.FilePath, entry.OldPath, entry.NewPath);

                            results.Add(new RenameLogEntry(
                                DateTimeOffset.Now,
                                parent.FilePath,
                                parent.FilePath,
                                ok ? RenameOutcome.Success : RenameOutcome.Failed,
                                ok
                                    ? $"{parent.FileName} の {node.FileName} への参照を新しい名前に更新しました。"
                                    : $"{parent.FileName} の参照更新に失敗しました" +
                                      "（ファイルが開かれている、または書き込み権限がない可能性があります）。"));
                        }
                        catch (Exception ex)
                        {
                            results.Add(new RenameLogEntry(
                                DateTimeOffset.Now, parent.FilePath, parent.FilePath, RenameOutcome.Failed,
                                $"{parent.FileName} の参照更新中に例外が発生しました: {ex.Message}"));
                        }
                    }
                }
            }

            // --- 重要: Pack and Goは「新しい名前でコピーを作成する」機能であり、
            // 元のファイルを削除するものではない（実機で確認）。「その場でのリネーム」を
            // 実現するには、コピーが成功した後に、こちらで明示的に旧ファイルを削除する必要がある。
            progress.Report(new RenameProgress(3, 4, "旧ファイルを削除しています..."));
            for (int i = 0; i < results.Count; i++)
            {
                var entry = results[i];

                if (entry.Outcome != RenameOutcome.Success)
                {
                    continue;
                }

                if (string.Equals(entry.OldPath, entry.NewPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 名前が変わっていないファイル（対象外・未編集）はそのまま
                }

                try
                {
                    if (File.Exists(entry.OldPath))
                    {
                        File.Delete(entry.OldPath);
                    }
                }
                catch (Exception ex)
                {
                    // コピー自体は成功しているためOutcomeはSuccessのまま維持し、
                    // 旧ファイル削除に失敗したことだけErrorMessageに記録する。
                    results[i] = entry with
                    {
                        ErrorMessage = $"新ファイルの作成は成功しましたが、旧ファイルの削除に失敗しました: {ex.Message}"
                    };
                }
            }

            progress.Report(new RenameProgress(4, 4, "完了"));
            return results;
        }
        finally
        {
            if (model != null)
            {
                Marshal.ReleaseComObject(model);
            }
        }
    }
}
