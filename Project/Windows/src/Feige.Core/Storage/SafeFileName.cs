using System.Text;

namespace Feige.Core.Storage;

/// <summary>
/// Makes a protocol-legal file name (通信协议 §4.4 forbids only separators and control characters) writable on
/// Windows, where <c>&lt; &gt; : " | ? *</c>, reserved device names and trailing dots/spaces are additionally illegal.
/// </summary>
/// <remarks>
/// The original name is preserved whenever it is already valid, so a normal <c>photo.jpg</c> arrives unchanged.
/// This is a local storage concern: the sender is not required to know the receiver's file system rules.
/// </remarks>
public static class SafeFileName
{
    private const int MaxNameLength = 120;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>V3 rejects unsupported Windows names before creating a temporary file.</summary>
    public static void ValidateForWindows(string fileName)
    {
        Protocol.TransferManifest.ValidateFileName(fileName);
        string stem = fileName.Split('.')[0].TrimEnd(' ');
        bool reserved = Array.Exists(ReservedNames, item => string.Equals(item, stem, StringComparison.OrdinalIgnoreCase)) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is '¹' or '²' or '³';
        if (reserved || fileName.IndexOfAny(InvalidChars) >= 0 || fileName.EndsWith(' ') || fileName.EndsWith('.'))
            throw new Protocol.ProtocolException(Protocol.ProtocolError.InvalidManifest);
    }

    /// <summary>
    /// Returns a name that can be created in a directory: invalid characters replaced by <c>_</c>, reserved device
    /// names prefixed with <c>_</c>, trailing dots and spaces removed, length capped.
    /// </summary>
    public static string MakeWritable(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var builder = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            builder.Append(c < ' ' || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c);
        }

        string result = builder.ToString().TrimEnd(' ', '.');
        if (result.Length == 0)
        {
            result = "_";
        }

        string stem = Path.GetFileNameWithoutExtension(result);
        if (Array.Exists(ReservedNames, reserved => string.Equals(reserved, stem, StringComparison.OrdinalIgnoreCase)))
        {
            result = "_" + result;
        }

        if (result.Length > MaxNameLength)
        {
            string extension = Path.GetExtension(result);
            if (extension.Length > 20)
            {
                extension = string.Empty;
            }

            result = string.Concat(result.AsSpan(0, MaxNameLength - extension.Length), extension);
        }

        return result;
    }

    /// <summary>
    /// Builds the n-th candidate name: <c>report.pdf</c>, <c>report (1).pdf</c>, <c>report (2).pdf</c>, …
    /// </summary>
    public static string WithSuffix(string fileName, int attempt)
    {
        if (attempt <= 0)
        {
            return fileName;
        }

        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        return $"{stem} ({attempt}){extension}";
    }
}
