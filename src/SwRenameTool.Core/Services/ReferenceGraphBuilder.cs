using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

/// <summary>
/// 当初はGetDependencies2（全階層をフラットに返すAPI）で参照解析を行っていたが、
/// これには2つの問題があった。
///   1. 一覧の並び順がSolidWorksのデザインツリーの表示順と一致しない
///      （GetDependencies2は内部的な順序で返すため）。
///   2. 3階層以上のネストで、孫パーツが正しい階層（本来の親のすぐ下）ではなく、
///      祖先アセンブリの直接の子として扱われてしまう
///      （GetDependencies2は「全階層をフラットに返す」ため、これを直接の子関係の
///      構築に使うと、本来の階層構造が失われる）。
///
/// そのため、FeatureManagerツリーを実際にたどる方式（ISldWorks標準API、
/// ModelDoc2.FirstFeature/Feature.GetNextFeature/Feature.GetSpecificFeature2で
/// Component2を辿る）に変更した。これはSolidWorksのデザインツリーが実際に
/// 表示する順序そのものであり、かつ真の親子階層を正確に反映する。
///
/// サブアセンブリへの再帰は Component2.GetModelDoc2() で直接取得したドキュメントを使い、
/// OpenDoc6での開き直しや親の明示的なクローズは行わない（動作実績のあるVBAマクロの
/// 実装に合わせた）。
///
/// また、Feature.GetSpecificFeature2()がまれにNothing(null)を返すことがある
/// （実機・VBAマクロ双方で確認済みの既知の挙動）。その場合、対象アセンブリドキュメント
/// 自体（AssemblyDoc）からGetComponents(False)で直接の子コンポーネント一覧を取得し、
/// フィーチャー名と一致するComponent2を探すフォールバックを行う
/// （FindComponentByFeatureName）。
///
/// SolidWorks.Interop.sldworks.dll / swconst.dll への早期バインディングを使う。
/// GetPackAndGo等、後期バインディングでは正しく動作しないAPIがあることを実機で確認したため。
/// </summary>
public sealed class ReferenceGraphBuilder : IReferenceGraphBuilder
{
    private readonly ISolidWorksSession _session;
    private readonly IToolboxDetector _toolboxDetector;

    public ReferenceGraphBuilder(ISolidWorksSession session, IToolboxDetector toolboxDetector)
    {
        _session = session;
        _toolboxDetector = toolboxDetector;
    }

    public Task<IReadOnlyList<FileNode>> BuildAsync(string topAssemblyPath, CancellationToken ct = default)
    {
        // SolidWorksとのCOM通信は専用スレッド固定（ISolidWorksSession.InvokeAsync）で行う。
        return _session.InvokeAsync(sldWorks => Build(sldWorks, topAssemblyPath, ct), ct);
    }

    private IReadOnlyList<FileNode> Build(ISldWorks sldWorks, string topAssemblyPath, CancellationToken ct)
    {
        var nodesByPath = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        var topNode = GetOrCreateNode(nodesByPath, topAssemblyPath, SwFileType.Assembly);
        var scopePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { topAssemblyPath };
        var processedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { topAssemblyPath };

        int errors = 0, warnings = 0;
        var topModel = sldWorks.OpenDoc6(
            topAssemblyPath,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            "",
            ref errors,
            ref warnings);

        if (topModel == null)
        {
            throw new InvalidOperationException(
                $"{topAssemblyPath} を開けませんでした。errors={errors}, warnings={warnings}");
        }

        try
        {
            WalkDirectChildren(sldWorks, topNode, topModel, nodesByPath, scopePaths, processedAssemblies, ct);
        }
        finally
        {
            CloseDocument(sldWorks, topModel);
            Marshal.ReleaseComObject(topModel);
        }

        LinkDrawings(nodesByPath, scopePaths);
        MarkOutOfScopeReferences(sldWorks, topAssemblyPath, nodesByPath, scopePaths, ct);

        return nodesByPath.Values.Where(n => scopePaths.Contains(n.FilePath)).ToList();
    }

