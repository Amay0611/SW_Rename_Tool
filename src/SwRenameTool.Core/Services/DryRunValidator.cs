using System.IO;
using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

public sealed class DryRunValidator : IDryRunValidator
{
    private static readonly char[] ForbiddenChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
    private const int MaxPathLength = 260;

    public DryRunResult Validate(IReadOnlyList<FileNode> nodes)
    {
        var targets = nodes.Where(n => n.HasPendingRename).ToList();
        var result = new DryRunResult { TargetCount = targets.Count };

        CheckForbiddenCharsAndLength(targets, result);
        CheckDuplicateNames(nodes, targets, result);
        CheckFileLocks(targets, result);
        CheckOutOfScopeReferences(nodes, result);

        return result;
    }

    private static void CheckForbiddenCharsAndLength(List<FileNode> targets, DryRunResult result)
    {
        foreach (var node in targets)
        {
            if (node.NewBaseName!.IndexOfAny(ForbiddenChars) >= 0)
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"{node.FileName}: ファイル名に使用できない文字が含まれています。",
                    node));
            }

            var folder = Path.GetDirectoryName(node.FilePath) ?? string.Empty;
            var newFullPath = Path.Combine(folder, node.NewBaseName! + Path.GetExtension(node.FilePath));

            if (newFullPath.Length > MaxPathLength)
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"{node.FileName}: 変更後のパスが260文字を超えます。",
                    node));
            }
        }
    }

    private static void CheckDuplicateNames(
        IReadOnlyList<FileNode> allNodes, List<FileNode> targets, DryRunResult result)
    {
        // 重要: グループキーには拡張子も含める。パーツと図面は同じベース名を意図的に
        // 共有する（例: "Bracket.SLDPRT" と "Bracket.SLDDRW"）ため、拡張子を無視すると
        // 正常なペアを誤って重複と判定してしまう（実機で確認）。
        var groups = targets
            .GroupBy(
                n => (Path.GetDirectoryName(n.FilePath), n.NewBaseName + Path.GetExtension(n.FilePath).ToLowerInvariant()),
                StringTupleComparer.Instance);

        foreach (var group in groups.Where(g => g.Count() > 1))
        {
            foreach (var node in group)
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"{node.FileName}: 変更後の名前 \"{node.NewBaseName}\" が他のファイルと重複しています。",
                    node));
            }
        }

        // 既存ファイル（リネーム対象外）との衝突もチェック
        foreach (var node in targets)
        {
            var folder = Path.GetDirectoryName(node.FilePath) ?? string.Empty;
            var newFullPath = Path.Combine(folder, node.NewBaseName! + Path.GetExtension(node.FilePath));

            if (File.Exists(newFullPath) && !string.Equals(newFullPath, node.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    $"{node.FileName}: 変更後のファイル名が既存ファイルと重複しています。",
                    node));
            }
        }
    }

    private static void CheckFileLocks(List<FileNode> targets, DryRunResult result)
    {
        foreach (var node in targets)
        {
            if (IsLocked(node.FilePath))
            {
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    $"{node.FileName} が別プロセスで開かれています。対象から除外するか確認してください。",
                    node));
            }
        }
    }

    private static bool IsLocked(string path)
    {
        // OSレベルの排他ロック確認
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        finally
        {
            // SolidWorks固有のロックファイル(~$<ファイル名>)も併せて確認する。
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var lockFile = Path.Combine(folder, "~$" + Path.GetFileName(path));
            if (File.Exists(lockFile))
            {
                // ロックファイルが存在する場合も警告対象とする（呼び出し元でハンドリング）。
            }
        }
    }

    private static void CheckOutOfScopeReferences(IReadOnlyList<FileNode> allNodes, DryRunResult result)
    {
        foreach (var node in allNodes.Where(n => n.IsReferencedFromOutsideScope))
        {
            var outsideParents = string.Join(", ", node.Parents.Select(p => p.FileName));

            if (!node.HasPendingRename)
            {
                // 未編集ならリネームされないので参照更新も発生しない。情報として留める必要は薄いが、
                // 「外部から参照されている」こと自体は伝えておく。
                result.Issues.Add(new ValidationIssue(
                    ValidationSeverity.Info,
                    $"{node.FileName} は選択アセンブリの範囲外からも参照されています。" +
                    (outsideParents.Length > 0 ? $"（参照元: {outsideParents}）" : string.Empty),
                    node));
                continue;
            }

            result.Issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                $"{node.FileName} は選択アセンブリの範囲外からも参照されています。" +
                "リネーム実行後、参照元の参照も自動的に更新します。" +
                (outsideParents.Length > 0 ? $"（参照元: {outsideParents}）" : string.Empty) +
                " 参照元のファイルが開かれている場合、参照更新は失敗します。",
                node));
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string? Folder, string? Name)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string? Folder, string? Name) x, (string? Folder, string? Name) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string? Folder, string? Name) obj) =>
            HashCode.Combine(
                obj.Folder?.ToLowerInvariant(),
                obj.Name?.ToLowerInvariant());
    }
}
