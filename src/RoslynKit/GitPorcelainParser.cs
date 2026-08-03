using System.Text;

namespace RoslynKit;

/// <summary>
/// Parses NUL-delimited Git porcelain records without treating paths as line-oriented text.
/// </summary>
internal static class GitPorcelainParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryParse(
        ReadOnlySpan<byte> output,
        out IReadOnlyList<GitStatusFingerprint> entries,
        out string? diagnostic)
    {
        var parsed = new List<GitStatusFingerprint>();
        var offset = 0;

        while (offset < output.Length)
        {
            if (!TryReadRecord(output, ref offset, out var record))
            {
                entries = [];
                diagnostic = "Git status output was not terminated by a NUL byte.";
                return false;
            }

            if (record.Length < 4 || record[2] != (byte)' ')
            {
                entries = [];
                diagnostic = "Git status output contained an invalid porcelain record prefix.";
                return false;
            }

            if (!IsStatusByte(record[0])
                || !IsStatusByte(record[1])
                || (record[0] == (byte)' ' && record[1] == (byte)' '))
            {
                entries = [];
                diagnostic = "Git status output contained an invalid status code.";
                return false;
            }

            if (!TryDecodePath(record[3..], out var path))
            {
                entries = [];
                diagnostic = "Git status output contained an empty or non-UTF-8 path.";
                return false;
            }

            string? originalPath = null;
            if (IsRenameOrCopy(record[0]) || IsRenameOrCopy(record[1]))
            {
                if (!TryReadRecord(output, ref offset, out var originalPathBytes))
                {
                    entries = [];
                    diagnostic = "Git status output omitted the original path for a rename or copy record.";
                    return false;
                }

                if (!TryDecodePath(originalPathBytes, out originalPath))
                {
                    entries = [];
                    diagnostic = "Git status output contained an empty or non-UTF-8 original path.";
                    return false;
                }
            }

            parsed.Add(new GitStatusFingerprint(
                string.Create(2, (record[0], record[1]), static (chars, state) =>
                {
                    chars[0] = (char)state.Item1;
                    chars[1] = (char)state.Item2;
                }),
                path,
                originalPath));
        }

        entries = parsed;
        diagnostic = null;
        return true;
    }

    private static bool TryReadRecord(
        ReadOnlySpan<byte> output,
        ref int offset,
        out ReadOnlySpan<byte> record)
    {
        var remaining = output[offset..];
        var terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            record = default;
            return false;
        }

        record = remaining[..terminator];
        offset += terminator + 1;
        return true;
    }

    private static bool TryDecodePath(ReadOnlySpan<byte> bytes, out string path)
    {
        if (bytes.IsEmpty)
        {
            path = string.Empty;
            return false;
        }

        try
        {
            path = StrictUtf8.GetString(bytes);
            return path.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            path = string.Empty;
            return false;
        }
    }

    private static bool IsStatusByte(byte value)
    {
        return value is (byte)' '
            or (byte)'M'
            or (byte)'T'
            or (byte)'A'
            or (byte)'D'
            or (byte)'R'
            or (byte)'C'
            or (byte)'U'
            or (byte)'?'
            or (byte)'!';
    }

    private static bool IsRenameOrCopy(byte value)
    {
        return value is (byte)'R' or (byte)'C';
    }
}
