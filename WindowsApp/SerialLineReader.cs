using System;
using System.Collections.Generic;
using System.Text;

namespace RainbowRecoil;

/// <summary>Bounded UTF-8 line framing that survives read timeouts and bad lines.</summary>
internal sealed class SerialLineReader
{
    internal const int MaximumLineBytes = 2048;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] _line = new byte[MaximumLineBytes];
    private readonly byte[] _chunk = new byte[256];
    private readonly Queue<string> _ready = new();
    private int _length;
    private bool _discardUntilNewline;

    public int RejectedLines { get; private set; }

    public string? ReadLine(ISerialPortTransport port)
    {
        if (_ready.Count == 0)
        {
            var count = port.Read(_chunk, 0, _chunk.Length);
            Feed(_chunk.AsSpan(0, count));
        }
        return _ready.TryDequeue(out var line) ? line : null;
    }

    internal void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                if (!_discardUntilNewline)
                {
                    try
                    {
                        var text = StrictUtf8.GetString(_line, 0, _length).TrimEnd('\r');
                        if (text.Length > 0) _ready.Enqueue(text);
                    }
                    catch (DecoderFallbackException)
                    {
                        ++RejectedLines;
                    }
                }
                _length = 0;
                _discardUntilNewline = false;
            }
            else if (!_discardUntilNewline)
            {
                if (_length == MaximumLineBytes)
                {
                    ++RejectedLines;
                    _length = 0;
                    _discardUntilNewline = true;
                }
                else
                {
                    _line[_length++] = value;
                }
            }
        }
    }
}
