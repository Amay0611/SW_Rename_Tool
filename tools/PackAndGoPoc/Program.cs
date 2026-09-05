using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

// ============================================================================
// SolidWorks API 技術検証用 PoC（早期バインディング版）
//
// SolidWorks.Interop.sldworks.dll / swconst.dll （SolidWorksインストールフォルダの
// api\redist にある公式Primary Interop Assembly）への直接参照を使う。
//
// 経緯: 当初は<COMReference>によるtlbimpベースの参照を試みたが、これは
// ResolveComReferenceというMSBuildタスクを必要とし、dotnet CLI（.NET Core版MSBuild）
// ではビルドできなかった。次にdynamic/Type.InvokeMemberによる後期バインディングに
// 切り替えたが、GetPackAndGoなど一部のAPIは後期バインディングでは正しく動作しない
// ことをVBAでの検証でも確認した（早期バインディングでのみ成功）。
// 事前ビルド済みのPIA(.dll)をそのまま参照すれば、tlbimpも不要かつ早期バインディングも
// 使えるため、この方式に統一した。
//
// 使い方:
//   PackAndGoPoc.exe deps "<アセンブリ/パーツ/図面のフルパス>"
//     → GetDependencies2 の検証（参照解析フェーズ用、読み取りのみ）
//
//   PackAndGoPoc.exe graph "<アセンブリのフルパス>"
//     → 参照グラフ構築(ReferenceGraphBuilder相当)の検証（読み取りのみ）
//
//   PackAndGoPoc.exe packandgo "<最上位アセンブリのフルパス>"
//     → GetPackAndGo → GetDocumentNames → SetDocumentSaveToNames → SavePackAndGo
//       の一連の流れの検証（リネーム実行フェーズ用）
//
//   PackAndGoPoc.exe toolbox "<パーツ/アセンブリのフルパス>"
//     → IModelDocExtension.ToolboxPartType の検証（Toolbox部品判定用、読み取りのみ）
//
// 注意:
//   packandgo コマンドは対象アセンブリに実際に新しい名前を割り当てて保存します。
//   検証用のコピーフォルダで試すこと。本番データに直接使わないこと。
//   deps / graph コマンドは読み取りのみで、ファイルへの変更は行いません。
// ============================================================================

static string DescribeSaveStatus(swPackAndGoSaveStatus_e status) => status switch
{
    swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed => "Succeed（成功）",
    swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_UserInputNotCorrect => "UserInputNotCorrect（入力内容が不正）",
    swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_FileAlreadyExist => "FileAlreadyExist（同名ファイルが既に存在）",
    swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_SaveToEmpty => "SaveToEmpty（保存先が未指定）",
    swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_SaveError => "SaveError（保存エラー）",
    _ => $"不明なステータス({status})"
};

if (args.Length < 2)
{
    Console.WriteLine("使い方:");
    Console.WriteLine("  PackAndGoPoc.exe deps \"<ファイルのフルパス>\"       … GetDependencies2 の検証");
    Console.WriteLine("  PackAndGoPoc.exe graph \"<アセンブリのフルパス>\"    … 参照グラフ構築の検証");
    Console.WriteLine("  PackAndGoPoc.exe packandgo \"<アセンブリのフルパス>\" … Pack and Go の検証");
    Console.WriteLine("  PackAndGoPoc.exe toolbox \"<ファイルのフルパス>\"     … ToolboxPartType の検証");
    return;
}

var command = args[0].ToLowerInvariant();
var targetPath = args[1];

if (!File.Exists(targetPath))
{
    Console.WriteLine($"ファイルが見つかりません: {targetPath}");
    return;
}

if (command != "deps" && command != "packandgo" && command != "graph" && command != "toolbox")
{
    Console.WriteLine($"不明なコマンドです: {command}");
    return;
}

ISldWorks? sldWorks = null;
ModelDoc2? model = null;

