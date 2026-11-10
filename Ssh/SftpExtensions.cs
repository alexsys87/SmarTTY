using System;
using System.Collections.Generic;
using System.Text;

namespace SmarTTY.Ssh;

/// <summary>
/// The extension requests of SFTP that the client uses, and how a server announces them (SSH_FXP_VERSION carries pairs of
/// extension name and data after the version number). Free of the SSH library, so that tests/KeyFileReplaceTests can speak it
/// to a real sftp-server.
/// </summary>
internal static class SftpExtensions
{
    /// <summary>
    /// A rename that replaces an existing target at once (OpenSSH, and the sftp-server of Win32-OpenSSH, which is built from
    /// it). SFTP version 3 has only SSH_FXP_RENAME, which refuses to replace a file.
    /// </summary>
    public const string PosixRename = "posix-rename@openssh.com";

    public const byte FxpExtended = 200;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The extensions in a version packet: <paramref name="packet"/> is what comes after the length (the type byte, the version,
    /// then the pairs). A list that is cut off ends the pairs read so far; it is no reason to refuse the server.
    /// </summary>
    public static Dictionary<string, string> ParseVersionExtensions(byte[] packet)
    {
        Dictionary<string, string> extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        int position = 5;
        while (packet != null && position < packet.Length)
        {
            string name = ReadString(packet, ref position);
            string data = name == null ? null : ReadString(packet, ref position);
            if (name == null || data == null)
            {
                break;
            }
            extensions[name] = data;
        }
        return extensions;
    }

    private static string ReadString(byte[] packet, ref int position)
    {
        if (position + 4 > packet.Length)
        {
            return null;
        }
        long length = (uint)(packet[position] << 24 | packet[position + 1] << 16 | packet[position + 2] << 8 | packet[position + 3]);
        if (length > packet.Length - position - 4)
        {
            return null;
        }
        string text = Utf8.GetString(packet, position + 4, (int)length);
        position += 4 + (int)length;
        return text;
    }

    /// <summary>
    /// A complete SSH_FXP_EXTENDED packet, length included: the type, the request id, the name of the extension and its
    /// arguments (all of them strings; posix-rename takes the old and the new path).
    /// </summary>
    public static byte[] BuildExtendedRequest(uint id, string extension, params string[] arguments)
    {
        List<byte> body = new List<byte> { FxpExtended };
        AddUInt32(body, id);
        AddString(body, extension);
        foreach (string argument in arguments)
        {
            AddString(body, argument);
        }
        List<byte> packet = new List<byte>();
        AddUInt32(packet, (uint)body.Count);
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AddString(List<byte> bytes, string text)
    {
        byte[] data = Utf8.GetBytes(text);
        AddUInt32(bytes, (uint)data.Length);
        bytes.AddRange(data);
    }
}
