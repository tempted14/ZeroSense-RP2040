using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RainbowRecoil;

/// <summary>Receives per-device mouse button transitions without intercepting input.</summary>
internal sealed class RawMouseInputMonitor : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RidevRemove = 0x00000001;
    private static readonly nuint SubclassId = 0x5A53524D;

    private readonly PhysicalMouseButtonState _buttons = new();
    private readonly SubclassProcedure _procedure;
    private nint _window;

    public bool AimAndFireHeld => _buttons.AimAndFireHeld;

    public RawMouseInputMonitor()
    {
        // Keep the delegate rooted while comctl32 owns the subclass callback.
        _procedure = WindowProcedure;
    }

    public bool Attach(nint window)
    {
        if (window == 0 || _window != 0 ||
            !SetWindowSubclass(window, _procedure, SubclassId, 0))
        {
            return false;
        }

        var mouse = new[]
        {
            new RawInputDevice
            {
                UsagePage = 1,
                Usage = 2,
                Flags = RidevInputSink | RidevDevNotify,
                Target = window
            }
        };
        if (!RegisterRawInputDevices(mouse, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            RemoveWindowSubclass(window, _procedure, SubclassId);
            return false;
        }

        _window = window;
        return true;
    }

    private nint WindowProcedure(
        nint window, uint message, nint wParam, nint lParam,
        nuint subclassId, nint reference)
    {
        if (message == WmInput)
        {
            ReadMouseInput(lParam);
        }
        else if (message == WmInputDeviceChange && wParam == 2)
        {
            _buttons.Remove(lParam); // GIDC_REMOVAL
        }

        // DefSubclassProc also performs the required WM_INPUT cleanup.
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ReadMouseInput(nint input)
    {
        var headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        if (GetRawInputData(input, RidInput, 0, ref size, headerSize) == uint.MaxValue ||
            size < headerSize + 6 || size > 4096)
        {
            return;
        }

        var data = new byte[(int)size];
        if (GetRawInputData(input, RidInput, data, ref size, headerSize) == uint.MaxValue ||
            size < headerSize + 6 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != 0)
        {
            return;
        }

        var device = IntPtr.Size == 8
            ? (nint)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8, 8))
            : (nint)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan((int)headerSize + 4, 2));
        _buttons.Apply(device, flags);
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }

        var remove = new[]
        {
            new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevRemove }
        };
        RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        RemoveWindowSubclass(_window, _procedure, SubclassId);
        _window = 0;
        _buttons.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    private delegate nint SubclassProcedure(
        nint window, uint message, nint wParam, nint lParam,
        nuint subclassId, nint reference);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window, SubclassProcedure procedure, nuint subclassId, nint reference);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window, SubclassProcedure procedure, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint input, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint input, uint command, [Out] byte[] data, ref uint size, uint headerSize);
}
