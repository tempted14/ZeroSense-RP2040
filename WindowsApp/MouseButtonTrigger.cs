using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RainbowRecoil;

/// <summary>
/// Polls the physical mouse buttons without suppressing or modifying the user's
/// normal mouse input. A burst is active only while aim and fire are both held.
/// </summary>
public sealed class MouseButtonTrigger : IDisposable
{
    private const int VirtualKeyLeftButton = 0x01;
    private const int VirtualKeyRightButton = 0x02;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(4) };
    private bool _lastPressed;
    private long _lastHeartbeatTimestamp;

    public event EventHandler<bool>? AimAndFireChanged;
    public event EventHandler? AimAndFireHeartbeat;

    public bool IsPressed { get; private set; }

    public MouseButtonTrigger()
    {
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        IsPressed = false;
        _lastPressed = false;
        _lastHeartbeatTimestamp = 0;
    }

    private void OnTick(object? sender, object e) => Poll();

    private void Poll()
    {
        IsPressed = IsDown(VirtualKeyLeftButton) && IsDown(VirtualKeyRightButton);
        if (IsPressed == _lastPressed)
        {
            if (IsPressed &&
                Stopwatch.GetElapsedTime(_lastHeartbeatTimestamp) >= TimeSpan.FromMilliseconds(250))
            {
                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
                AimAndFireHeartbeat?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        _lastPressed = IsPressed;
        _lastHeartbeatTimestamp = IsPressed ? Stopwatch.GetTimestamp() : 0;
        AimAndFireChanged?.Invoke(this, IsPressed);
    }

    private static bool IsDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTick;
    }
}
