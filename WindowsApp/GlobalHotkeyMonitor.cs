using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RainbowRecoil;

/// <summary>
/// Lightweight edge-triggered hotkey monitor. Polling is used intentionally so
/// mouse buttons (for example Shift + M1) can be configured alongside F-keys.
/// </summary>
internal sealed class GlobalHotkeyMonitor : IDisposable
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private static readonly IReadOnlyDictionary<string, int> VirtualKeys =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["M1"] = 0x01,
            ["M2"] = 0x02,
            ["F1"] = 0x70,
            ["F2"] = 0x71,
            ["F3"] = 0x72,
            ["F4"] = 0x73,
            ["F5"] = 0x74,
            ["F6"] = 0x75,
            ["F7"] = 0x76,
            ["F8"] = 0x77,
            ["F9"] = 0x78,
            ["F10"] = 0x79,
            ["F11"] = 0x7A,
            ["F12"] = 0x7B
        };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(28) };
    private bool _overlayWasPressed;
    private bool _detectionWasPressed;
    private readonly WeaponSlotHotkeyState _weaponSlotState = new();

    public Func<HotkeyChord>? OverlayChordProvider { get; set; }
    public Func<HotkeyChord>? DetectionChordProvider { get; set; }
    public Func<bool>? OverlayEnabledProvider { get; set; }
    public Func<bool>? DetectionEnabledProvider { get; set; }
    public Func<bool>? WeaponSlotHotkeysEnabledProvider { get; set; }

    public event EventHandler? OverlayPressed;
    public event EventHandler? DetectionPressed;
    public event EventHandler? PrimaryWeaponPressed;
    public event EventHandler? SecondaryWeaponPressed;

    public GlobalHotkeyMonitor()
    {
        _timer.Tick += Timer_Tick;
    }

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _overlayWasPressed = false;
        _detectionWasPressed = false;
        _weaponSlotState.Reset();
    }

    private void Timer_Tick(object? sender, object e)
    {
        var overlayPressed = OverlayEnabledProvider?.Invoke() == true &&
                             IsPressed(OverlayChordProvider?.Invoke());
        if (overlayPressed && !_overlayWasPressed)
        {
            OverlayPressed?.Invoke(this, EventArgs.Empty);
        }
        _overlayWasPressed = overlayPressed;

        var detectionPressed = DetectionEnabledProvider?.Invoke() == true &&
                               IsPressed(DetectionChordProvider?.Invoke());
        if (detectionPressed && !_detectionWasPressed)
        {
            DetectionPressed?.Invoke(this, EventArgs.Empty);
        }
        _detectionWasPressed = detectionPressed;

        var slotsEnabled = WeaponSlotHotkeysEnabledProvider?.Invoke() == true;
        var primaryPressed = slotsEnabled && IsVirtualKeyDown(0x31);
        var secondaryPressed = slotsEnabled && IsVirtualKeyDown(0x32);
        var slotEdges = _weaponSlotState.Update(primaryPressed, secondaryPressed);
        if (slotEdges.PrimaryPressed)
        {
            PrimaryWeaponPressed?.Invoke(this, EventArgs.Empty);
        }
        if (slotEdges.SecondaryPressed)
        {
            SecondaryWeaponPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsPressed(HotkeyChord? chord)
    {
        if (chord is null || !VirtualKeys.TryGetValue(chord.Key, out var virtualKey))
        {
            return false;
        }

        var modifierPressed = chord.Modifier.ToUpperInvariant() switch
        {
            "SHIFT" => IsVirtualKeyDown(VkShift),
            "CTRL" => IsVirtualKeyDown(VkControl),
            "ALT" => IsVirtualKeyDown(VkMenu),
            _ => true
        };
        return modifierPressed && IsVirtualKeyDown(virtualKey);
    }

    private static bool IsVirtualKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        Stop();
        _timer.Tick -= Timer_Tick;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
