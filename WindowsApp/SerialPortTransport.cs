using System;
using System.IO.Ports;

namespace RainbowRecoil;

// The production transport and fault-injection tests use the same connection
// lifecycle. No Windows COM driver is needed to replay lost data or hung closes.
internal interface ISerialPortTransport : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
    void DiscardInBuffer();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}

internal sealed class SerialPortTransport : ISerialPortTransport
{
    private readonly SerialPort _port;

    public SerialPortTransport(string portName)
    {
        _port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 100,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = false
        };
    }

    public string PortName => _port.PortName;
    public bool IsOpen => _port.IsOpen;
    public void Open() => _port.Open();
    public void Close() => _port.Close();
    public void DiscardInBuffer() => _port.DiscardInBuffer();
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void Dispose() => _port.Dispose();
}
