using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace RainbowRecoil;

/// <summary>Main UI, detection, overlay, and device lifecycle coordinator.</summary>
public sealed partial class MainPage : UserControl, IDisposable
{
    private static readonly SolidColorBrush DisconnectedBrush = Brush(0x62, 0x62, 0x62);
    private static readonly SolidColorBrush ConnectedBrush = Brush(0x45, 0xD4, 0x83);
    private static readonly SolidColorBrush WarningBrush = Brush(0xD8, 0xB4, 0x5A);
    private static readonly SolidColorBrush ErrorBrush = Brush(0xEF, 0x6A, 0x6A);
    private static readonly SolidColorBrush SelectedNavBrush = Brush(0x21, 0x7C, 0x5C, 0xFF);
    private static readonly SolidColorBrush TransparentBrush = Brush(0, 0, 0, 0);

    private readonly OperatorSelectorViewModel _viewModel = new();
    private readonly MouseButtonTrigger _mouseButtonTrigger = new();
    private readonly GlobalHotkeyMonitor _hotkeyMonitor = new();
    private readonly OperatorDetectionService _operatorDetectionService = new();
    private readonly WeaponDetectionService _weaponDetectionService = new();
    private readonly DispatcherTimer _continuousDetectionTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private readonly DispatcherTimer _rp2350ArmLeaseTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _configurationSyncLock = new(1, 1);
    private readonly DetectionDebouncer _operatorDetectionDebouncer = new();
    private readonly DetectionDebouncer _weaponDetectionDebouncer = new();
    private readonly IReadOnlyList<CompensationModeOption> _modeOptions =
    [
        new(CompensationMode.General, "General · constant adjustment"),
        new(CompensationMode.WeaponPattern, "Weapon pattern · estimated"),
        new(CompensationMode.Experimental, "Experimental · per-weapon calibrated")
    ];
    private readonly IReadOnlyList<DetectionModeOption> _detectionModeOptions =
    [
        new(OperatorDetectionMode.Disabled, "Disabled", "Screen recognition is off."),
        new(OperatorDetectionMode.Continuous, "Continuous detection", "Checks every two seconds while another app is active."),
        new(OperatorDetectionMode.Keybind, "Keybind detection", "Captures once when the configured shortcut is pressed.")
    ];

    private IRecoilDeviceConnection? _connection;
    // True before InitializeComponent so RangeBase/selection events raised while
    // XAML is constructing controls cannot touch sibling elements prematurely.
    private bool _isInitializing = true;
    private bool _isConnecting;
    private bool _isArmed;
    private bool _isDetecting;
    private bool _isWeaponDetecting;
    private bool _isRefreshingWeapons;
    private bool _isSynchronizingConfiguration;
    private bool _outputActive;
    private bool _rp2350ArmLeaseSent;
    private bool _isLoaded;
    private bool _isDisposed;
    private int _configurationRevision;
    private CalibrationSnapshot? _calibrationUndo;
    private FirmwareStatusKind? _firmwareDeviceKind;
    private readonly DeviceConfigurationSynchronizer _configurationSynchronizer = new();
    private FirmwareStatusKind? _physicalMouseStatus;
    private ushort _physicalMouseVendorId;
    private ushort _physicalMouseProductId;

    public MainPage()
    {
        InitializeComponent();
        DiagnosticLog.Record("app", "Main page initialized.");
        InitializeSelectors();
        ApplySavedSettings();
        SelectNavigationPage("Overview");

        _mouseButtonTrigger.AimAndFireChanged += MouseButtonTrigger_AimAndFireChanged;
        _mouseButtonTrigger.AimAndFireHeartbeat += MouseButtonTrigger_AimAndFireHeartbeat;
        _hotkeyMonitor.OverlayChordProvider = GetOverlayChord;
        _hotkeyMonitor.DetectionChordProvider = GetDetectionChord;
        _hotkeyMonitor.OverlayEnabledProvider = () => SettingsManager.LoadSettings().OverlayEnabled;
        _hotkeyMonitor.DetectionEnabledProvider = () =>
            SettingsManager.LoadSettings().OperatorDetectionMode == OperatorDetectionMode.Keybind;
        _hotkeyMonitor.WeaponSlotHotkeysEnabledProvider = () =>
            SettingsManager.LoadSettings().WeaponSlotHotkeysEnabled &&
            ScreenCaptureService.IsRainbowSixForeground();
        _hotkeyMonitor.OverlayPressed += HotkeyMonitor_OverlayPressed;
        _hotkeyMonitor.DetectionPressed += HotkeyMonitor_DetectionPressed;
        _hotkeyMonitor.PrimaryWeaponPressed += HotkeyMonitor_PrimaryWeaponPressed;
        _hotkeyMonitor.SecondaryWeaponPressed += HotkeyMonitor_SecondaryWeaponPressed;
        _continuousDetectionTimer.Tick += ContinuousDetectionTimer_Tick;
        _rp2350ArmLeaseTimer.Tick += Rp2350ArmLeaseTimer_Tick;
    }

    private void InitializeSelectors()
    {
        _isInitializing = true;
        try
        {
            OperatorSelector.ItemsSource = _viewModel.AllOperators;
            CompensationModeSelector.ItemsSource = _modeOptions;
            DetectionModeSelector.ItemsSource = _detectionModeOptions;
            MagnificationSelector.ItemsSource = new[] { "1.0x", "2.5x", "3.5x", "8.0x" };
            DetectionModifierSelector.ItemsSource = HotkeyChord.Modifiers;
            DetectionKeySelector.ItemsSource = HotkeyChord.Keys;
            OverlayModifierSelector.ItemsSource = HotkeyChord.Modifiers;
            OverlayKeySelector.ItemsSource = HotkeyChord.Keys;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void ApplySavedSettings()
    {
        _isInitializing = true;
        try
        {
            var settings = SettingsManager.LoadSettings();
            MouseDpiBox.Value = settings.MouseDpi ?? 1600;
            HorizontalSensitivityBox.Value = settings.HorizontalSensitivity ?? 55.0f;
            HorizontalSensitivitySlider.Value = HorizontalSensitivityBox.Value;
            VerticalSensitivityBox.Value = settings.VerticalSensitivity ?? 55.0f;
            VerticalSensitivitySlider.Value = VerticalSensitivityBox.Value;
            SensitivityMultiplierBox.Value = settings.MouseSensitivityMultiplierUnit;

            MagnificationSelector.SelectedItem = settings.ActiveMagnification;
            if (MagnificationSelector.SelectedIndex < 0)
            {
                MagnificationSelector.SelectedItem = "1.0x";
            }
            AutomaticMagnificationToggle.IsOn = settings.AutomaticMagnificationEnabled;
            AdsSensitivityBox.Value = settings.GetActiveAdsSensitivity();

            CompensationModeSelector.SelectedItem = _modeOptions.First(option =>
                option.Value == settings.CompensationMode);
            DetectionModeSelector.SelectedItem = _detectionModeOptions.First(option =>
                option.Value == settings.OperatorDetectionMode);
            AutoApplyDetectionToggle.IsOn = settings.AutoApplyDetectedOperator;
            ConfidenceSlider.Value = settings.OperatorDetectionConfidence * 100.0;
            DetectionRegionXBox.Value = settings.DetectionRegionX;
            DetectionRegionYBox.Value = settings.DetectionRegionY;
            DetectionRegionWidthBox.Value = settings.DetectionRegionWidth;
            DetectionRegionHeightBox.Value = settings.DetectionRegionHeight;
            WeaponDetectionToggle.IsOn = settings.WeaponDetectionEnabled;
            AutoApplyWeaponDetectionToggle.IsOn = settings.AutoApplyDetectedWeapon;
            WeaponSlotHotkeysToggle.IsOn = settings.WeaponSlotHotkeysEnabled;
            RapidFireToggle.IsOn = settings.RapidFireEnabled;
            WeaponConfidenceSlider.Value = settings.WeaponDetectionConfidence * 100.0;
            WeaponDetectionRegionXBox.Value = settings.WeaponDetectionRegionX;
            WeaponDetectionRegionYBox.Value = settings.WeaponDetectionRegionY;
            WeaponDetectionRegionWidthBox.Value = settings.WeaponDetectionRegionWidth;
            WeaponDetectionRegionHeightBox.Value = settings.WeaponDetectionRegionHeight;
            DetectionModifierSelector.SelectedItem = settings.DetectionHotkeyModifier;
            DetectionKeySelector.SelectedItem = settings.DetectionHotkeyKey;
            OverlayEnabledToggle.IsOn = settings.OverlayEnabled;
            OverlayModifierSelector.SelectedItem = settings.OverlayHotkeyModifier;
            OverlayKeySelector.SelectedItem = settings.OverlayHotkeyKey;

            var selectedOperator = _viewModel.AllOperators.FirstOrDefault(item =>
                item.OperatorName.Equals(settings.LastOperator, StringComparison.OrdinalIgnoreCase))
                ?? _viewModel.AllOperators.FirstOrDefault();
            OperatorSelector.SelectedItem = selectedOperator;
            RefreshWeapons(settings.LastWeapon);
            ApplyMagnificationPolicy(settings);
        }
        finally
        {
            _isInitializing = false;
        }

        UpdateProfileDescription();
        UpdateModeDescription();
        UpdateExperimentalTuningUi();
        UpdateDetectionUi();
        UpdateHotkeyPreviews();
        UpdateOverlayContent();

        var configurationWarning = WeaponProfile.LastLoadError ?? SettingsManager.LastError;
        if (!string.IsNullOrWhiteSpace(configurationWarning))
        {
            DeviceMessageText.Text = configurationWarning;
            DevicePageConnectionDetail.Text = configurationWarning;
        }
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed || _isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _mouseButtonTrigger.Start();
        _hotkeyMonitor.Start();
        _continuousDetectionTimer.Start();
        _rp2350ArmLeaseTimer.Start();
        await ConnectToDeviceAsync(_lifetimeCancellation.Token);
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e) => Dispose();

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            SelectNavigationPage(page);
        }
    }