try
{
    Console.WriteLine("SolidWorksを起動しています...");
    var swType = Type.GetTypeFromProgID("SldWorks.Application")
        ?? throw new InvalidOperationException("SolidWorksが見つかりません。インストールされているか確認してください。");

    sldWorks = (ISldWorks)Activator.CreateInstance(swType)!;
    sldWorks.Visible = true; // 検証中は挙動を目視できるよう表示する

    if (command == "graph")
    {
        RunGraphCheck(sldWorks, targetPath);
        return;
    }

    var docType = Path.GetExtension(targetPath).ToLowerInvariant() switch
    {
        ".sldprt" => swDocumentTypes_e.swDocPART,
        ".sldasm" => swDocumentTypes_e.swDocASSEMBLY,
        ".slddrw" => swDocumentTypes_e.swDocDRAWING,
        _ => throw new NotSupportedException($"未対応の拡張子です: {targetPath}")
    };

    Console.WriteLine($"ファイルを開いています: {targetPath}");
    int errors = 0, warnings = 0;
    model = sldWorks.OpenDoc6(
        targetPath, (int)docType, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);

    if (model == null)
    {
        Console.WriteLine($"オープンに失敗しました。errors={errors}, warnings={warnings}");
        return;
    }

    if (command == "deps")
    {
        RunDependenciesCheck(model);
    }
    else if (command == "toolbox")
    {
        RunToolboxCheck(model);
    }
    else
    {
        RunPackAndGoCheck(model);
    }
}
catch (Exception ex)
{
    Console.WriteLine("エラーが発生しました:");
    Console.WriteLine(ex);
}
finally
{
    if (model != null)
    {
        Marshal.ReleaseComObject(model);
    }

    if (sldWorks != null)
    {
        Marshal.ReleaseComObject(sldWorks);
    }
}

