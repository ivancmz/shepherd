using System.Text;

namespace Shepherd.Protocol.Wire;

/// <summary>
/// Newline-delimited UTF-8 framing (ADR-0005): one JSON object per line.
/// Used both for the broker protocol (pipe/TCP) and for MCP over stdio.
/// </summary>
public sealed class LineChannel : IAsyncDisposable
{
    public const int DefaultMaxLineBytes = 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maxLineBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;
    private bool _discarding;

    public LineChannel(Stream input, Stream output, int maxLineBytes = DefaultMaxLineBytes)
    {
        _input = input;
        _output = output;
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>
    /// Reads the next line. Returns <c>null</c> at end of stream. A line longer than the
    /// limit is skipped entirely and reported as <see cref="LineResult.TooLarge"/>.
    /// </summary>
    public async Task<LineResult?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                var len = nl - _start;
                if (len > 0 && _buffer[nl - 1] == (byte)'\r') len--;
                var wasDiscarding = _discarding || len > _maxLineBytes;
                var text = wasDiscarding ? null : Encoding.UTF8.GetString(_buffer, _start, len);
                _start = nl + 1;
                _discarding = false;
                if (wasDiscarding) return LineResult.TooLarge;
                if (text!.Length == 0) continue; // ignore blank lines
                return new LineResult(text, false);
            }

            if (_discarding)
            {
                _start = _end = 0; // drop everything until the next newline
            }
            else if (_end - _start > _maxLineBytes)
            {
                _discarding = true;
                _start = _end = 0;
            }

            Compact();
            var n = await _input.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (_end > _start && !_discarding)
                {
                    var text = Encoding.UTF8.GetString(_buffer, _start, _end - _start).TrimEnd('\r');
                    _start = _end = 0;
                    if (text.Length > 0) return new LineResult(text, false);
                }
                return null;
            }
            _end += n;
        }
    }

    public async Task WriteLineAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Compact()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
        {
            var bigger = new byte[Math.Min(_buffer.Length * 2, _maxLineBytes + 64 * 1024)];
            if (bigger.Length <= _buffer.Length) bigger = new byte[_buffer.Length * 2];
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
            _buffer = bigger;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await _input.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_input, _output)) await _output.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record LineResult(string? Text, bool IsTooLarge)
{
    public static readonly LineResult TooLarge = new(null, true);
}