    private void OpenDetectionPage_Click(object sender, RoutedEventArgs e) =>
        SelectNavigationPage("Detection");

    private void SelectNavigationPage(string page)
    {
        OverviewPage.Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        DetectionPage.Visibility = page == "Detection" ? Visibility.Visible : Visibility.Collapsed;
        CalibrationPage.Visibility = page == "Calibration" ? Visibility.Visible : Visibility.Collapsed;
        DevicePage.Visibility = page == "Device" ? Visibility.Visible : Visibility.Collapsed;
        InstallationPage.Visibility = page == "Installation" ? Visibility.Visible : Visibility.Collapsed;
        CurrentPageTitle.Text = page;

        SetNavigationState(OverviewNavButton, OverviewNavIndicator, page == "Overview");
        SetNavigationState(DetectionNavButton, DetectionNavIndicator, page == "Detection");
        SetNavigationState(CalibrationNavButton, CalibrationNavIndicator, page == "Calibration");
        SetNavigationState(DeviceNavButton, DeviceNavIndicator, page == "Device");
        SetNavigationState(InstallationNavButton, InstallationNavIndicator, page == "Installation");
    }

    private void OpenInstallationGuide_Click(object sender, RoutedEventArgs e)
        => OpenBundledGuide("INSTALLATION.md", "installation", InstallationStatusText);

    private void OpenTroubleshootingGuide_Click(object sender, RoutedEventArgs e)
        => OpenBundledGuide("TROUBLESHOOTING.md", "troubleshooting", InstallationStatusText);

