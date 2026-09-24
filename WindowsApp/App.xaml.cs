using Microsoft.UI.Xaml;
using System;
using System.IO;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;

namespace RainbowRecoil;

public partial class App : Application
{
    private Window? _window;
    private MainPage? _mainPage;
    private PointInt32 _normalPosition;
    private SizeInt32 _normalSize;
    private bool _isOverlayMode;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
            LogStartupException("XAML", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            LogStartupException("AppDomain", eventArgs.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainPage = new MainPage();
        _window = new Window
        {
            Title = "ZeroSense",
            Content = _mainPage
        };
        ConfigureWindowChrome(_window.AppWindow);
        SetWindowIcon(_window.AppWindow);
        _window.AppWindow.Closing += AppWindow_Closing;
        _window.AppWindow.Resize(new SizeInt32(1180, 800));
        _window.Activate();
        _mainPage.AttachPhysicalMouseInput(WinRT.Interop.WindowNative.GetWindowHandle(_window));

        if (Array.Exists(
            Environment.GetCommandLineArgs(),
            argument => argument.Equals("--overlay", StringComparison.OrdinalIgnoreCase)))
        {
            SetOverlayMode(true);
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Number boxes use a short debounce while the user is editing. Flush any
        // pending master, per-weapon, or horizontal change before process exit.
        _mainPage?.Dispose();
    }

    public void ToggleOverlay() => SetOverlayMode(!_isOverlayMode);

    public void SetOverlayMode(bool enabled)
    {
        if (_window is null || _mainPage is null || enabled == _isOverlayMode)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (enabled)
        {
            _normalPosition = appWindow.Position;
            _normalSize = appWindow.Size;
            presenter.Restore();
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
            var overlaySize = new SizeInt32(430, 330);
            appWindow.Resize(overlaySize);

            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            appWindow.Move(new PointInt32(
                workArea.X + Math.Max(12, workArea.Width - overlaySize.Width - 18),
                workArea.Y + 18));
            appWindow.Title = "ZeroSense Overlay";
        }
        else
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            if (_normalSize.Width > 0 && _normalSize.Height > 0)
            {
                appWindow.Resize(_normalSize);
                appWindow.Move(_normalPosition);
            }
            appWindow.Title = "ZeroSense";
        }

        _isOverlayMode = enabled;
        _mainPage.SetOverlayVisualMode(enabled);
        _window.Activate();
    }

    private static void ConfigureWindowChrome(AppWindow appWindow)
    {
        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = Color.FromArgb(255, 8, 8, 8);
        titleBar.ForegroundColor = Color.FromArgb(255, 255, 255, 255);
        titleBar.InactiveBackgroundColor = Color.FromArgb(255, 10, 10, 10);
        titleBar.InactiveForegroundColor = Color.FromArgb(255, 125, 125, 125);
        titleBar.ButtonBackgroundColor = Color.FromArgb(255, 8, 8, 8);
        titleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 28, 28, 28);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 42, 42, 42);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 10, 10, 10);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 100, 100, 100);
    }

    private static void SetWindowIcon(AppWindow appWindow)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "zerosense.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch (Exception exception)
        {
            LogStartupException("Icon", exception);
        }
    }

    private static void LogStartupException(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RainbowRecoil");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup-error.log"),
                $"{DateTimeOffset.Now:O} [{source}] {exception}\n");
        }
        catch
        {
        }
    }
}