// ============================================================================
// GetDependencies2 検証（参照解析フェーズ用）
// ============================================================================
void RunDependenciesCheck(ModelDoc2 openedModel)
{
    var depsObj = openedModel.GetDependencies2(true, true, false);
    var deps = (string[])depsObj;

    Console.WriteLine();
    Console.WriteLine($"戻り値の要素数: {deps.Length}（偶数=ファイル名, 奇数=フルパスのペアと想定）");
    Console.WriteLine();

    for (int i = 0; i < deps.Length; i += 2)
    {
        var name = deps[i];
        var fullPath = (i + 1 < deps.Length) ? deps[i + 1] : "(なし)";

        Console.WriteLine($"  名前: {name}");
        Console.WriteLine($"  パス: {fullPath}");

        var isUnderToolboxLikely = fullPath.Contains("toolbox", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  Toolboxっぽいパスか: {isUnderToolboxLikely}");
        Console.WriteLine();
    }
}

// ============================================================================
// ToolboxPartType の検証（Toolbox部品判定用）
// ============================================================================
void RunToolboxCheck(ModelDoc2 openedModel)
{
    var toolboxType = openedModel.Extension.ToolboxPartType;

    Console.WriteLine();
    Console.WriteLine($"ToolboxPartType の値: {toolboxType}");
    Console.WriteLine($"Toolbox部品と判定: {(toolboxType != 0 ? "はい" : "いいえ")}");
    Console.WriteLine();
    Console.WriteLine("参考: swToolBoxPartType_e");
    Console.WriteLine("  0 = swNotAToolboxPart（Toolbox部品ではない）");
    Console.WriteLine("  1以上 = Toolbox部品（種類により値が異なる）");
}

// ============================================================================
// 参照グラフ構築の検証（ReferenceGraphBuilder相当）
// ============================================================================

(ModelDoc2 Model, string[] FullPaths) OpenAndGetFlatDependencies(ISldWorks sw, string path)
{
    int errs = 0, warns = 0;
    var m = sw.OpenDoc6(
        path, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errs, ref warns);

    if (m == null)
    {
        throw new InvalidOperationException($"{path} を開けませんでした。errors={errs}, warnings={warns}");
    }

    var depsObj = m.GetDependencies2(true, true, false);
    var deps = (string[])depsObj;

    var fullPaths = new List<string>(deps.Length / 2);
    for (int i = 0; i < deps.Length; i += 2)
    {
        if (i + 1 < deps.Length)
        {
            fullPaths.Add(deps[i + 1]);
        }
    }

    return (m, fullPaths.ToArray());
}

void RunGraphCheck(ISldWorks sw, string topAssemblyPath)
{
    var nodesByPath = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
    var scopePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { topAssemblyPath };

    GraphNode GetOrCreate(string path) =>
        nodesByPath.TryGetValue(path, out var existing)
            ? existing
            : nodesByPath[path] = new GraphNode(path);

    Console.WriteLine();
    Console.WriteLine("=== 1. 最上位アセンブリの全体依存関係を取得 ===");
    var (topModel, topDeps) = OpenAndGetFlatDependencies(sw, topAssemblyPath);
    try
    {
        foreach (var p in topDeps)
        {
            GetOrCreate(p);
            scopePaths.Add(p);
        }
        Console.WriteLine($"全体で {topDeps.Length} 件のファイルを検出");

        Console.WriteLine();
        Console.WriteLine("=== 2. サブアセンブリの直接の子関係を構築 ===");
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { topAssemblyPath };
        var queue = new Queue<string>(topDeps.Where(p => p.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)));
        var topNode = GetOrCreate(topAssemblyPath);

        while (queue.Count > 0)
        {
            var asmPath = queue.Dequeue();
            var asmNode = GetOrCreate(asmPath);

            var (subModel, subDeps) = OpenAndGetFlatDependencies(sw, asmPath);
            try
            {
                Console.WriteLine($"  {asmNode.FileName} の直接の子: {subDeps.Length}件");
                foreach (var childPath in subDeps)
                {
                    var childNode = GetOrCreate(childPath);
                    if (!childNode.Parents.Contains(asmNode))
                    {
                        childNode.Parents.Add(asmNode);
                    }
                    claimed.Add(childPath);

                    if (childPath.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) && processed.Add(childPath))
                    {
                        queue.Enqueue(childPath);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(subModel);
            }
        }

        foreach (var p in topDeps.Where(p => !claimed.Contains(p)))
        {
            var node = GetOrCreate(p);
            if (!node.Parents.Contains(topNode))
            {
                node.Parents.Add(topNode);
            }
        }
    }
    finally
    {
        Marshal.ReleaseComObject(topModel);
    }

    Console.WriteLine();
    Console.WriteLine("=== 3. 範囲外参照の検出 ===");
    var folder = Path.GetDirectoryName(topAssemblyPath) ?? string.Empty;
    foreach (var otherAsmPath in Directory.EnumerateFiles(folder, "*.sldasm"))
    {
        if (Path.GetFileName(otherAsmPath).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (scopePaths.Contains(otherAsmPath))
        {
            continue;
        }

        Console.WriteLine($"  範囲外アセンブリを確認中: {Path.GetFileName(otherAsmPath)}");
        ModelDoc2? otherModel = null;
        try
        {
            (otherModel, var otherDeps) = OpenAndGetFlatDependencies(sw, otherAsmPath);
            var hits = otherDeps.Where(scopePaths.Contains).ToList();

            if (hits.Count == 0)
            {
                Console.WriteLine("    → 今回のスコープへの参照なし");
                continue;
            }

            var externalNode = GetOrCreate(otherAsmPath);
            foreach (var hitPath in hits)
            {
                var node = GetOrCreate(hitPath);
                node.IsOutOfScope = true;
                if (!node.Parents.Contains(externalNode))
                {
                    node.Parents.Add(externalNode);
                }
                Console.WriteLine($"    → {node.FileName} を参照している（範囲外と判定）");
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"    → 開けなかったためスキップ: {ex.Message}");
        }
        finally
        {
            if (otherModel != null)
            {
                Marshal.ReleaseComObject(otherModel);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== 最終結果（UI一覧に出す想定のファイル） ===");
    foreach (var node in nodesByPath.Values.Where(n => scopePaths.Contains(n.FilePath)))
    {
        var parentNames = string.Join(", ", node.Parents.Select(p => p.FileName));
        var flags = node.IsOutOfScope ? " [範囲外参照のため対象外]" : "";
        Console.WriteLine($"  {node.FileName}{flags}  (親: {parentNames})");
    }
}

// ============================================================================
// Pack and Go 検証（リネーム実行フェーズ用）
// GetPackAndGo → GetDocumentNames → SetDocumentSaveToNames → SavePackAndGo
// ============================================================================
void RunPackAndGoCheck(ModelDoc2 openedModel)
{
    var ext = openedModel.Extension;
    var packAndGo = ext.GetPackAndGo();

    // 重要: IncludeDrawings等の設定は、GetDocumentNamesを呼ぶ「前」に行う必要がある。
    // 後で設定すると、GetDocumentNamesが返す一覧（＝newNames配列のベース）と、
    // 実際の保存対象一覧（図面を含む）の件数が食い違い、SetDocumentSaveToNamesが
    // 正しく機能しなくなる（実機検証で確認）。
    packAndGo.IncludeDrawings = true;
    packAndGo.IncludeSimulationResults = false;

    // --- 1. 依存ファイル一覧の取得 -----------------------------------------
    bool status = packAndGo.GetDocumentNames(out object namesObj);
    var originalNames = (string[])namesObj;

    Console.WriteLine();
    Console.WriteLine($"依存ファイル数: {originalNames.Length}  (GetDocumentNames status={status})");
    for (int i = 0; i < originalNames.Length; i++)
    {
        Console.WriteLine($"  [{i}] {originalNames[i]}");
    }

    // --- 3. 新しい名前の一覧を組み立てる -------------------------------------
    // このPoCでは動作確認のため、ファイル名の先頭に "TEST_" を付けるだけの
    // 単純なマッピングを行う。実装時はUIで指定された新ファイル名（対象外は元名のまま）に置き換える。
    var newNames = new string[originalNames.Length];
    for (int i = 0; i < originalNames.Length; i++)
    {
        var dir = Path.GetDirectoryName(originalNames[i])!;
        var extName = Path.GetExtension(originalNames[i]);
        var baseName = Path.GetFileNameWithoutExtension(originalNames[i]);
        var newBaseName = "TEST_" + baseName;

        newNames[i] = Path.Combine(dir, newBaseName + extName);
    }

    Console.WriteLine();
    Console.WriteLine("設定する新しい名前:");
    for (int i = 0; i < newNames.Length; i++)
    {
        Console.WriteLine($"  [{i}] {originalNames[i]}  ->  {newNames[i]}");
    }

    // --- 4. 新しい名前を設定 --------------------------------------------------
    bool setStatus = packAndGo.SetDocumentSaveToNames(newNames);
    Console.WriteLine();
    Console.WriteLine($"SetDocumentSaveToNames status={setStatus}");

    // --- 5. 保存前の最終確認 ---------------------------------------------------
    bool getStatus = packAndGo.GetDocumentSaveToNames(out object confirmNamesObj, out object confirmStatusObj);
    var confirmNames = (string[])confirmNamesObj;
    var confirmStatuses = (int[])confirmStatusObj;

    Console.WriteLine();
    Console.WriteLine("保存前の最終確認 (GetDocumentSaveToNames):");
    for (int i = 0; i < confirmNames.Length; i++)
    {
        Console.WriteLine($"  [{i}] {confirmNames[i]}  status={DescribeSaveStatus((swPackAndGoSaveStatus_e)confirmStatuses[i])}");
    }

    Console.WriteLine();
    Console.Write("この内容で SavePackAndGo を実行しますか？ (y/N): ");
    var answer = Console.ReadLine();
    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("キャンセルしました。");
        return;
    }

    // --- 6. 実行 -----------------------------------------------------------
    var saveResultObj = ext.SavePackAndGo(packAndGo);
    var saveResults = (int[])saveResultObj;

    Console.WriteLine();
    Console.WriteLine("SavePackAndGo 結果:");
    for (int i = 0; i < saveResults.Length; i++)
    {
        var statusEnum = (swPackAndGoSaveStatus_e)saveResults[i];
        var ok = statusEnum == swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed;
        Console.WriteLine($"  [{i}] {(ok ? "OK " : "NG ")} {confirmNames[i]}  ({DescribeSaveStatus(statusEnum)})");
    }
}

sealed class GraphNode
{
    public GraphNode(string filePath) => FilePath = filePath;
    public string FilePath { get; }
    public List<GraphNode> Parents { get; } = new();
    public bool IsOutOfScope { get; set; }
    public string FileName => Path.GetFileName(FilePath);
}
