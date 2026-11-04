using System.Text;

namespace DiskMon.Core.Ipc;

/// <summary>Reads and writes the pipe's UTF-8, newline-terminated lines, refusing oversized ones.</summary>
public static class PipeLines
{
    /// <summary>Writes <paramref name="line"/> plus <c>\n</c> and flushes.</summary>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    /// <summary>
    /// Reads up to the next <c>\n</c> (a trailing <c>\r</c> is dropped). Returns null if the stream ends before any
    /// byte arrives; a final unterminated line is returned as is. Throws <see cref="InvalidDataException"/> past
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token, int maxBytes = PipeProtocol.MaxLineBytes)
    {
        // Byte at a time: lines are tiny, and reading ahead could swallow bytes that belong to the peer's next use.
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, token);
            if (read == 0)
                return buffer.Length == 0 ? null : Decode(buffer);
            if (one[0] == (byte)'\n')
                return Decode(buffer);
            if (buffer.Length >= maxBytes)
                throw new InvalidDataException($"line longer than {maxBytes} bytes");
            buffer.WriteByte(one[0]);
        }
    }

    private static string Decode(MemoryStream buffer)
    {
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.EndsWith('\r') ? text[..^1] : text;
    }
}
