using System.Diagnostics;
using RainbowRecoil;

internal static class UsbProbe
{
    // Explicit opt-in only: ordinary CI must never open somebody's COM port.
    // Sends PING/STATUS and a safety STOP on close, never START/configuration.
    public static async Task<int> RunAsync(string portName, int cycles)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (cycles is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(cycles));
        var elapsed = Stopwatch.StartNew();
        for (var cycle = 1; cycle <= cycles; ++cycle)
        {
            using var connection = new SerialConnection(portName);
            string? resetReason = null;
            var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var metrics = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.OnCommandReceived += (_, line) =>
            {
                if (line?.StartsWith("STATUS:HASH=", StringComparison.Ordinal) == true)
                    status.TrySetResult(line);
                if (line?.StartsWith("BUILD:", StringComparison.Ordinal) == true && cycle == 1)
                    Console.WriteLine(line);
                if (line?.StartsWith("LAST_RESET:", StringComparison.Ordinal) == true)
                    Volatile.Write(ref resetReason, line);
                if (line?.StartsWith("METRICS:", StringComparison.Ordinal) == true)
                    metrics.TrySetResult(line);
            };
            await connection.ConnectAsync(CancellationToken.None);
            connection.SendCommand("STATUS");
            var statusLine = await status.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // LAST_RESET follows STATUS; wait for the end of the diagnostic
            // block before checking it, rather than racing the read loop.
            var metricLine = await metrics.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!FirmwareStatusParser.TryParse(metricLine, out var snapshot) || snapshot.CdcDroppedMessages != 0)
                throw new IOException("Incomplete or dropped firmware diagnostic replies: " + metricLine);
            if (cycle == 1 || cycle % 100 == 0 || cycle == cycles)
                Console.WriteLine($"Cycle {cycle}/{cycles}: {statusLine}");
            if (Volatile.Read(ref resetReason) is { } reason)
                throw new IOException("Board reports a watchdog reset; stress check is not clean: " + reason);
            connection.Disconnect();
            // Next Connect exercises the actual close/cancel ownership gate.
        }
        await Task.Delay(500);
        Console.WriteLine($"PASS native CDC connect/status/close: {cycles} cycles in {elapsed.Elapsed.TotalSeconds:F1}s; no movement emitted.");
        return 0;
    }

    // Changes only the board's volatile configuration with output kept OFF.
    // Never saves profiles/settings files. The app restores the user's current
    // configuration on its next verified connection.
    public static async Task<int> RunConfigurationAsync(string portName, int transactions)
    {
        if (transactions is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(transactions));
        using var connection = new SerialConnection(portName);
        await connection.ConnectAsync(CancellationToken.None);
        for (var index = 0; index < transactions; ++index)
        {
            var pattern = index % 2 == 0;
            var profile = WeaponProfile.FindByName(pattern ? "F2" : "TCSG12")!;
            var mode = pattern ? CompensationMode.WeaponPattern : CompensationMode.General;
            var rapid = !pattern;
            var variance = index % 3 != 0;
            var noise = index % 4 != 0;
            var kick = pattern ? 2.0f : 1.0f;
            var scale = new SensitivityScale(0.523f, 1.237f);
            var hash = SerialProtocol.ComputeConfigurationHash(profile, mode, scale,
                rapid, rapid ? 960 : 0, variance, noise, kick);
            await connection.SynchronizeConfigurationAsync(profile, mode, scale, rapid,
                rapid ? 960 : 0, variance, noise, kick, CancellationToken.None);
            var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLine(object? sender, string? line)
            {
                if (line?.StartsWith("STATUS:", StringComparison.Ordinal) == true)
                    status.TrySetResult(line);
            }
            connection.OnCommandReceived += OnLine;
            try
            {
                connection.SendCommand("STATUS");
                var line = await status.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (!FirmwareContract.CommittedStatusMatches(line, hash) || !line.EndsWith(":FIRE=OFF", StringComparison.Ordinal))
                    throw new IOException("Configuration/status mismatch: " + line);
                Console.WriteLine($"PASS transaction {index + 1}: {profile.Name}, variance={variance}, noise={noise}, rapid={rapid}, kick={kick}; {line}");
            }
            finally { connection.OnCommandReceived -= OnLine; }
        }
        connection.Disconnect();
        Console.WriteLine($"PASS {transactions} real configuration transactions; no START, arm lease, or movement emitted.");
        return 0;
    }

    public static void InvalidPortsAreRejected()
    {
        foreach (var invalid in new[] { "COM0", "COM", "COM6\\file", @"\\.\PhysicalDrive0", "LPT1" })
        {
            try { using var port = new WindowsSerialPortTransport(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("Native CDC accepted an invalid path: " + invalid);
        }
        using var valid = new WindowsSerialPortTransport("com256");
        if (valid.PortName != "COM256" || valid.IsOpen) throw new Exception("Port normalization failed.");
        valid.Close();
        valid.Dispose();
    }

    public static void StatusDoesNotWaitForNativeOperations()
    {
        using var port = new WindowsSerialPortTransport("COM6");
        // Simulate a worker held inside the native submission/cleanup lock.
        // No live port or Windows driver is opened by this regression test.
        var state = typeof(WindowsSerialPortTransport).GetField("_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(port)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var nativeWorker = Task.Run(() =>
        {
            lock (state) { entered.Set(); release.Wait(); }
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Test worker did not start.");
            var status = Task.Run(() => port.IsOpen);
            if (!status.Wait(TimeSpan.FromSeconds(1)))
                throw new Exception("Native CDC status waited on the driver's worker lock.");
            if (status.Result) throw new Exception("An unopened native port reported connected.");
        }
        finally { release.Set(); nativeWorker.GetAwaiter().GetResult(); }
    }

    // Explicit live-hardware test for buffer-allocation/submission vs Close.
    // Opens CDC and cancels reads only: no protocol command or mouse output.
    public static async Task<int> RunCloseRaceAsync(string portName, int cycles)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (cycles is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(cycles));
        for (var cycle = 0; cycle < cycles; ++cycle)
        {
            using var port = new WindowsSerialPortTransport(portName);
            await Task.Run(port.Open).WaitAsync(TimeSpan.FromSeconds(6));
            var reading = Task.Run(() =>
            {
                try { var bytes = new byte[4096]; port.Read(bytes, 0, bytes.Length); }
                catch (Exception error) when (error is IOException or TimeoutException or
                    InvalidOperationException or ObjectDisposedException) { }
            });
            if (cycle % 2 == 0) await Task.Delay(1);
            await Task.Run(port.Close).WaitAsync(TimeSpan.FromSeconds(3));
            await reading.WaitAsync(TimeSpan.FromSeconds(3));
            if (port.IsOpen) throw new IOException("Canceled CDC session stayed open.");
        }
        Console.WriteLine($"PASS native CDC read/close races: {cycles} cycles; no commands or movement emitted.");
        return 0;
    }
}
