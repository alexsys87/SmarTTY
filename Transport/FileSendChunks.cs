using System.IO;

namespace SmarTTY.Transport;

/// <summary>Cuts a file into the pieces that "Send file" writes to the device one by one.</summary>
public static class FileSendChunks
{
    private const byte CR = 13;

    private const byte LF = 10;

    /// <summary>
    /// Reads the next piece of the file into <paramref name="buffer"/>, which must hold <paramref name="size"/> + 1 bytes,
    /// and returns its length (0 at the end of the file). A piece never ends between a CR and the LF that follows it, so it
    /// may be one byte longer than <paramref name="size"/>: a Telnet connection sends a CR that ends a write as CR NUL
    /// (a bare CR), and the LF at the start of the next write would then be a second line end for a server that takes CR for
    /// one already. A file that cannot be read back by a step (a pipe) is cut at <paramref name="size"/> as it is.
    /// </summary>
    public static int Read(Stream stream, byte[] buffer, int size)
    {
        int count = stream.Read(buffer, 0, size);
        if (count > 0 && buffer[count - 1] == CR && stream.CanSeek)
        {
            int next = stream.ReadByte();
            if (next == LF)
            {
                buffer[count++] = LF;
            }
            else if (next >= 0)
            {
                stream.Seek(-1, SeekOrigin.Current);
            }
        }
        return count;
    }
}
