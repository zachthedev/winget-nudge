using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>Shared window setup: Mica, icon, size, centering and resize policy.</summary>
public static class WindowChrome
{
    private const int BaseDpi = 96;

    /// <summary>Applies the app's chrome to a window.</summary>
    /// <param name="window">Window to configure.</param>
    /// <param name="width">Width in device-independent pixels.</param>
    /// <param name="height">Height in device-independent pixels.</param>
    /// <param name="title">
    /// Window-specific name shown before the app name, so two open windows are told apart in
    /// Alt+Tab and by a screen reader. Empty for the app's main window.
    /// </param>
    /// <param name="resizable">Whether the user can resize and maximize.</param>
    public static void Apply(Window window, int width, int height, bool resizable, string title = "")
    {
        window.Title = title.Length > 0 ? $"{title} - {AppServices.DisplayName}" : AppServices.DisplayName;
        window.SystemBackdrop = new MicaBackdrop();

        AppWindow appWindow = window.AppWindow;
        appWindow.SetIcon(AppServices.Current.IconIcoPath);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
        }

        HWND hwnd = new(WinRT.Interop.WindowNative.GetWindowHandle(window));
        double scale = PInvoke.GetDpiForWindow(hwnd) / (double)BaseDpi;
        SizeInt32 size = new((int)(width * scale), (int)(height * scale));
        appWindow.Resize(size);

        DisplayArea area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 work = area.WorkArea;
        appWindow.Move(
            new PointInt32(work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2)
        );
    }
}
