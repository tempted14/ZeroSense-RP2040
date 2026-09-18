using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RainbowRecoil;

internal readonly record struct CaptureRegion(
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent);

/// <summary>Captures a bounded desktop region directly into an OCR-ready bitmap.</summary>
internal static class ScreenCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int DibRgbColors = 0;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    public static SoftwareBitmap Capture(CaptureRegion region, uint maximumDimension)
    {
        var virtualX = GetSystemMetrics(SmXVirtualScreen);
        var virtualY = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = GetSystemMetrics(SmCxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            throw new InvalidOperationException("Windows did not report a valid desktop capture area.");
        }

        var sourceX = virtualX + (int)Math.Round(virtualWidth * region.XPercent / 100.0);
        var sourceY = virtualY + (int)Math.Round(virtualHeight * region.YPercent / 100.0);
        var sourceWidth = Math.Max(1, (int)Math.Round(virtualWidth * region.WidthPercent / 100.0));
        var sourceHeight = Math.Max(1, (int)Math.Round(virtualHeight * region.HeightPercent / 100.0));
        sourceWidth = Math.Min(sourceWidth, virtualX + virtualWidth - sourceX);
        sourceHeight = Math.Min(sourceHeight, virtualY + virtualHeight - sourceY);

        var safeMaximum = maximumDimension is > 0 and < 8192 ? maximumDimension : 2048;
        var scale = Math.Min(1.0, safeMaximum / (double)Math.Max(sourceWidth, sourceHeight));
        var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw LastWin32Exception("Could not access the desktop surface.");
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw LastWin32Exception("Could not allocate the screen capture buffer.");
            }

            previousBitmap = SelectObject(memoryDc, bitmap);
            SetStretchBltMode(memoryDc, Halftone);
            if (!StretchBlt(
                    memoryDc,
                    0,
                    0,
                    targetWidth,
                    targetHeight,
                    screenDc,
                    sourceX,
                    sourceY,
                    sourceWidth,
                    sourceHeight,
                    SrcCopy | CaptureBlt))
            {
                throw LastWin32Exception("Windows could not capture the selected screen region.");
            }

            // GetDIBits requires the bitmap not to be selected into a device context.
            SelectObject(memoryDc, previousBitmap);
            previousBitmap = IntPtr.Zero;

            var pixels = new byte[checked(targetWidth * targetHeight * 4)];
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = targetWidth,
                    Height = -targetHeight,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)pixels.Length
                }
            };
            if (GetDIBits(
                    memoryDc,
                    bitmap,
                    0,
                    (uint)targetHeight,
                    pixels,
                    ref bitmapInfo,
                    DibRgbColors) == 0)
            {
                throw LastWin32Exception("Windows could not read the captured pixels.");
            }

            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                targetWidth,
                targetHeight,
                BitmapAlphaMode.Ignore);
            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            var buffer = writer.DetachBuffer();
            softwareBitmap.CopyFromBuffer(buffer);
            return softwareBitmap;
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousBitmap);
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

    public static bool IsCurrentProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    public static bool IsRainbowSixForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Contains("RainbowSix", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (SystemException)
        {
            // Protected/elevated processes can deny metadata access. Failing
            // closed prevents number-row typing in another app changing slots.
            return false;
        }
    }

    private static Win32Exception LastWin32Exception(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint operation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);
}
