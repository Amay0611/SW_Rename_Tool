using SwRenameTool.Core.Models;

namespace SwRenameTool.Core.Services;

/// <summary>
/// 選択された最上位アセンブリを起点に、GetDependencies2で参照関係を辿り
/// FileNodeグラフを構築する。フォルダ全体ではなく、このアセンブリの依存範囲のみを対象とする。
/// </summary>
public interface IReferenceGraphBuilder
{
    /// <param name="topAssemblyPath">ユーザーが選択した最上位アセンブリ(.sldasm)のパス。</param>
    Task<IReadOnlyList<FileNode>> BuildAsync(string topAssemblyPath, CancellationToken ct = default);
}
