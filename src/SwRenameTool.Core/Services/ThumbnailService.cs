using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SwRenameTool.Core.Services;

public sealed class ThumbnailService : IThumbnailService
{
    public Task<BitmapSource?> GetThumbnailAsync(string filePath, int size = 64)
    {
        // Shell APIの呼び出しはSTAスレッド前提のため、呼び出し元（UIスレッド）で
        // 同期的に実行する。ファイルI/O・COM呼び出しは軽量なので、
        // Task.FromResultで包んで非同期メソッドの形にしているだけで、
        // 別スレッドには移していない（意図的）。
        return Task.FromResult(GetThumbnailCore(filePath, size));
    }

    private static BitmapSource? GetThumbnailCore(string filePath, int size)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        IShellItemImageFactory? factory = null;
        var hBitmap = IntPtr.Zero;

        try
        {
            var riid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref riid, out factory);
            if (hr != 0 || factory == null)
            {
                return null;
            }

            var sizeStruct = new SIZE { cx = size, cy = size };
            // 重要: ThumbnailOnly と IconOnly は意味的に矛盾するフラグ（片方は「実サムネイルのみ」、
            // もう片方は「アイコンのみ」を強制する）。両方同時に指定するとGetImageがエラーを
            // 返すことを実機で確認した。ThumbnailOnly + BiggerSizeOk のみを指定する。
            hr = factory.GetImage(sizeStruct, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                // 実サムネイルが取得できない場合は、アイコン表示にフォールバックする
                // （何も表示されないよりはファイル種別が分かるアイコンだけでも表示する）。
                hr = factory.GetImage(sizeStruct, SIIGBF.BiggerSizeOk, out hBitmap);
            }
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze(); // スレッド間で使い回せるようにフリーズする
            return bitmapSource;
        }
        catch
        {
            // サムネイルが取得できないファイルもあり得るため、例外は握りつぶしてnullを返す。
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (factory != null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    // --- Windows Shell API の P/Invoke 宣言 ---

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x0,
        BiggerSizeOk = 0x1,
        MemoryOnly = 0x2,
        IconOnly = 0x4,
        ThumbnailOnly = 0x8,
        InCacheOnly = 0x10,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
