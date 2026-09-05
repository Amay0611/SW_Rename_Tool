using System.IO;
using Microsoft.Win32;

namespace SwRenameTool.Core.Services;

public sealed class ToolboxDetector : IToolboxDetector
{
    // 実際のレジストリキーは以下（実機・公式情報で確認済み）:
    //   HKEY_CURRENT_USER\Software\SolidWorks\SOLIDWORKS <年度>\General
    //   値名: "Toolbox Data Location"
    // 年度部分（例: "SOLIDWORKS 2024"）はインストールされているSolidWorksのバージョンにより
    // 異なり、かつ複数バージョンが同居している場合もあるため、
    // "Software\SolidWorks" 直下のサブキーを走査して該当するものを全て拾う。
    //
    // 当初は "Software\SolidWorks\Applications\Toolbox\BrowserPath" という誤ったキーを
    // 参照しており、実際のToolboxパスが取得できていなかった（常に検出0件になっていた）。
    private const string SolidWorksRegistryRoot = @"Software\SolidWorks";
    private const string ToolboxValueName = "Toolbox Data Location";

    public IReadOnlyList<string> ToolboxRootPaths { get; }

    public ToolboxDetector()
    {
        ToolboxRootPaths = DetectFromRegistry();
    }

    private static IReadOnlyList<string> DetectFromRegistry()
    {
        var paths = new List<string>();

        try
        {
            using var swRoot = Registry.CurrentUser.OpenSubKey(SolidWorksRegistryRoot);
            if (swRoot == null)
            {
                return paths;
            }

            // "SOLIDWORKS 2024" のようなバージョンごとのサブキーを走査する。
            foreach (var versionKeyName in swRoot.GetSubKeyNames())
            {
                using var generalKey = swRoot.OpenSubKey($@"{versionKeyName}\General");
                var toolboxPath = generalKey?.GetValue(ToolboxValueName) as string;

                if (!string.IsNullOrWhiteSpace(toolboxPath) &&
                    Directory.Exists(toolboxPath) &&
                    !paths.Contains(toolboxPath, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(toolboxPath);
                }
            }
        }
        catch (Exception)
        {
            // レジストリが読めない場合はToolbox未導入とみなし、空リストのまま続行する。
            // 呼び出し側で「Toolbox検出なし」を設定画面に表示する。
        }

        return paths;
    }

    public bool IsToolboxFile(string filePath)
    {
        if (ToolboxRootPaths.Count == 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);

        return ToolboxRootPaths.Any(root =>
            fullPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }
}