    private void OpenLatestRelease_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://github.com/tempted14/ZeroSense-RP2040/releases/latest")
            {
                UseShellExecute = true
            });
            InstallationStatusText.Text = "Opened the official ZeroSense release page.";
            InstallationStatusText.Foreground = ConnectedBrush;
        }
        catch (Exception exception)
        {
            InstallationStatusText.Text = $"Could not open the release page: {exception.Message}";
            InstallationStatusText.Foreground = ErrorBrush;
        }
    }

    private static void OpenBundledGuide(
        string fileName,
        string guideName,
        TextBlock statusText)
    {
        var guidePath = Path.Combine(AppContext.BaseDirectory, "Docs", fileName);
        try
        {
            if (!File.Exists(guidePath))
            {
                statusText.Text = $"The {guideName} guide is missing from this build.";
                statusText.Foreground = ErrorBrush;
                return;
            }

            Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true });
            statusText.Text = $"Opened the {guideName} guide.";
            statusText.Foreground = ConnectedBrush;
        }
        catch (Exception exception)
        {
            statusText.Text = $"Could not open the {guideName} guide: {exception.Message}";
            statusText.Foreground = ErrorBrush;
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking the official release feed…";
        try
        {
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ??
                new Version(1, 4, 0);
            var result = await new ReleaseUpdateService().CheckAsync(
                currentVersion,
                _lifetimeCancellation.Token);
            if (!result.IsUpdateAvailable)
            {
                UpdateStatusText.Text = $"ZeroSense {currentVersion.ToString(3)} is current.";
                return;
            }

            var installerStatus = result.HasInstaller
                ? "an installer is available"
                : "use the portable ZIP";
            var checksumStatus = result.HasChecksumManifest
                ? "a checksum manifest is included"
                : "no checksum manifest was found";
            UpdateStatusText.Text =
                $"ZeroSense {result.AvailableVersion.ToString(3)} is available; " +
                $"{installerStatus} and {checksumStatus}.";
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "ZeroSense update available",
                Content = UpdateStatusText.Text +
                    " Open the official release page to review it?",
                PrimaryButtonText = "Open release",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                result.ReleasePage is not null)
            {
                Process.Start(new ProcessStartInfo(result.ReleasePage.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"Update check failed: {exception.Message}";
            DiagnosticLog.Record("update-error", exception.Message);
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private static void SetNavigationState(Button button, Border indicator, bool selected)
    {
        button.Background = selected ? SelectedNavBrush : TransparentBrush;
        button.Foreground = selected ? new SolidColorBrush(Colors.White) : Brush(0xA0, 0xA0, 0xA0);
        indicator.Opacity = selected ? 1 : 0;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectToDeviceAsync(_lifetimeCancellation.Token);
    }

    private async void ConnectSimulatorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting || _isDisposed)
        {
            return;
        }
        if (_connection?.IsSimulator == true)
        {
            SetArmControls(false, false);
            DisconnectCurrentDevice();
            ConnectSimulatorButton.Content = "Use simulator";
            SetConnectionStatus(
                "Device not connected",
                "Simulator stopped. No hardware output is active.",
                DisconnectedBrush);
            DiagnosticLog.Record("device", "Simulator disconnected by user.");
            return;
        }

        _isConnecting = true;
        SetConnectButtonsEnabled(false);
        SetArmControls(false, false);
        DisconnectCurrentDevice();
        var simulator = new SimulatedRecoilDeviceConnection();
        simulator.OnCommandReceived += Connection_CommandReceived;
        simulator.OnStatusChanged += Connection_StatusChanged;
        try
        {
            await simulator.ConnectAsync(_lifetimeCancellation.Token);
            _connection = simulator;
            if (!await SendCurrentConfigurationAsync(_lifetimeCancellation.Token, false))
            {
                DisconnectCurrentDevice();
                SetConnectionStatus(
                    "Simulator rejected configuration",
                    ConfigurationStatusText.Text,
                    ErrorBrush);
                return;
            }
            SetArmControls(false, true);
            ConnectSimulatorButton.Content = "Stop simulator";
            SetConnectionStatus(
                "Simulator connected",
                "Full protocol path is active. The simulator never emits mouse input.",
                WarningBrush);
            UpdateHardwareStatusPresentation();
            DiagnosticLog.Record("device", "Simulator connected and configuration accepted.");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            simulator.Dispose();
        }
        catch (Exception exception)
        {
            simulator.Dispose();
            SetConnectionStatus("Simulator error", exception.Message, ErrorBrush);
            DiagnosticLog.Record("device-error", $"Simulator connection failed: {exception.Message}");
        }
        finally
        {
            _isConnecting = false;
            if (!_isDisposed)
            {
                SetConnectButtonsEnabled(true);
            }
        }
    }

    private async Task ConnectToDeviceAsync(CancellationToken cancellationToken)
    {
        if (_isConnecting || _isDisposed)
        {
            return;
        }

        _isConnecting = true;
        SetConnectButtonsEnabled(false);
        ConnectSimulatorButton.Content = "Use simulator";
        if (_isArmed)
        {
            TrySendCommand("STOP");
        }
        SetArmControls(false, false);
        DisconnectCurrentDevice();
        SetConnectionStatus("Scanning for device…", "Checking available serial ports.", WarningBrush);

        try
        {
            var ports = Rp2040DeviceDiscovery.FindRuntimePorts(SettingsManager.LastPort);
            if (ports.Count == 0)
            {
                SetConnectionStatus(
                    "Device not found",
                    "No serial ports are present. Check the USB cable and firmware state.",
                    DisconnectedBrush);
                DiagnosticLog.Record("device", "No serial ports were available during scan.");
                return;
            }

            var errors = new List<string>();
            foreach (var portName in ports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResetFirmwareHardwareStatus();
                SetConnectionStatus("Connecting…", $"Trying {portName}.", WarningBrush);

                var candidate = new SerialConnection(portName);
                candidate.OnCommandReceived += Connection_CommandReceived;
                candidate.OnStatusChanged += Connection_StatusChanged;
                try
                {
                    await candidate.ConnectAsync(cancellationToken);
                    _connection = candidate;
                    SettingsManager.LastPort = portName;
                    SaveSettingsWithFeedback();
                    if (!await SendCurrentConfigurationAsync(cancellationToken, false))
                    {
                        if (ReferenceEquals(_connection, candidate))
                        {
                            _connection = null;
                        }
                        candidate.OnCommandReceived -= Connection_CommandReceived;
                        candidate.OnStatusChanged -= Connection_StatusChanged;
                        candidate.Dispose();
                        errors.Add($"{portName}: connected, but initial synchronization failed.");
                        continue;
                    }
                    SetArmControls(false, CanArmConnectedHardware());
                    SetConnectButtonContent("Reconnect");
                    SetConnectionStatus(
                        $"Connected on {portName}",
                        BuildConnectedHardwareDetail(),
                        ConnectedBrush);
                    UpdateHardwareStatusPresentation();
                    DiagnosticLog.Record("device", $"Connected to verified firmware on {portName}.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    candidate.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    candidate.OnCommandReceived -= Connection_CommandReceived;
                    candidate.OnStatusChanged -= Connection_StatusChanged;
                    candidate.Dispose();
                    errors.Add($"{portName}: {ex.Message}");
                    DiagnosticLog.Record("device-error", $"{portName} connection failed: {ex.Message}");
                }
            }

            SetConnectionStatus(
                "Firmware handshake failed",
                string.Join("  ", errors),
                DisconnectedBrush);
            DiagnosticLog.Record("device-error", "All firmware handshake attempts failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetConnectionStatus("Connection cancelled", "The device scan was cancelled.", DisconnectedBrush);
        }
        catch (Exception ex)
        {
            SetConnectionStatus("Connection error", ex.Message, ErrorBrush);
            DiagnosticLog.Record("device-error", ex.Message);
        }
        finally
        {
            _isConnecting = false;
            if (!_isDisposed)
            {
                SetConnectButtonsEnabled(true);
            }
        }
    }

    private void Connection_CommandReceived(object? sender, string? message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (message is null)
            {
                HandleConnectionLost("The hardware stopped responding.");
            }
            else if (message.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                HandleFirmwareFault(message);
            }
            else if (FirmwareStatusParser.TryParse(message, out var status))
            {
                ApplyFirmwareStatus(status);
                DiagnosticLog.Record("firmware", message);
            }
            else if (!message.StartsWith("PONG:", StringComparison.Ordinal))
            {
                DeviceMessageText.Text = message;
                DevicePageConnectionDetail.Text = message;
                DiagnosticLog.Record("firmware", message);
            }
        });
    }

    private void Connection_StatusChanged(object? sender, string status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (status.StartsWith("Disconnected", StringComparison.OrdinalIgnoreCase))
            {
                HandleConnectionLost(status);
            }
        });
    }

    private void HandleConnectionLost(string detail)
    {
        SetArmControls(false, false);
        DisconnectCurrentDevice();
        SetConnectionStatus("Device disconnected", detail, DisconnectedBrush);
        DiagnosticLog.Record("device-error", $"Connection lost: {detail}");
    }

    private void HandleFirmwareFault(string detail)
    {
        var connection = _connection;
        try
        {
            connection?.SendCommand("STOP");
        }
        catch
        {
            // The normal connection-loss path will close a failed transport.
        }
        SetArmControls(false, connection?.IsConnected == true);
        SetConnectionStatus("Firmware reported an error", detail, ErrorBrush);
        DiagnosticLog.Record("firmware-error", detail);
    }

    private void ApplyFirmwareStatus(FirmwareStatusUpdate update)
    {
        switch (update.Kind)
        {
            case FirmwareStatusKind.Rp2040Device:
            case FirmwareStatusKind.Rp2350MouseProxy:
                _firmwareDeviceKind = update.Kind;
                if (update.Kind == FirmwareStatusKind.Rp2040Device)
                {
                    _physicalMouseStatus = null;
                }
                break;
            case FirmwareStatusKind.MouseConnected:
                _physicalMouseStatus = update.Kind;
                _physicalMouseVendorId = update.VendorId;
                _physicalMouseProductId = update.ProductId;
                break;
            case FirmwareStatusKind.MouseDisconnected:
            case FirmwareStatusKind.MouseUnsupported:
            case FirmwareStatusKind.MouseHostError:
                _physicalMouseStatus = update.Kind;
                _physicalMouseVendorId = 0;
                _physicalMouseProductId = 0;
                break;
        }

        if (_connection?.IsConnected == true && !_connection.IsSimulator)
        {
            var canArm = CanArmConnectedHardware();
            SetArmControls(canArm && _isArmed, canArm);
            DeviceMessageText.Text = BuildConnectedHardwareDetail();
            DevicePageConnectionDetail.Text = DeviceMessageText.Text;
        }
        UpdateHardwareStatusPresentation();
    }

    private bool CanArmConnectedHardware() =>
        _firmwareDeviceKind != FirmwareStatusKind.Rp2350MouseProxy ||
        _physicalMouseStatus == FirmwareStatusKind.MouseConnected;

    private string BuildConnectedHardwareDetail()
    {
        if (_connection?.IsSimulator == true)
        {
            return "Full protocol path is active. The simulator never emits mouse input.";
        }
        return _firmwareDeviceKind switch
        {
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseConnected =>
                $"RP2350 mouse proxy and physical mouse {_physicalMouseVendorId:X4}:{_physicalMouseProductId:X4} are online; profile and calibration are synchronized.",
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseUnsupported =>
                "RP2350 proxy is online, but the attached mouse report descriptor is unsupported. Output remains disarmed.",
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseHostError =>
                "RP2350 proxy is online, but its PIO-USB host reported an error. Output remains disarmed.",
            FirmwareStatusKind.Rp2350MouseProxy =>
                "RP2350 proxy is online. Connect the mouse to the female PIO-USB port to enable output.",
            FirmwareStatusKind.Rp2040Device =>
                "RP2040-Zero is online; profile and calibration are synchronized.",
            _ => "Verified firmware is online; profile and calibration are synchronized."
        };
    }

    private void ResetFirmwareHardwareStatus()
    {
        _firmwareDeviceKind = null;
        _physicalMouseStatus = null;
        _physicalMouseVendorId = 0;
        _physicalMouseProductId = 0;
        UpdateHardwareStatusPresentation();
    }

    private void UpdateHardwareStatusPresentation()
    {
        if (_connection?.IsSimulator == true)
        {
            HardwareIdentityText.Text = "Hardware: in-process simulator";
            PhysicalMouseStatusText.Text = "Physical mouse: not used; simulator emits no HID input";
            return;
        }

        HardwareIdentityText.Text = _firmwareDeviceKind switch
        {
            FirmwareStatusKind.Rp2350MouseProxy => "Hardware: Waveshare RP2350-USB-C mouse proxy",
            FirmwareStatusKind.Rp2040Device => "Hardware: TENSTAR/Waveshare RP2040-Zero",
            _ => "Hardware: waiting for firmware identity"
        };
        PhysicalMouseStatusText.Text = _firmwareDeviceKind switch
        {
            FirmwareStatusKind.Rp2040Device =>
                "Physical mouse: connected directly to Windows (separate from RP2040)",
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseConnected =>
                $"Physical mouse: {_physicalMouseVendorId:X4}:{_physicalMouseProductId:X4} · forwarding movement, 8 buttons, wheel, and pan",
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseUnsupported =>
                "Physical mouse: detected, but its HID report layout could not be decoded",
            FirmwareStatusKind.Rp2350MouseProxy when
                _physicalMouseStatus == FirmwareStatusKind.MouseHostError =>
                "Physical mouse: PIO-USB host error; check the cable and CC source configuration",
            FirmwareStatusKind.Rp2350MouseProxy =>
                "Physical mouse: disconnected from the female PIO-USB port",
            _ => "Physical mouse: status will appear after the firmware handshake"
        };
    }

    private void OperatorSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || OperatorSelector.SelectedItem is not OperatorViewModel selected)
        {
            return;
        }

        SettingsManager.LastOperator = selected.OperatorName;
        RefreshWeapons(SettingsManager.LastWeapon);
        ApplyMagnificationPolicy(SettingsManager.LoadSettings());
        UpdateProfileDescription();
        UpdateExperimentalTuningUi();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void RefreshWeapons(string? preferredWeapon)
    {
        var selectedOperator = OperatorSelector.SelectedItem as OperatorViewModel;
        var operatorName = selectedOperator?.OperatorName;
        var profiles = WeaponSlotCatalog.ForOperator(_viewModel.AllProfiles, operatorName);

        var settings = SettingsManager.LoadSettings();
        var rememberedPrimary = !string.IsNullOrWhiteSpace(operatorName) &&
                                settings.PrimaryWeaponsByOperator.TryGetValue(operatorName, out var remembered)
            ? remembered
            : null;
        var selected = WeaponSlotCatalog.SelectDefault(
                profiles,
                WeaponSlot.Primary,
                rememberedPrimary,
                preferredWeapon,
                selectedOperator?.PrimaryWeaponName)
            ?? profiles.FirstOrDefault();

        _isRefreshingWeapons = true;
        try
        {
            WeaponSelector.ItemsSource = profiles;
            WeaponSelector.SelectedItem = selected;
        }
        finally
        {
            _isRefreshingWeapons = false;
        }
    }

    private void WeaponSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isRefreshingWeapons ||
            WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            return;
        }

        SettingsManager.LastWeapon = selected.Name;
        RememberWeaponSelection(selected);
        ApplyMagnificationPolicy(SettingsManager.LoadSettings());
        UpdateProfileDescription();
        UpdateExperimentalTuningUi();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void RememberWeaponSelection(WeaponProfileViewModel selected)
    {
        var operatorName = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        var map = WeaponSlotCatalog.GetSlot(selected.Profile) == WeaponSlot.Primary
            ? settings.PrimaryWeaponsByOperator
            : settings.SecondaryWeaponsByOperator;
        map[operatorName] = selected.Name;
    }

    private void SelectWeaponSlot(WeaponSlot slot)
    {
        if (WeaponSelector.ItemsSource is not IEnumerable<WeaponProfileViewModel> profilesSource)
        {
            return;
        }

        var profiles = profilesSource.ToArray();
        var settings = SettingsManager.LoadSettings();
        var selectedOperator = OperatorSelector.SelectedItem as OperatorViewModel;
        var operatorName = selectedOperator?.OperatorName;
        var remembered = !string.IsNullOrWhiteSpace(operatorName) &&
                         (slot == WeaponSlot.Primary
                             ? settings.PrimaryWeaponsByOperator
                             : settings.SecondaryWeaponsByOperator)
                         .TryGetValue(operatorName, out var rememberedName)
            ? rememberedName
            : null;
        var selected = WeaponSlotCatalog.SelectDefault(
            profiles,
            slot,
            remembered,
            null,
            selectedOperator?.PrimaryWeaponName);
        if (selected is null)
        {
            SetWeaponDetectionStatus(
                "Slot unavailable",
                $"No {slot.ToString().ToLowerInvariant()} weapon is listed for this operator.",
                WarningBrush);
            return;
        }

        if (WeaponSelector.SelectedItem != selected)
        {
            WeaponSelector.SelectedItem = selected;
        }
        else
        {
            UpdateProfileDescription();
            UpdateOverlayContent();
        }
    }

    private bool SelectWeapon(string weaponName)
    {
        if (WeaponSelector.ItemsSource is not IEnumerable<WeaponProfileViewModel> profiles)
        {
            return false;
        }

        var match = profiles.FirstOrDefault(profile =>
            profile.Name.Equals(weaponName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        if (WeaponSelector.SelectedItem != match)
        {
            WeaponSelector.SelectedItem = match;
        }
        return true;
    }

    private void CompensationModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || CompensationModeSelector.SelectedItem is not CompensationModeOption selected)
        {
            return;
        }

        SettingsManager.CompensationMode = selected.Value;
        UpdateModeDescription();
        UpdateExperimentalTuningUi();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void MagnificationSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || MagnificationSelector.SelectedItem is not string magnification)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.ActiveMagnification = magnification;
        _isInitializing = true;
        AdsSensitivityBox.Value = settings.GetActiveAdsSensitivity();
        _isInitializing = false;
        ApplyMagnificationPolicy(settings);
        SaveAndSynchronize();
    }

    private void AutomaticMagnificationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.AutomaticMagnificationEnabled = AutomaticMagnificationToggle.IsOn;
        ApplyMagnificationPolicy(settings);
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void ApplyMagnificationPolicy(Settings settings)
    {
        var automatic = settings.AutomaticMagnificationEnabled;
        MagnificationSelector.IsEnabled = !automatic;
        if (automatic)
        {
            var operatorName = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
            var weaponName = (WeaponSelector.SelectedItem as WeaponProfileViewModel)?.Name;
            settings.ActiveMagnification = OpticMagnificationPolicy.Recommend(operatorName, weaponName);
        }

        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            MagnificationSelector.SelectedItem = settings.ActiveMagnification;
            AdsSensitivityBox.Value = settings.GetActiveAdsSensitivity();
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        var activeOperator = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
        var side = OpticMagnificationPolicy.GetSide(activeOperator);
        AutomaticMagnificationStatusText.Text = automatic
            ? side == OperatorSide.Attacker
                ? $"Attacker default · {settings.ActiveMagnification}"
                : settings.ActiveMagnification == "2.5x"
                    ? "Defender DMR exception · 2.5x"
                    : "Defender default · 1.0x"
            : $"Manual override · {settings.ActiveMagnification}";
        UpdateCalibrationSummary(settings);
    }

    private void CalibrationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        if (ReferenceEquals(sender, HorizontalSensitivitySlider))
        {
            HorizontalSensitivityBox.Value = e.NewValue;
        }
        else if (ReferenceEquals(sender, VerticalSensitivitySlider))
        {
            VerticalSensitivityBox.Value = e.NewValue;
        }
        _isInitializing = false;
        SaveCalibrationFromUi();
    }

    private void Calibration_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        _isInitializing = true;
        if (sender == HorizontalSensitivityBox && double.IsFinite(sender.Value))
        {
            HorizontalSensitivitySlider.Value = sender.Value;
        }
        else if (sender == VerticalSensitivityBox && double.IsFinite(sender.Value))
        {
            VerticalSensitivitySlider.Value = sender.Value;
        }
        _isInitializing = false;
        SaveCalibrationFromUi();
    }

    private void Calibration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            SaveCalibrationFromUi();
        }
    }

    private void SaveCalibrationFromUi()
    {
        var settings = SettingsManager.LoadSettings();
        settings.AutomaticMagnificationEnabled = AutomaticMagnificationToggle.IsOn;
        settings.MouseDpi = ValidInt(MouseDpiBox.Value, settings.MouseDpi ?? 1600);
        settings.HorizontalSensitivity = ValidFloat(
            HorizontalSensitivityBox.Value,
            settings.HorizontalSensitivity ?? 55.0f);
        settings.VerticalSensitivity = ValidFloat(
            VerticalSensitivityBox.Value,
            settings.VerticalSensitivity ?? 55.0f);
        settings.MouseSensitivityMultiplierUnit = ValidFloat(
            SensitivityMultiplierBox.Value,
            settings.MouseSensitivityMultiplierUnit);
        if (MagnificationSelector.SelectedItem is string magnification)
        {
            settings.ActiveMagnification = magnification;
            settings.AdsSensitivity[magnification] = ValidFloat(
                AdsSensitivityBox.Value,
                settings.GetActiveAdsSensitivity());
        }

        settings.Normalize();
        ApplyMagnificationPolicy(settings);
        SaveAndSynchronize();
    }

    private void ResetCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.LoadSettings();
        var selectedWeapon =
            (WeaponSelector.SelectedItem as WeaponProfileViewModel)?.Name;
        _calibrationUndo = CalibrationSnapshot.Capture(settings, selectedWeapon);
        UndoCalibrationButton.IsEnabled = true;

        settings.HorizontalSensitivity = 55.0f;
        settings.VerticalSensitivity = 55.0f;
        settings.MouseDpi = 1600;
        settings.MouseSensitivityMultiplierUnit = 0.001f;
        settings.AdsSensitivity = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.0x"] = 38.0f,
            ["2.5x"] = 67.0f,
            ["3.5x"] = 72.0f,
            ["8.0x"] = 74.0f
        };
        settings.AutomaticMagnificationEnabled = true;
        if (!string.IsNullOrWhiteSpace(selectedWeapon))
        {
            settings.SetWeaponOutputStrength(selectedWeapon, RecoilStrengthModel.Default);
        }
        settings.Normalize();
        RefreshCalibrationControls(settings);
        CalibrationActionStatusText.Text =
            "Reference sensitivity and this weapon's output strength were reset. Undo is available.";
        DiagnosticLog.Record("calibration", $"Reset calibration for {selectedWeapon ?? "no weapon"}.");
        SaveAndSynchronize();
    }

    private void UndoCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationUndo is not { } snapshot)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        snapshot.Restore(settings);
        settings.Normalize();
        _calibrationUndo = null;
        UndoCalibrationButton.IsEnabled = false;
        RefreshCalibrationControls(settings);
        CalibrationActionStatusText.Text = "The previous calibration was restored.";
        DiagnosticLog.Record("calibration", "Undid the most recent calibration reset.");
        SaveAndSynchronize();
    }

    private void RefreshCalibrationControls(Settings settings)
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            MouseDpiBox.Value = settings.MouseDpi ?? 1600;
            HorizontalSensitivityBox.Value = settings.HorizontalSensitivity ?? 55.0f;
            HorizontalSensitivitySlider.Value = HorizontalSensitivityBox.Value;
            VerticalSensitivityBox.Value = settings.VerticalSensitivity ?? 55.0f;
            VerticalSensitivitySlider.Value = VerticalSensitivityBox.Value;
            SensitivityMultiplierBox.Value = settings.MouseSensitivityMultiplierUnit;
            AutomaticMagnificationToggle.IsOn = settings.AutomaticMagnificationEnabled;
            MagnificationSelector.SelectedItem = settings.ActiveMagnification;
            AdsSensitivityBox.Value = settings.GetActiveAdsSensitivity();
            ApplyMagnificationPolicy(settings);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
        UpdateWeaponStrengthUi();
        UpdateOverlayContent();
    }

    private void RapidFireToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SettingsManager.LoadSettings().RapidFireEnabled = RapidFireToggle.IsOn;
        UpdateProfileDescription();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void ArmToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            ApplyRequestedArmState(ArmToggle.IsOn);
        }
    }

    private void DevicePageArmToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            ApplyRequestedArmState(DevicePageArmToggle.IsOn);
        }
    }

    private void OverlayArmToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            ApplyRequestedArmState(OverlayArmToggle.IsOn);
        }
    }

    private void ApplyRequestedArmState(bool requested)
    {
        if (requested && _connection?.IsConnected != true)
        {
            SetArmControls(false, false);
            DeviceMessageText.Text = "Connect a supported board before arming output.";
            DevicePageConnectionDetail.Text = DeviceMessageText.Text;
            return;
        }
        if (requested && !CanArmConnectedHardware())
        {
            SetArmControls(false, false);
            DeviceMessageText.Text = BuildConnectedHardwareDetail();
            DevicePageConnectionDetail.Text = DeviceMessageText.Text;
            return;
        }

        SetArmControls(requested, _connection?.IsConnected == true);
        DiagnosticLog.Record("safety", _isArmed ? "Output armed by user." : "Output disarmed by user.");
        SaveSettingsWithFeedback();
        if (!_isArmed)
        {
            if (_rp2350ArmLeaseSent)
            {
                TrySendArmLease(false);
                _rp2350ArmLeaseSent = false;
            }
            TrySendCommand("STOP");
            _outputActive = false;
        }
        else
        {
            RefreshRp2350ArmLease();
        }
    }

    private void SetArmControls(bool isArmed, bool isEnabled)
    {
        _isArmed = isArmed && isEnabled;
        if (!_isArmed)
        {
            _outputActive = false;
        }
        _isInitializing = true;
        ArmToggle.IsEnabled = isEnabled;
        DevicePageArmToggle.IsEnabled = isEnabled;
        OverlayArmToggle.IsEnabled = isEnabled;
        ArmToggle.IsOn = _isArmed;
        DevicePageArmToggle.IsOn = _isArmed;
        OverlayArmToggle.IsOn = _isArmed;
        _isInitializing = false;

        TopArmStatusText.Text = _isArmed ? "Output armed" : "Output safe";
        TopArmStatusText.Foreground = _isArmed ? ConnectedBrush : new SolidColorBrush(Colors.Gray);
        UpdateOverlayContent();
    }

    private void MouseButtonTrigger_AimAndFireChanged(object? sender, bool isPressed)
    {
        var connection = _connection;
        if (_isSynchronizingConfiguration || connection?.IsConnected != true)
        {
            return;
        }

        // RP2350 activation is derived from the raw downstream mouse state on
        // the device. The upstream state includes synthesized rapid-fire pulses
        // and must never be fed back into its own activation decision.
        if (_firmwareDeviceKind == FirmwareStatusKind.Rp2350MouseProxy)
        {
            return;
        }

        var safeForeground = connection.IsSimulator || ScreenCaptureService.IsRainbowSixForeground();
        if (isPressed && _isArmed && safeForeground)
        {
            TrySendCommand("START");
            _outputActive = true;
            return;
        }

        if (_outputActive || !isPressed)
        {
            TrySendCommand("STOP");
        }
        _outputActive = false;
    }

    private void MouseButtonTrigger_AimAndFireHeartbeat(object? sender, EventArgs e)
    {
        var connection = _connection;
        if (_isSynchronizingConfiguration || !_isArmed ||
            connection?.IsConnected != true || !_mouseButtonTrigger.IsPressed)
        {
            return;
        }

        if (_firmwareDeviceKind == FirmwareStatusKind.Rp2350MouseProxy)
        {
            return;
        }

        if (connection.IsSimulator || ScreenCaptureService.IsRainbowSixForeground())
        {
            TrySendCommand("KEEPALIVE");
            _outputActive = true;
        }
        else if (_outputActive)
        {
            TrySendCommand("STOP");
            _outputActive = false;
            DiagnosticLog.Record("safety", "Output stopped because Rainbow Six lost focus.");
        }
    }

    private void Rp2350ArmLeaseTimer_Tick(object? sender, object e) =>
        RefreshRp2350ArmLease();

    private void RefreshRp2350ArmLease()
    {
        var connection = _connection;
        var shouldLease = !_isDisposed && !_isSynchronizingConfiguration &&
            _isArmed && connection?.IsConnected == true && !connection.IsSimulator &&
            _firmwareDeviceKind == FirmwareStatusKind.Rp2350MouseProxy &&
            _physicalMouseStatus == FirmwareStatusKind.MouseConnected &&
            ScreenCaptureService.IsRainbowSixForeground();

        if (shouldLease)
        {
            _rp2350ArmLeaseSent = TrySendArmLease(true);
        }
        else if (_rp2350ArmLeaseSent)
        {
            TrySendArmLease(false);
            _rp2350ArmLeaseSent = false;
        }
    }

    private bool TrySendArmLease(bool enabled)
    {
        try
        {
            _connection?.SendArmLease(enabled);
            return _connection?.IsConnected == true;
        }
        catch (Exception ex)
        {
            HandleConnectionLost(ex.Message);
            return false;
        }
    }

    private void DetectionModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || DetectionModeSelector.SelectedItem is not DetectionModeOption selected)
        {
            return;
        }

        SettingsManager.LoadSettings().OperatorDetectionMode = selected.Value;
        SaveSettingsWithFeedback();
        UpdateDetectionUi();
    }

    private void DetectionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SettingsManager.LoadSettings().AutoApplyDetectedOperator = AutoApplyDetectionToggle.IsOn;
        SaveSettingsWithFeedback();
    }

    private void ConfidenceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SettingsManager.LoadSettings().OperatorDetectionConfidence = e.NewValue / 100.0;
        ConfidenceValueText.Text = $"{e.NewValue:0}% minimum";
        SaveSettingsWithFeedback();
    }

    private void DetectionRegion_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.DetectionRegionX = ValidDouble(DetectionRegionXBox.Value, settings.DetectionRegionX);
        settings.DetectionRegionY = ValidDouble(DetectionRegionYBox.Value, settings.DetectionRegionY);
        settings.DetectionRegionWidth = ValidDouble(DetectionRegionWidthBox.Value, settings.DetectionRegionWidth);
        settings.DetectionRegionHeight = ValidDouble(DetectionRegionHeightBox.Value, settings.DetectionRegionHeight);
        settings.Normalize();
        _isInitializing = true;
        DetectionRegionXBox.Value = settings.DetectionRegionX;
        DetectionRegionYBox.Value = settings.DetectionRegionY;
        DetectionRegionWidthBox.Value = settings.DetectionRegionWidth;
        DetectionRegionHeightBox.Value = settings.DetectionRegionHeight;
        _isInitializing = false;
        SaveSettingsWithFeedback();
    }

    private void WeaponDetectionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.WeaponDetectionEnabled = WeaponDetectionToggle.IsOn;
        settings.AutoApplyDetectedWeapon = AutoApplyWeaponDetectionToggle.IsOn;
        settings.WeaponSlotHotkeysEnabled = WeaponSlotHotkeysToggle.IsOn;
        SaveSettingsWithFeedback();
        UpdateDetectionUi();
    }

    private void WeaponConfidenceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SettingsManager.LoadSettings().WeaponDetectionConfidence = e.NewValue / 100.0;
        WeaponConfidenceValueText.Text = $"{e.NewValue:0}% minimum";
        SaveSettingsWithFeedback();
    }

    private void WeaponDetectionRegion_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.WeaponDetectionRegionX = ValidDouble(
            WeaponDetectionRegionXBox.Value,
            settings.WeaponDetectionRegionX);
        settings.WeaponDetectionRegionY = ValidDouble(
            WeaponDetectionRegionYBox.Value,
            settings.WeaponDetectionRegionY);
        settings.WeaponDetectionRegionWidth = ValidDouble(
            WeaponDetectionRegionWidthBox.Value,
            settings.WeaponDetectionRegionWidth);
        settings.WeaponDetectionRegionHeight = ValidDouble(
            WeaponDetectionRegionHeightBox.Value,
            settings.WeaponDetectionRegionHeight);
        settings.Normalize();
        _isInitializing = true;
        WeaponDetectionRegionXBox.Value = settings.WeaponDetectionRegionX;
        WeaponDetectionRegionYBox.Value = settings.WeaponDetectionRegionY;
        WeaponDetectionRegionWidthBox.Value = settings.WeaponDetectionRegionWidth;
        WeaponDetectionRegionHeightBox.Value = settings.WeaponDetectionRegionHeight;
        _isInitializing = false;
        SaveSettingsWithFeedback();
    }

    private void DetectionHotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.DetectionHotkeyModifier = DetectionModifierSelector.SelectedItem as string ?? "Shift";
        settings.DetectionHotkeyKey = DetectionKeySelector.SelectedItem as string ?? "M1";
        SaveSettingsWithFeedback();
        UpdateHotkeyPreviews();
    }

    private void OverlaySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SettingsManager.LoadSettings().OverlayEnabled = OverlayEnabledToggle.IsOn;
        SaveSettingsWithFeedback();
        UpdateHotkeyPreviews();
    }

    private void OverlayHotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.OverlayHotkeyModifier = OverlayModifierSelector.SelectedItem as string ?? "None";
        settings.OverlayHotkeyKey = OverlayKeySelector.SelectedItem as string ?? "F8";
        SaveSettingsWithFeedback();
        UpdateHotkeyPreviews();
    }

    private async void CaptureNowButton_Click(object sender, RoutedEventArgs e) =>
        await DetectOperatorAsync(_lifetimeCancellation.Token, false);

    private async void CaptureWeaponNowButton_Click(object sender, RoutedEventArgs e) =>
        await DetectWeaponAsync(_lifetimeCancellation.Token, false);

    private async void ContinuousDetectionTimer_Tick(object? sender, object e)
    {
        var settings = SettingsManager.LoadSettings();
        if (_isDetecting || _isWeaponDetecting || _isDisposed ||
            (settings.OperatorDetectionMode != OperatorDetectionMode.Continuous &&
             !settings.WeaponDetectionEnabled) ||
            ScreenCaptureService.IsCurrentProcessForeground())
        {
            return;
        }

        if (settings.OperatorDetectionMode == OperatorDetectionMode.Continuous)
        {
            await DetectOperatorAsync(_lifetimeCancellation.Token, true);
        }
        else
        {
            await DetectWeaponAsync(_lifetimeCancellation.Token, true);
        }
    }

    private void HotkeyMonitor_OverlayPressed(object? sender, EventArgs e) =>
        (Application.Current as App)?.ToggleOverlay();

    private async void HotkeyMonitor_DetectionPressed(object? sender, EventArgs e) =>
        await DetectOperatorAsync(_lifetimeCancellation.Token, false);

    private void HotkeyMonitor_PrimaryWeaponPressed(object? sender, EventArgs e) =>
        SelectWeaponSlot(WeaponSlot.Primary);

    private void HotkeyMonitor_SecondaryWeaponPressed(object? sender, EventArgs e) =>
        SelectWeaponSlot(WeaponSlot.Secondary);

    private async Task DetectOperatorAsync(
        CancellationToken cancellationToken,
        bool requireConfirmation)
    {
        if (_isDetecting || _isDisposed)
        {
            return;
        }

        _isDetecting = true;
        CaptureNowButton.IsEnabled = false;
        SetDetectionStatus("Reading screen…", "Capturing the configured region in memory.", WarningBrush);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var settings = SettingsManager.LoadSettings();
            var result = await _operatorDetectionService.DetectAsync(
                settings,
                _viewModel.GetOperatorNames(),
                cancellationToken);
            RecognizedTextPreview.Text = PreviewText(result.RecognizedText);
            DiagnosticLog.Record(
                "operator-ocr",
                $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; " +
                $"recognized {result.RecognizedText.Length} characters; decision='{result.Status}'");
            if (result.Match is null)
            {
                _operatorDetectionDebouncer.Reset();
                SetDetectionStatus("No match", result.Status, WarningBrush);
            }
            else
            {
                SetDetectionStatus("Match found", result.Status, ConnectedBrush);
                if (settings.AutoApplyDetectedOperator)
                {
                    var currentOperator =
                        (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
                    if (!requireConfirmation ||
                        result.Match.Name.Equals(currentOperator, StringComparison.OrdinalIgnoreCase) ||
                        _operatorDetectionDebouncer.Observe(result.Match.Name))
                    {
                        SelectOperator(result.Match.Name);
                        _operatorDetectionDebouncer.Reset();
                    }
                    else
                    {
                        SetDetectionStatus(
                            "Confirming match",
                            $"{result.Match.Name} at {result.Match.Confidence:P0}; waiting for a second consecutive scan.",
                            WarningBrush);
                        return;
                    }
                }
                else
                {
                    _operatorDetectionDebouncer.Reset();
                }
            }

            if (settings.WeaponDetectionEnabled)
            {
                await DetectWeaponCoreAsync(settings, cancellationToken, requireConfirmation);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetDetectionStatus("Detection error", ex.Message, ErrorBrush);
        }
        finally
        {
            _isDetecting = false;
            if (!_isDisposed)
            {
                CaptureNowButton.IsEnabled = true;
            }
        }
    }

    private async Task DetectWeaponAsync(
        CancellationToken cancellationToken,
        bool requireConfirmation)
    {
        if (_isWeaponDetecting || _isDetecting || _isDisposed)
        {
            return;
        }

        _isWeaponDetecting = true;
        CaptureWeaponNowButton.IsEnabled = false;
        try
        {
            await DetectWeaponCoreAsync(
                SettingsManager.LoadSettings(),
                cancellationToken,
                requireConfirmation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetWeaponDetectionStatus("Detection error", ex.Message, ErrorBrush);
        }
        finally
        {
            _isWeaponDetecting = false;
            if (!_isDisposed)
            {
                CaptureWeaponNowButton.IsEnabled = true;
            }
        }
    }

    private async Task DetectWeaponCoreAsync(
        Settings settings,
        CancellationToken cancellationToken,
        bool requireConfirmation)
    {
        SetWeaponDetectionStatus(
            "Reading loadout…",
            "Capturing the configured primary and secondary card regions in memory.",
            WarningBrush);
        var candidates = WeaponSelector.ItemsSource is IEnumerable<WeaponProfileViewModel> profiles
            ? profiles.ToArray()
            : Array.Empty<WeaponProfileViewModel>();
        var primaryCandidates = candidates
            .Where(profile => WeaponSlotCatalog.GetSlot(profile.Profile) == WeaponSlot.Primary)
            .Select(profile => profile.Name)
            .ToArray();
        var secondaryCandidates = candidates
            .Where(profile => WeaponSlotCatalog.GetSlot(profile.Profile) == WeaponSlot.Secondary)
            .Select(profile => profile.Name)
            .ToArray();
        var started = Stopwatch.GetTimestamp();
        var result = await _weaponDetectionService.DetectAsync(
            settings,
            primaryCandidates,
            secondaryCandidates,
            cancellationToken);
        WeaponRecognizedTextPreview.Text = PreviewText(
            $"Primary: {result.PrimaryRecognizedText} | Secondary: {result.SecondaryRecognizedText}");
        DiagnosticLog.Record(
            "weapon-ocr",
            $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; " +
            $"recognized {result.PrimaryRecognizedText.Length}/{result.SecondaryRecognizedText.Length} " +
            $"characters; decision='{result.Status}'");
        if (result.PrimaryMatch is null && result.SecondaryMatch is null)
        {
            _weaponDetectionDebouncer.Reset();
            SetWeaponDetectionStatus("No loadout match", result.Status, WarningBrush);
            return;
        }

        var candidateKey = $"{result.PrimaryMatch?.Name ?? "-"}|{result.SecondaryMatch?.Name ?? "-"}";
        if (settings.AutoApplyDetectedWeapon && requireConfirmation &&
            !_weaponDetectionDebouncer.Observe(candidateKey))
        {
            SetWeaponDetectionStatus(
                "Confirming loadout",
                $"{result.Status} Waiting for a second consecutive scan before changing profiles.",
                WarningBrush);
            return;
        }
        _weaponDetectionDebouncer.Reset();

        var operatorName = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
        if (!string.IsNullOrWhiteSpace(operatorName))
        {
            if (result.PrimaryMatch is not null)
            {
                settings.PrimaryWeaponsByOperator[operatorName] = result.PrimaryMatch.Name;
            }
            if (result.SecondaryMatch is not null)
            {
                settings.SecondaryWeaponsByOperator[operatorName] = result.SecondaryMatch.Name;
            }
            SaveSettingsWithFeedback();
        }

        SetWeaponDetectionStatus("Loadout found", result.Status, ConnectedBrush);
        if (settings.AutoApplyDetectedWeapon && result.PrimaryMatch is not null &&
            !SelectWeapon(result.PrimaryMatch.Name))
        {
            SetWeaponDetectionStatus("Weapon unavailable", result.Status, WarningBrush);
        }
    }

    private void SelectOperator(string operatorName)
    {
        var match = _viewModel.AllOperators.FirstOrDefault(item =>
            item.OperatorName.Equals(operatorName, StringComparison.OrdinalIgnoreCase));
        if (match is not null && OperatorSelector.SelectedItem != match)
        {
            OperatorSelector.SelectedItem = match;
        }
    }

    private void SetDetectionStatus(string title, string detail, Brush brush)
    {
        DetectionStatusText.Text = title;
        DetectionDetailText.Text = detail;
        DetectionStatusDot.Fill = brush;
        OverviewDetectionStatusText.Text = detail;
        OverlayDetectionText.Text = detail;
    }

    private void SetWeaponDetectionStatus(string title, string detail, Brush brush)
    {
        WeaponDetectionStatusText.Text = title;
        WeaponDetectionDetailText.Text = detail;
        WeaponDetectionStatusDot.Fill = brush;
    }

    private void UpdateDetectionUi()
    {
        var settings = SettingsManager.LoadSettings();
        var mode = _detectionModeOptions.First(option => option.Value == settings.OperatorDetectionMode);
        DetectionStatusText.Text = mode.Value == OperatorDetectionMode.Disabled ? "Disabled" : "Ready";
        DetectionDetailText.Text = mode.Description;
        DetectionStatusDot.Fill = mode.Value == OperatorDetectionMode.Disabled
            ? DisconnectedBrush
            : ConnectedBrush;
        DetectionModifierSelector.IsEnabled = mode.Value == OperatorDetectionMode.Keybind;
        DetectionKeySelector.IsEnabled = mode.Value == OperatorDetectionMode.Keybind;
        OverviewDetectionStatusText.Text = mode.DisplayName;
        OverlayDetectionText.Text = mode.DisplayName;
        ConfidenceValueText.Text = $"{settings.OperatorDetectionConfidence:P0} minimum";
        WeaponDetectionStatusText.Text = settings.WeaponDetectionEnabled ? "Ready" : "Disabled";
        WeaponDetectionDetailText.Text = settings.WeaponDetectionEnabled
            ? "Scans the operator Loadout screen for named primary and secondary cards."
            : "Loadout OCR is off; the 1/2 slot shortcuts can still be used.";
        WeaponDetectionStatusDot.Fill = settings.WeaponDetectionEnabled
            ? ConnectedBrush
            : DisconnectedBrush;
        AutoApplyWeaponDetectionToggle.IsEnabled = settings.WeaponDetectionEnabled;
        WeaponConfidenceSlider.IsEnabled = settings.WeaponDetectionEnabled;
        WeaponDetectionRegionXBox.IsEnabled = settings.WeaponDetectionEnabled;
        WeaponDetectionRegionYBox.IsEnabled = settings.WeaponDetectionEnabled;
        WeaponDetectionRegionWidthBox.IsEnabled = settings.WeaponDetectionEnabled;
        WeaponDetectionRegionHeightBox.IsEnabled = settings.WeaponDetectionEnabled;
        CaptureWeaponNowButton.IsEnabled = !_isWeaponDetecting;
        WeaponConfidenceValueText.Text = $"{settings.WeaponDetectionConfidence:P0} minimum";
    }

    private void UpdateHotkeyPreviews()
    {
        var detection = GetDetectionChord();
        var overlay = GetOverlayChord();
        DetectionHotkeyPreview.Text = detection.DisplayName;
        TopOverlayShortcutText.Text = $"{overlay.DisplayName}  Overlay";
        SidebarOverlayHint.Text = SettingsManager.LoadSettings().OverlayEnabled
            ? $"Press {overlay.DisplayName} for overlay"
            : "Overlay disabled";
        OverlayShortcutText.Text = $"{overlay.DisplayName} to return";
        ShowOverlayButton.IsEnabled = SettingsManager.LoadSettings().OverlayEnabled;
    }

    private HotkeyChord GetDetectionChord()
    {
        var settings = SettingsManager.LoadSettings();
        return new HotkeyChord(settings.DetectionHotkeyModifier, settings.DetectionHotkeyKey);
    }

    private HotkeyChord GetOverlayChord()
    {
        var settings = SettingsManager.LoadSettings();
        return new HotkeyChord(settings.OverlayHotkeyModifier, settings.OverlayHotkeyKey);
    }

    private void ShowOverlayButton_Click(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.SetOverlayMode(true);

    private void ExitOverlayButton_Click(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.SetOverlayMode(false);

    public void SetOverlayVisualMode(bool enabled)
    {
        FullShell.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        CompactOverlayShell.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateOverlayContent();
    }

    private void UpdateOverlayContent()
    {
        if (OverlayOperatorText is null)
        {
            return;
        }

        OverlayOperatorText.Text = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName ?? "—";
        var settings = SettingsManager.LoadSettings();
        OverlayWeaponText.Text = WeaponSelector.SelectedItem is WeaponProfileViewModel selected
            ? $"{WeaponSlotCatalog.GetSlot(selected.Profile)} · {selected.Name} · {settings.ActiveMagnification}" +
              (Math.Abs(settings.GetWeaponOutputStrength(selected.Name) -
                        RecoilStrengthModel.Default) > 0.0001
                  ? $" · {settings.GetWeaponOutputStrength(selected.Name):0.00}×"
                  : string.Empty) +
              (settings.CompensationMode == CompensationMode.Experimental
                  ? " · Experimental"
                  : string.Empty) +
              (selected.Profile.SupportsRapidFire && settings.RapidFireEnabled
                  ? " · Rapid"
                  : string.Empty)
            : "—";
        OverlayStatusText.Text = _connection?.IsConnected == true
            ? _connection.IsSimulator
                ? (_isArmed ? "Simulator · armed test state" : "Simulator · safe")
                : (_isArmed ? "Device online · armed" : "Device online · safe")
            : "Device offline";
        OverlayStatusDot.Fill = _connection?.IsConnected == true ? ConnectedBrush : DisconnectedBrush;
    }

    private void SaveAndSynchronize()
    {
        SaveSettingsWithFeedback();
        if (_connection?.IsConnected == true)
        {
            var revision = Interlocked.Increment(ref _configurationRevision);
            _ = SynchronizeLatestConfigurationAsync(revision);
        }
    }

    private async Task SynchronizeLatestConfigurationAsync(int revision)
    {
        try
        {
            await _configurationSyncLock.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (revision != Volatile.Read(ref _configurationRevision) ||
                    _connection?.IsConnected != true)
                {
                    return;
                }

                await SendCurrentConfigurationAsync(_lifetimeCancellation.Token, true);
            }
            finally
            {
                _configurationSyncLock.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void SaveSettingsWithFeedback()
    {
        if (!SettingsManager.TrySave(out var error) && !string.IsNullOrWhiteSpace(error))
        {
            DeviceMessageText.Text = $"Settings were not saved: {error}";
            DevicePageConnectionDetail.Text = DeviceMessageText.Text;
            DiagnosticLog.Record("settings-error", error);
        }
    }

    private WeaponProfile? BuildEffectiveSelectedProfile(Settings settings)
    {
        if (WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            return null;
        }

        var operatorName = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
        var setup = RecoilAttachmentModel.Resolve(selected.Profile, operatorName);
        var effectiveProfile = selected.Profile
            .WithAttachmentSetup(setup)
            .WithOpticSetup(settings.ActiveMagnification, operatorName)
            .WithOutputStrength(settings.GetWeaponOutputStrength(selected.Name));
        if (settings.CompensationMode == CompensationMode.Experimental &&
            effectiveProfile.HasWeaponPattern)
        {
            effectiveProfile.Pattern = ExperimentalRecoilModel.Apply(
                effectiveProfile.Pattern,
                settings.GetExperimentalRecoilTuning(effectiveProfile.Name));
            effectiveProfile.PatternDataQuality = PatternDataQuality.VideoDerivedEstimate;
            effectiveProfile.PatternSource =
                "Standard estimated pattern with local per-weapon experimental calibration";
        }
        return effectiveProfile;
    }

    private ConfigurationValidationResult ValidateCurrentConfiguration(
        Settings settings,
        WeaponProfile? effectiveProfile) =>
        ConfigurationValidator.Validate(
            settings,
            effectiveProfile,
            settings.CompensationMode,
            settings.RapidFireEnabled && effectiveProfile?.SupportsRapidFire == true);

    private void ValidateConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.LoadSettings();
        var result = ValidateCurrentConfiguration(settings, BuildEffectiveSelectedProfile(settings));
        ConfigurationStatusText.Text = result.Summary;
        ConfigurationStatusText.Foreground = result.IsValid ? ConnectedBrush : ErrorBrush;
        DiagnosticLog.Record(
            result.IsValid ? "validation" : "validation-error",
            result.Summary);
        if (!result.IsValid)
        {
            TrySendCommand("STOP");
            SetArmControls(false, _connection?.IsConnected == true);
        }
    }

    private void CopyDiagnosticReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedOperator =
                (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
            var selectedProfile =
                (WeaponSelector.SelectedItem as WeaponProfileViewModel)?.Profile;
            var report = DiagnosticLog.BuildReport(
                SettingsManager.LoadSettings(),
                _connection,
                selectedOperator,
                selectedProfile,
                _isArmed);
            var package = new DataPackage();
            package.SetText(report);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            DiagnosticCopyStatusText.Text = "Diagnostic report copied to the clipboard.";
            DiagnosticCopyStatusText.Foreground = ConnectedBrush;
        }
        catch (Exception exception)
        {
            DiagnosticCopyStatusText.Text = $"Could not copy diagnostics: {exception.Message}";
            DiagnosticCopyStatusText.Foreground = ErrorBrush;
        }
    }

    private async Task<bool> SendCurrentConfigurationAsync(
        CancellationToken cancellationToken,
        bool preserveFireState)
    {
        var connection = _connection;
        if (connection?.IsConnected != true)
        {
            return false;
        }

        var synchronized = false;
        try
        {
            if (preserveFireState)
            {
                _isSynchronizingConfiguration = true;
                connection.SendCommand("STOP");
                _outputActive = false;
            }
            var settings = SettingsManager.LoadSettings();
            var effectiveProfile = BuildEffectiveSelectedProfile(settings);
            var validation = ValidateCurrentConfiguration(settings, effectiveProfile);
            ConfigurationStatusText.Text = validation.Summary;
            ConfigurationStatusText.Foreground = validation.IsValid
                ? ConnectedBrush
                : ErrorBrush;
            if (!validation.IsValid || effectiveProfile is null)
            {
                connection.SendCommand("STOP");
                SetArmControls(false, true);
                DiagnosticLog.Record("validation-error", validation.Summary);
                return false;
            }
            var syncResult = await _configurationSynchronizer.SynchronizeAsync(
                connection,
                new DeviceConfigurationRequest(
                    effectiveProfile,
                    settings.CompensationMode,
                    settings.CalculateSensitivityScale(),
                    settings.RapidFireEnabled && effectiveProfile.SupportsRapidFire,
                    effectiveProfile.RapidFireRoundsPerMinute),
                cancellationToken);
            synchronized = true;
            DiagnosticLog.Record(
                "configuration",
                $"Synchronized {effectiveProfile.Name} in " +
                $"{syncResult.Elapsed.TotalMilliseconds:F1} ms with hash " +
                $"{syncResult.Hash:X8} using " +
                $"{(connection.IsSimulator ? "simulator" : connection.PortName)}.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Record("configuration-error", ex.Message);
            if (ReferenceEquals(_connection, connection))
            {
                HandleConnectionLost(ex.Message);
            }
            return false;
        }
        finally
        {
            if (preserveFireState)
            {
                _isSynchronizingConfiguration = false;
                if (synchronized && ReferenceEquals(_connection, connection) &&
                    _firmwareDeviceKind != FirmwareStatusKind.Rp2350MouseProxy &&
                    _isArmed && _mouseButtonTrigger.IsPressed)
                {
                    var safeForeground = connection.IsSimulator ||
                        ScreenCaptureService.IsRainbowSixForeground();
                    if (safeForeground)
                    {
                        TrySendCommand("START");
                        _outputActive = true;
                    }
                }
            }
        }
    }

    private void TrySendCommand(string command)
    {
        try
        {
            _connection?.SendCommand(command);
        }
        catch (Exception ex)
        {
            HandleConnectionLost(ex.Message);
        }
    }

    private void UpdateProfileDescription()
    {
        if (WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            ProfileDescriptionText.Text = "No weapon is available for this operator.";
            WeaponSlotText.Text = "NO ACTIVE SLOT";
            RapidFireStatusText.Text = "Rapid fire unavailable";
            RapidFireToggle.IsEnabled = false;
            AttachmentNoteText.Visibility = Visibility.Collapsed;
            UpdateWeaponStrengthUi();
            return;
        }

        var operatorName = (OperatorSelector.SelectedItem as OperatorViewModel)?.OperatorName;
        var setup = RecoilAttachmentModel.Resolve(selected.Profile, operatorName);
        WeaponSlotText.Text = $"{WeaponSlotCatalog.GetSlot(selected.Profile).ToString().ToUpperInvariant()} SLOT";
        ProfileDescriptionText.Text = selected.Description;
        var rapidAvailable = selected.Profile.SupportsRapidFire;
        RapidFireToggle.IsEnabled = rapidAvailable;
        RapidFireStatusText.Text = rapidAvailable
            ? SettingsManager.LoadSettings().RapidFireEnabled
                ? $"ON · {selected.Profile.RapidFireRoundsPerMinute} RPM · recoil applied per shot"
                : "OFF · manual clicks still receive one recoil correction per shot"
            : "Automatic weapon · rapid fire not applicable";
        AttachmentNoteText.Text = setup.Note ?? string.Empty;
        AttachmentNoteText.Visibility = string.IsNullOrWhiteSpace(setup.Note)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateWeaponStrengthUi();
    }

    private void UpdateModeDescription()
    {
        ModeDescriptionText.Text = SettingsManager.CompensationMode switch
        {
            CompensationMode.WeaponPattern =>
                "Uses an RPM-timed estimate when the weapon supports it; otherwise general mode is sent.",
            CompensationMode.Experimental =>
                "Uses a separate, per-weapon calibrated copy of the estimated pattern. General and standard Pattern remain unchanged.",
            _ => "Applies the profile's steady horizontal and vertical correction every 8 ms."
        };
    }

    private void UpdateExperimentalTuningUi()
    {
        var settings = SettingsManager.LoadSettings();
        var visible = settings.CompensationMode == CompensationMode.Experimental;
        ExperimentalTuningPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        var selected = WeaponSelector.SelectedItem as WeaponProfileViewModel;
        var available = selected?.Profile.HasWeaponPattern == true;
        var tuning = available
            ? settings.GetExperimentalRecoilTuning(selected!.Name)
            : new ExperimentalRecoilTuning();

        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            ExperimentalFirstShotBox.Value = tuning.FirstShotGain;
            ExperimentalEarlyBox.Value = tuning.EarlyGain;
            ExperimentalMidBox.Value = tuning.MidGain;
            ExperimentalLateBox.Value = tuning.LateGain;
            ExperimentalHorizontalBox.Value = tuning.HorizontalGain;
            ExperimentalFirstShotBox.IsEnabled = available;
            ExperimentalEarlyBox.IsEnabled = available;
            ExperimentalMidBox.IsEnabled = available;
            ExperimentalLateBox.IsEnabled = available;
            ExperimentalHorizontalBox.IsEnabled = available;
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        ExperimentalTuningStatusText.Text = available
            ? $"{selected!.Name} · first {tuning.FirstShotGain:0.00}, early {tuning.EarlyGain:0.00}, " +
              $"mid {tuning.MidGain:0.00}, late {tuning.LateGain:0.00}, horizontal {tuning.HorizontalGain:0.00}"
            : "No automatic pattern is available for this weapon; the device safely falls back to General mode.";
    }

    private void UpdateWeaponStrengthUi()
    {
        var selected = WeaponSelector.SelectedItem as WeaponProfileViewModel;
        var available = selected is not null &&
                        (selected.Profile.VerticalCompensation > 0 ||
                         selected.Profile.HorizontalCompensation != 0 ||
                         selected.Profile.HasWeaponPattern);
        var strength = selected is null
            ? RecoilStrengthModel.Default
            : SettingsManager.LoadSettings().GetWeaponOutputStrength(selected.Name);

        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            WeaponStrengthBox.Value = strength;
            WeaponStrengthBox.IsEnabled = available;
            ResetWeaponStrengthButton.IsEnabled = available &&
                Math.Abs(strength - RecoilStrengthModel.Default) > 0.0001;
        }
        finally
        {
            _isInitializing = wasInitializing;
        }

        WeaponStrengthStatusText.Text = selected is null
            ? "Select a weapon to tune its output."
            : available
                ? $"{selected.Name} · {strength:0.00}× · 1.00× preserves the profile"
                : $"{selected.Name} has no automatic recoil output to scale.";
    }

    private void WeaponStrength_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing ||
            WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        var current = settings.GetWeaponOutputStrength(selected.Name);
        settings.SetWeaponOutputStrength(
            selected.Name,
            ValidDouble(WeaponStrengthBox.Value, current));
        UpdateWeaponStrengthUi();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void ResetWeaponStrength_Click(object sender, RoutedEventArgs e)
    {
        if (WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            return;
        }

        SettingsManager.LoadSettings().SetWeaponOutputStrength(
            selected.Name,
            RecoilStrengthModel.Default);
        UpdateWeaponStrengthUi();
        UpdateOverlayContent();
        SaveAndSynchronize();
    }

    private void ExperimentalTuning_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitializing || SettingsManager.CompensationMode != CompensationMode.Experimental ||
            WeaponSelector.SelectedItem is not WeaponProfileViewModel selected ||
            !selected.Profile.HasWeaponPattern)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        var tuning = settings.GetExperimentalRecoilTuning(selected.Name);
        tuning.FirstShotGain = ValidDouble(ExperimentalFirstShotBox.Value, tuning.FirstShotGain);
        tuning.EarlyGain = ValidDouble(ExperimentalEarlyBox.Value, tuning.EarlyGain);
        tuning.MidGain = ValidDouble(ExperimentalMidBox.Value, tuning.MidGain);
        tuning.LateGain = ValidDouble(ExperimentalLateBox.Value, tuning.LateGain);
        tuning.HorizontalGain = ValidDouble(ExperimentalHorizontalBox.Value, tuning.HorizontalGain);
        ExperimentalRecoilModel.Normalize(tuning);
        UpdateExperimentalTuningUi();
        SaveAndSynchronize();
    }

    private void ResetExperimentalTuning_Click(object sender, RoutedEventArgs e)
    {
        if (WeaponSelector.SelectedItem is not WeaponProfileViewModel selected)
        {
            return;
        }

        var settings = SettingsManager.LoadSettings();
        settings.ExperimentalRecoilTunings.Remove(selected.Name);
        UpdateExperimentalTuningUi();
        SaveAndSynchronize();
    }

    private void UpdateCalibrationSummary(Settings settings)
    {
        var scale = settings.CalculateSensitivityScale();
        CalibrationSummaryText.Text =
            $"Scale H {scale.Horizontal:0.000} / V {scale.Vertical:0.000}  ·  " +
            $"equivalent {settings.DefaultMultiplierEquivalent:0.###}  ·  " +
            $"{settings.ActiveMagnification} ADS {settings.GetActiveAdsSensitivity():0.#}";
    }

    private void SetConnectionStatus(string title, string detail, Brush brush)
    {
        ConnectionStatusText.Text = title;
        DeviceMessageText.Text = detail;
        ConnectionStatusDot.Fill = brush;
        DevicePageConnectionTitle.Text = title;
        DevicePageConnectionDetail.Text = detail;
        DevicePageConnectionDot.Fill = brush;
        TopConnectionText.Text = _connection?.IsConnected == true
            ? _connection.IsSimulator ? "Simulator" : "Online"
            : "Offline";
        TopConnectionDot.Fill = brush;
        UpdateOverlayContent();
    }

    private void SetConnectButtonsEnabled(bool enabled)
    {
        ConnectButton.IsEnabled = enabled;
        DeviceConnectButton.IsEnabled = enabled;
        ConnectSimulatorButton.IsEnabled = enabled;
    }

    private void SetConnectButtonContent(string content)
    {
        ConnectButton.Content = content;
        DeviceConnectButton.Content = content;
    }

    private void DisconnectCurrentDevice()
    {
        var connection = _connection;
        if (_rp2350ArmLeaseSent && connection?.IsConnected == true)
        {
            try
            {
                connection.SendArmLease(false);
            }
            catch
            {
                // Closing the transport still lets the short device lease expire.
            }
        }
        _rp2350ArmLeaseSent = false;
        _connection = null;
        ResetFirmwareHardwareStatus();
        if (connection is null)
        {
            return;
        }

        connection.OnCommandReceived -= Connection_CommandReceived;
        connection.OnStatusChanged -= Connection_StatusChanged;
        connection.Dispose();
        if (connection.IsSimulator && !_isDisposed)
        {
            ConnectSimulatorButton.Content = "Use simulator";
        }
    }

    private static string PreviewText(string text)
    {
        var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }

    private static int ValidInt(double value, int fallback) =>
        double.IsFinite(value) ? (int)Math.Round(value) : fallback;

    private static float ValidFloat(double value, float fallback) =>
        double.IsFinite(value) ? (float)value : fallback;

    private static double ValidDouble(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(0xFF, red, green, blue));

    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(alpha, red, green, blue));

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetimeCancellation.Cancel();
        _continuousDetectionTimer.Stop();
        _continuousDetectionTimer.Tick -= ContinuousDetectionTimer_Tick;
        _rp2350ArmLeaseTimer.Stop();
        _rp2350ArmLeaseTimer.Tick -= Rp2350ArmLeaseTimer_Tick;
        _hotkeyMonitor.OverlayPressed -= HotkeyMonitor_OverlayPressed;
        _hotkeyMonitor.DetectionPressed -= HotkeyMonitor_DetectionPressed;
        _hotkeyMonitor.PrimaryWeaponPressed -= HotkeyMonitor_PrimaryWeaponPressed;
        _hotkeyMonitor.SecondaryWeaponPressed -= HotkeyMonitor_SecondaryWeaponPressed;
        _hotkeyMonitor.Dispose();
        _mouseButtonTrigger.AimAndFireChanged -= MouseButtonTrigger_AimAndFireChanged;
        _mouseButtonTrigger.AimAndFireHeartbeat -= MouseButtonTrigger_AimAndFireHeartbeat;
        _mouseButtonTrigger.Dispose();
        if (_isArmed)
        {
            TrySendCommand("STOP");
        }
        DisconnectCurrentDevice();
        _lifetimeCancellation.Dispose();
    }

    private sealed record CalibrationSnapshot(
        float? HorizontalSensitivity,
        float? VerticalSensitivity,
        int? MouseDpi,
        float MultiplierUnit,
        Dictionary<string, float> AdsSensitivity,
        string ActiveMagnification,
        bool AutomaticMagnification,
        string? WeaponName,
        double WeaponStrength)
    {
        public static CalibrationSnapshot Capture(Settings settings, string? weaponName) => new(
            settings.HorizontalSensitivity,
            settings.VerticalSensitivity,
            settings.MouseDpi,
            settings.MouseSensitivityMultiplierUnit,
            new Dictionary<string, float>(settings.AdsSensitivity, StringComparer.OrdinalIgnoreCase),
            settings.ActiveMagnification,
            settings.AutomaticMagnificationEnabled,
            weaponName,
            string.IsNullOrWhiteSpace(weaponName)
                ? RecoilStrengthModel.Default
                : settings.GetWeaponOutputStrength(weaponName));

        public void Restore(Settings settings)
        {
            settings.HorizontalSensitivity = HorizontalSensitivity;
            settings.VerticalSensitivity = VerticalSensitivity;
            settings.MouseDpi = MouseDpi;
            settings.MouseSensitivityMultiplierUnit = MultiplierUnit;
            settings.AdsSensitivity = new Dictionary<string, float>(
                AdsSensitivity,
                StringComparer.OrdinalIgnoreCase);
            settings.ActiveMagnification = ActiveMagnification;
            settings.AutomaticMagnificationEnabled = AutomaticMagnification;
            if (!string.IsNullOrWhiteSpace(WeaponName))
            {
                settings.SetWeaponOutputStrength(WeaponName, WeaponStrength);
            }
        }
    }

    private sealed record CompensationModeOption(CompensationMode Value, string DisplayName);
    private sealed record DetectionModeOption(
        OperatorDetectionMode Value,
        string DisplayName,
        string Description);
}
