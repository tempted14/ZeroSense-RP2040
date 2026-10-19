using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RainbowRecoil;

/// <summary>
/// Overlapped Windows CDC I/O. Unlike SerialPort.Close this never flushes the
/// transmit queue: a failed USB driver must not make teardown wait for delivery.
/// Native buffers and handle leases stay alive until canceled I/O completes.
/// </summary>
internal sealed class WindowsSerialPortTransport : ISerialPortTransport
{
    private readonly object _state = new();
    private readonly object _reader = new();
    private readonly object _writer = new();
    private SafeFileHandle? _handle;
    public string PortName { get; }
    // UI status checks must not wait on a worker inside a native driver call.
    public bool IsOpen => Volatile.Read(ref _handle) is { IsClosed: false, IsInvalid: false };

    public WindowsSerialPortTransport(string portName)
    {
        if (!portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(portName.AsSpan(3), out var number) || number < 1)
            throw new ArgumentException("A Windows COM port name is required.", nameof(portName));
        PortName = "COM" + number;
    }

    public void Open()
    {
        lock (_state)
        {
            if (_handle is not null) throw new InvalidOperationException("Port is already open.");
            var handle = Native.CreateFile(@"\\.\" + PortName, 0xC0000000, 0,
                IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 5) throw new UnauthorizedAccessException($"{PortName}: access denied.");
                throw Error(error);
            }
            try
            {
                var dcb = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
                Check(Native.GetCommState(handle, ref dcb));
                dcb.BaudRate = 115200;
                dcb.Flags = 1 | (1 << 4); // binary, DTR enabled; no flow control
                dcb.ByteSize = 8;
                dcb.Parity = 0;
                dcb.StopBits = 0;
                Check(Native.SetCommState(handle, ref dcb));
                // Return available bytes immediately, otherwise wait for the
                // first byte for at most 100 ms. Quiet devices are not faults.
                var timeouts = new CommTimeouts
                {
                    ReadInterval = uint.MaxValue, ReadMultiplier = uint.MaxValue,
                    ReadConstant = 100, WriteConstant = 500
                };
                Check(Native.SetCommTimeouts(handle, ref timeouts));
                Check(Native.SetupComm(handle, 4096, 4096));
                _handle = handle;
            }
            catch { handle.Dispose(); throw; }
        }
    }

    public void Close()
    {
        lock (_state)
        {
            if (_handle is not { } handle) return;
            _handle = null;
            // No FlushFileBuffers, SerialPort event loop, or wait on the UI.
            // Outstanding operations own leases, so disposal cannot free their
            // handles/buffers prematurely or redirect old writes to a new port.
            Native.CancelIoEx(handle.DangerousGetHandle(), IntPtr.Zero);
            handle.Dispose();
        }
    }

    public void Dispose() => Close();
    public void DiscardInBuffer() => Purge(0x2 | 0x8);
    public void DiscardOutBuffer() => Purge(0x1 | 0x4);

    private void Purge(uint flags)
    {
        lock (_state)
        {
            if (_handle is not { } handle) throw new InvalidOperationException("Port is closed.");
            Check(Native.PurgeComm(handle, flags));
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_reader) return Transfer(buffer, offset, count, write: false);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_writer)
        {
            if (Transfer(buffer, offset, count, write: true) != count)
                throw new IOException("CDC write was incomplete; the session must be reconnected.");
        }
    }

    private int Transfer(byte[] buffer, int offset, int count, bool write)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return 0;
        SafeFileHandle handle;
        var leased = false;
        lock (_state)
        {
            handle = _handle ?? throw new InvalidOperationException("Port is closed.");
            handle.DangerousAddRef(ref leased);
        }
        IntPtr data = IntPtr.Zero, overlapped = IntPtr.Zero;
        SafeWaitHandle? completion = null;
        try
        {
            var nativeHandle = handle.DangerousGetHandle();
            data = Marshal.AllocHGlobal(count);
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
            completion = Native.CreateEvent(IntPtr.Zero, true, false, null);
            if (completion.IsInvalid) throw Error(Marshal.GetLastWin32Error());
            Marshal.StructureToPtr(new Overlapped { Event = completion.DangerousGetHandle() }, overlapped, false);
            if (write) Marshal.Copy(buffer, offset, data, count);
            bool started;
            int submissionError;
            lock (_state)
            {
                // Close may run while the native buffers are being allocated.
                // Never submit a stale packet AFTER its cancellation sweep.
                if (!ReferenceEquals(_handle, handle))
                    throw new IOException("The CDC session closed before the transfer started.");
                started = write
                    ? Native.WriteFile(nativeHandle, data, (uint)count, IntPtr.Zero, overlapped)
                    : Native.ReadFile(nativeHandle, data, (uint)count, IntPtr.Zero, overlapped);
                submissionError = started ? 0 : Marshal.GetLastWin32Error();
            }
            if (!started)
            {
                if (submissionError != 997) throw Error(submissionError); // ERROR_IO_PENDING
            }
            var wait = Native.WaitForSingleObject(completion, write ? 500u : 250u);
            var expired = wait != 0;
            if (expired) Native.CancelIoEx(nativeHandle, overlapped);
            // CancelIoEx does not wait. ALWAYS reap completion before freeing
            // OVERLAPPED/event/data, even if the driver delays cancellation.
            // SerialConnection's outer deadline keeps callers responsive and
            // its ownership gate prevents overlapping sessions in that case.
            var complete = Native.GetOverlappedResult(nativeHandle, overlapped, out var transferred, true);
            var completionError = complete ? 0 : Marshal.GetLastWin32Error();
            if (expired) throw new TimeoutException($"{PortName}: CDC {(write ? "write" : "read")} timed out.");
            if (!complete) throw Error(completionError);
            if (transferred > count) throw new IOException("Invalid CDC transfer length.");
            if (!write)
            {
                if (transferred == 0) throw new TimeoutException("CDC read is idle.");
                Marshal.Copy(data, buffer, offset, (int)transferred);
            }
            return (int)transferred;
        }
        finally
        {
            completion?.Dispose();
            if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (leased) handle.DangerousRelease();
        }
    }

    private static void Check(bool success)
    {
        if (!success) throw Error(Marshal.GetLastWin32Error());
    }
    private static IOException Error(int code) => new(
        new Win32Exception(code).Message, unchecked((int)(0x80070000u | (uint)code)));

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length, BaudRate, Flags;
        public ushort Reserved, XonLimit, XoffLimit;
        public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EventChar;
        public ushort Reserved1;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadInterval, ReadMultiplier, ReadConstant, WriteMultiplier, WriteConstant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public UIntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeWaitHandle CreateEvent(IntPtr security, bool manualReset, bool initialState, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetCommState(SafeFileHandle handle, ref Dcb dcb);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetupComm(SafeFileHandle handle, uint input, uint output);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool PurgeComm(SafeFileHandle handle, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(IntPtr handle, IntPtr buffer, uint count, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(IntPtr handle, IntPtr buffer, uint count, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    }
}