    /// <summary>
    /// 開かれているアセンブリのFeatureManagerツリーを上から順にたどり、直接の子
    /// （Reference型のフィーチャー＝Component2）をSolidWorksのデザインツリーの表示順
    /// そのままで処理する。サブアセンブリはComponent2.GetModelDoc2()で取得した
    /// ドキュメントに対して、その場で再帰的に辿る（動作実績のあるVBAマクロと同じ方式）。
    /// </summary>
    private void WalkDirectChildren(
        ISldWorks sldWorks,
        FileNode parentNode,
        ModelDoc2 parentModel,
        Dictionary<string, FileNode> nodesByPath,
        HashSet<string> scopePaths,
        HashSet<string> processedAssemblies,
        CancellationToken ct)
    {
        var seenInThisAssembly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var feat = parentModel.FirstFeature() as Feature;

        while (feat != null)
        {
            ct.ThrowIfCancellationRequested();

            if (feat.GetTypeName2() == "Reference")
            {
                var comp = feat.GetSpecificFeature2() as Component2;

                // GetSpecificFeature2()がNothing(null)を返すことがある（実機・VBAマクロ双方で
                // 確認済み）。その場合、親のGetComponents(False)で直接の子を列挙し、
                // フィーチャー名と一致するComponent2を探す。
                comp ??= FindComponentByFeatureName(parentModel, feat.Name);

                if (comp != null)
                {
                    var childPath = comp.GetPathName();

                    // 抑制されたコンポーネントもリネーム対象に含める（BOM上は実在するファイルのため）。
                    // 同じ部品が複数個使われている場合（ボルト4本等）は、ファイルとしては1つなので
                    // 初回のみ処理する。
                    if (!string.IsNullOrEmpty(childPath) && seenInThisAssembly.Add(childPath))
                    {
                        var childType = GetFileTypeFromExtension(childPath);
                        var childNode = GetOrCreateNode(nodesByPath, childPath, childType);
                        childNode.IsToolboxPart = IsToolboxPart(sldWorks, childPath);
                        scopePaths.Add(childPath);

                        if (!childNode.Parents.Contains(parentNode))
                        {
                            childNode.Parents.Add(parentNode);
                            parentNode.Children.Add(childNode); // デザインツリー表示順のまま追加
                        }

                        if (childType == SwFileType.Assembly && processedAssemblies.Add(childPath))
                        {
                            var childModel = comp.GetModelDoc2() as ModelDoc2;
                            if (childModel != null)
                            {
                                try
                                {
                                    WalkDirectChildren(sldWorks, childNode, childModel,
                                        nodesByPath, scopePaths, processedAssemblies, ct);
                                }
                                finally
                                {
                                    // GetModelDoc2()で取得したドキュメントは、最上位アセンブリの
                                    // 一部としてSolidWorksが管理しているため、CloseDocは呼ばない
                                    // （呼ぶと最上位側の表示が壊れる）。COM参照の解放のみ行う。
                                    Marshal.ReleaseComObject(childModel);
                                }
                            }
                        }
                    }
                }
            }

            feat = feat.GetNextFeature() as Feature;
        }
    }

