using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Jnl.WallPaperSync;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow
{
    private const uint WmSpawnWorkerW = 0x052C;
    private const uint SmtoNormal = 0x0000;
    private const uint PwRenderFullContent = 0x00000002;
    private const int SwHide = 0;
    private const int SwShow = 5;

    private BitmapSource? _currentWallpaper;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWallpaper();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            RefreshWallpaper();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void SyncLockScreen_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (_currentWallpaper is null)
            {
                MessageBox.Show(this, "Capture a wallpaper image before syncing the lock screen.",
                    "Sync lock screen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Jnl", "Wallpaper");
            Directory.CreateDirectory(folder);
            var filePath = Path.Combine(folder, "Wallpaper.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_currentWallpaper));
            using (var stream = File.Create(filePath))
            {
                encoder.Save(stream);
            }

            var file = await StorageFile.GetFileFromPathAsync(filePath);
            await LockScreen.SetImageFileAsync(file);

            MessageBox.Show(this, "The lock screen image has been updated.", "Sync lock screen",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to sync the lock screen image.\n\n{exception.Message}",
                "Sync lock screen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Captures the current desktop wallpaper and applies it as this window's
    /// background, remembering it so it can be saved/synced later.
    /// </summary>
    private void RefreshWallpaper()
    {
        try
        {
            _currentWallpaper = CaptureDesktopWallpaper();
            Background = new ImageBrush(_currentWallpaper)
            {
                Stretch = Stretch.UniformToFill
            };
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"Unable to capture the desktop wallpaper.\n\n{exception.Message}",
                "Desktop wallpaper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Captures whatever is currently rendered on the desktop's wallpaper layer
    /// (the Progman/WorkerW window), which works even when the background is
    /// generated dynamically by a third-party application rather than set via
    /// the standard Windows wallpaper file/registry mechanism.
    /// </summary>
    private static BitmapSource CaptureDesktopWallpaper()
    {
        var wallpaperWindow = FindWallpaperWindow();
        var desktopIconsWindow = FindDesktopIconsWindow();

        var iconsWereHidden = false;
        try
        {
            if (desktopIconsWindow == IntPtr.Zero) return CaptureWindow(wallpaperWindow);
            
            // Briefly hide the desktop icons list view so it doesn't show
            // up in the captured image, then restore it once we're done.
            ShowWindow(desktopIconsWindow, SwHide);
            iconsWereHidden = true;
            Thread.Sleep(50);

            return CaptureWindow(wallpaperWindow);
        }
        finally
        {
            if (iconsWereHidden)
            {
                ShowWindow(desktopIconsWindow, SwShow);
            }
        }
    }

    private static BitmapSource CaptureWindow(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The desktop wallpaper window has no visible area.");
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            oldBitmap = SelectObject(memoryDc, bitmap);

            if (!PrintWindow(window, memoryDc, PwRenderFullContent))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();
            return bitmapSource;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Locates the desktop's icon list view (the SysListView32 control hosted
    /// inside SHELLDLL_DefView) so it can be temporarily hidden while capturing
    /// the wallpaper, the same effect as toggling "Show desktop icons" off.
    /// </summary>
    private static IntPtr FindDesktopIconsWindow()
    {
        var listView = IntPtr.Zero;
        EnumWindows((topHandle, _) =>
        {
            var shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
            {
                return true;
            }

            var candidate = FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
            if (candidate == IntPtr.Zero)
            {
                return true;
            }

            listView = candidate;
            return false;
        }, IntPtr.Zero);

        return listView;
    }

    /// <summary>
    /// Locates the WorkerW window that Windows renders the desktop wallpaper
    /// into, falling back to the Progman window itself if no separate WorkerW
    /// layer was created.
    /// </summary>
    private static IntPtr FindWallpaperWindow()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not locate the Progman window.");
        }

        // Asking Progman to spawn a WorkerW window behind the desktop icons is
        // what makes the wallpaper layer available as its own HWND.
        SendMessageTimeout(progman, WmSpawnWorkerW, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);

        var workerW = IntPtr.Zero;
        EnumWindows((topHandle, _) =>
        {
            var shellView = FindWindowEx(topHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                workerW = FindWindowEx(IntPtr.Zero, topHandle, "WorkerW", null);
            }

            return true;
        }, IntPtr.Zero);

        return workerW != IntPtr.Zero ? workerW : progman;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