    /// <summary>
    /// Feature.GetSpecificFeature2()がNothing(null)を返した場合のフォールバック。
    /// 対象アセンブリドキュメント自体（IAssemblyDoc）からGetComponents(False)で
    /// 直接の子コンポーネント一覧を取得し、フィーチャー名と一致するComponent2を探す
    /// （動作実績のあるVBAマクロと同じロジック。GetComponentsはComponent2ではなく
    /// AssemblyDoc側のメソッドのため、ModelDoc2をAssemblyDocにキャストして呼び出す）。
    /// </summary>
    private static Component2? FindComponentByFeatureName(ModelDoc2 modelDoc, string featureName)
    {
        try
        {
            if (modelDoc is not AssemblyDoc assemblyDoc)
            {
                return null;
            }

            if (assemblyDoc.GetComponents(false) is not object[] comps)
            {
                return null;
            }

            foreach (var c in comps)
            {
                if (c is Component2 candidate &&
                    string.Equals(candidate.Name2, featureName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 取得できない場合はnullを返し、呼び出し元でこのコンポーネントをスキップする。
        }

        return null;
    }

    /// <summary>
    /// アセンブリ(またはパーツ)を開き、GetDependencies2でフルパスの一覧を取得する。
    /// 呼び出し元でモデルの解放(ReleaseComObject)を行うこと。
    /// </summary>
    private static (ModelDoc2 Model, string[] FullPaths) OpenAndGetFlatDependencies(ISldWorks sldWorks, string path)
    {
        int errors = 0, warnings = 0;
        var model = sldWorks.OpenDoc6(
            path,
            (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            "",
            ref errors,
            ref warnings);

        if (model == null)
        {
            throw new InvalidOperationException($"{path} を開けませんでした。errors={errors}, warnings={warnings}");
        }

        var depsObj = model.GetDependencies2(true, true, false);
        var deps = (string[])depsObj;

        var fullPaths = new List<string>(deps.Length / 2);
        for (int i = 0; i < deps.Length; i += 2)
        {
            if (i + 1 < deps.Length)
            {
                fullPaths.Add(deps[i + 1]);
            }
        }

        return (model, fullPaths.ToArray());
    }

    /// <summary>
    /// Marshal.ReleaseComObjectは.NET側の参照カウントを減らすだけで、SolidWorks側で
    /// ウィンドウを閉じる指示にはならない（実機で確認：解析後もSolidWorks上にドキュメントが
    /// 開いたままになり、後続のDry Runでファイルロックとして誤検知される原因になっていた）。
    /// 参照解析のためだけに開いたドキュメントは、使い終わったら明示的に閉じる。
    /// </summary>
    private static void CloseDocument(ISldWorks sldWorks, ModelDoc2 model)
    {
        try
        {
            var title = model.GetTitle();
            sldWorks.CloseDoc(title);
        }
        catch
        {
            // 既に閉じられている等の理由で失敗しても、参照解析自体は継続する。
        }
    }

    /// <summary>
    /// Toolbox部品かどうかを判定する。
    ///   1. パスベース判定（レジストリで検出したToolboxフォルダ配下かどうか）を先に試す。
    ///      該当すれば、ファイルを開かずに済むため高速。
    ///   2. 該当しない場合、IModelDocExtension.ToolboxPartType（標準API、SolidWorks 2014〜）で
    ///      内部フラグを確認する。sldsetdocprop.exe等でToolbox化された、Toolboxフォルダ外に
    ///      置かれているカスタム部品もこれで検出できる（実際の運用で必要になったため追加）。
    ///      0 = Toolbox部品ではない、0以外 = Toolbox部品（swToolBoxPartType_e）。
    ///      この判定はファイルを開く必要があるため、パスベース判定よりコストが高い。
    /// </summary>
    private bool IsToolboxPart(ISldWorks sldWorks, string filePath)
    {
        if (_toolboxDetector.IsToolboxFile(filePath))
        {
            return true;
        }

        // 図面は対象外（ToolboxPartTypeはパーツ/アセンブリ向けのプロパティ）。
        var fileType = GetFileTypeFromExtension(filePath);
        if (fileType == SwFileType.Drawing)
        {
            return false;
        }

        ModelDoc2? model = null;
        try
        {
            var docType = fileType == SwFileType.Assembly
                ? swDocumentTypes_e.swDocASSEMBLY
                : swDocumentTypes_e.swDocPART;

            int errors = 0, warnings = 0;
            model = sldWorks.OpenDoc6(
                filePath, (int)docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);

            if (model == null)
            {
                return false;
            }

            return model.Extension.ToolboxPartType != 0;
        }
        catch
        {
            // 判定できない場合は「Toolbox部品ではない」扱いにする（安全側 = リネーム対象に残す）。
            return false;
        }
        finally
        {
            if (model != null)
            {
                CloseDocument(sldWorks, model);
                Marshal.ReleaseComObject(model);
            }
        }
    }

    private static FileNode GetOrCreateNode(
        Dictionary<string, FileNode> nodesByPath, string path, SwFileType type)
    {
        if (nodesByPath.TryGetValue(path, out var existing))
        {
            return existing;
        }

        var node = new FileNode { FilePath = path, FileType = type };
        nodesByPath[path] = node;
        return node;
    }

    private static SwFileType GetFileTypeFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sldprt" => SwFileType.Part,
            ".sldasm" => SwFileType.Assembly,
            ".slddrw" => SwFileType.Drawing,
            _ => throw new NotSupportedException($"未対応の拡張子です: {path}")
        };

    /// <summary>
    /// 実機検証済み: GetDependencies2の結果に図面(.slddrw)は含まれないため、
    /// 同一フォルダ内の拡張子違い同名ファイルを探索してペアリングする。
    /// 新たに見つかった図面ファイルは scopePaths にも追加し、最終結果に含める。
    /// </summary>
    private static void LinkDrawings(Dictionary<string, FileNode> nodesByPath, HashSet<string> scopePaths)
    {
        // 重要: .ToList() で先に確定させること。遅延評価のままだと、下のforeach内で
        // GetOrCreateNode が nodesByPath に図面ノードを追加するたびに再列挙が走り、
        // 「列挙中にコレクションが変更された」例外になる（実機で確認）。
        var modelFolders = nodesByPath.Values
            .Where(n => n.FileType != SwFileType.Drawing)
            .Select(n => Path.GetDirectoryName(n.FilePath) ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var drawingsByKey = new Dictionary<(string Folder, string BaseName), FileNode>(
            new FolderBaseNameComparer());

        foreach (var folder in modelFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var drawingPath in Directory.EnumerateFiles(folder, "*.slddrw"))
            {
                var node = GetOrCreateNode(nodesByPath, drawingPath, SwFileType.Drawing);
                drawingsByKey[(folder, node.BaseName)] = node;
            }
        }

        foreach (var model in nodesByPath.Values.Where(n => n.FileType != SwFileType.Drawing).ToList())
        {
            var folder = Path.GetDirectoryName(model.FilePath) ?? string.Empty;
            if (drawingsByKey.TryGetValue((folder, model.BaseName), out var drawing))
            {
                model.LinkedDrawing = drawing;
                scopePaths.Add(drawing.FilePath);
            }
        }
    }

    /// <summary>
    /// 選択アセンブリの依存範囲外から参照されているノードを自動除外フラグ付きにする。
    /// 対象アセンブリと同じフォルダ内にある、今回のスコープに含まれない他のアセンブリ(.sldasm)を
    /// 軽くGetDependencies2でスキャンし、今回のグラフに含まれる部品を参照していないか確認する。
    /// </summary>
    private void MarkOutOfScopeReferences(
        ISldWorks sldWorks,
        string topAssemblyPath,
        Dictionary<string, FileNode> nodesByPath,
        HashSet<string> scopePaths,
        CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(topAssemblyPath) ?? string.Empty;
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var otherAssemblyPath in Directory.EnumerateFiles(folder, "*.sldasm"))
        {
            ct.ThrowIfCancellationRequested();

            // SolidWorksが開いている間に生成される "~$ファイル名" ロックファイルは実体がないため除外。
            if (Path.GetFileName(otherAssemblyPath).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 今回選択したツリーに既に含まれるアセンブリ（自分自身やサブアセンブリ）はスキップ。
            if (scopePaths.Contains(otherAssemblyPath))
            {
                continue;
            }

            ModelDoc2? otherModel = null;
            try
            {
                (otherModel, var otherDependencyPaths) = OpenAndGetFlatDependencies(sldWorks, otherAssemblyPath);

                var referencedInScope = otherDependencyPaths
                    .Where(p => scopePaths.Contains(p))
                    .ToList();

                if (referencedInScope.Count == 0)
                {
                    continue;
                }

                // 重要: 外部アセンブリXが「選択した最上位アセンブリtopAssemblyPath自体」も
                // 参照している場合（＝XはBの祖先アセンブリである場合）、Xの依存関係一覧には
                // GetDependencies2の「全階層フラット取得」の仕様により、Bの子孫パーツも
                // 自動的に含まれてくる。しかしこれはBを経由した間接的な参照に過ぎず、
                // Assem1→Bの参照さえ更新すれば自動的に解決されるため、Bの子孫を
                // 個別に「範囲外」として除外する必要はない（実際に運用上の問題として指摘された）。
                // 一方、Xがtopを参照しておらず、かつスコープ内の別のノードを直接参照している場合
                // （例: OtherAssemがSharedPartを直接参照するケース）は、真に独立した外部参照であり、
                // 引き続き除外が必要。
                var xReferencesTopDirectlyOrTransitively = otherDependencyPaths
                    .Contains(topAssemblyPath, StringComparer.OrdinalIgnoreCase);

                // 外部アセンブリ自体はnodesByPathに登録するが、scopePathsには加えないため
                // 最終結果（UIの一覧）には出てこない。Parentsからの参照元表示にのみ使われる。
                var externalNode = GetOrCreateNode(nodesByPath, otherAssemblyPath, SwFileType.Assembly);

                foreach (var referencedPath in referencedInScope)
                {
                    var node = nodesByPath[referencedPath];
                    var isTopItself = string.Equals(referencedPath, topAssemblyPath, StringComparison.OrdinalIgnoreCase);

                    if (isTopItself)
                    {
                        // 選択した最上位アセンブリ自身は、外部から参照されていても除外フラグを
                        // 立てない。ユーザーが明示的にこのアセンブリをリネーム対象として
                        // 選択している以上、「範囲外から参照されている」という理由だけで
                        // リネーム不可にしてしまうと、サブアセンブリ単位でのリネームができなくなる。
                        // 代わりに Parents に記録し、RenameExecutionService側でリネーム成功後に
                        // ReplaceReferencedDocumentで参照を更新する。
                    }
                    else if (xReferencesTopDirectlyOrTransitively)
                    {
                        // Bの子孫であり、かつXがBを経由してのみ参照しているノード。
                        // Assem1→Bの参照更新で自動的に解決されるため、除外もParents記録も不要。
                        continue;
                    }
                    else
                    {
                        // Bを介さない独立した外部参照（真の共有部品）。従来通り除外する。
                        node.IsReferencedFromOutsideScope = true;
                    }

                    if (!node.Parents.Contains(externalNode))
                    {
                        node.Parents.Add(externalNode);
                        externalNode.Children.Add(node);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 開けない・破損している等の他アセンブリはスキップして続行する。
            }
            finally
            {
                if (otherModel != null)
                {
                    CloseDocument(sldWorks, otherModel);
                    Marshal.ReleaseComObject(otherModel);
                }
            }
        }
    }

    private sealed class FolderBaseNameComparer : IEqualityComparer<(string Folder, string BaseName)>
    {
        public bool Equals((string Folder, string BaseName) x, (string Folder, string BaseName) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.BaseName, y.BaseName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string BaseName) obj) =>
            HashCode.Combine(obj.Folder.ToLowerInvariant(), obj.BaseName.ToLowerInvariant());
    }
}
