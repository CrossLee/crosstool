using System.Buffers.Binary;

namespace Crosio.Windows.Media;

/// <summary>
/// Minimal top-level ISO BMFF validation used after the native completion
/// callback. It does not replace media decoding; it prevents an empty or
/// obviously unfinished .mp4 from being promoted as a successful recording.
/// </summary>
public static class Mp4ContainerInspector
{
    private const int BoxHeaderBytes = 8;
    private const int ExtendedBoxHeaderBytes = 16;

    public static bool IsFinalized(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return IsFinalized(stream);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFinalized(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Length < BoxHeaderBytes)
        {
            return false;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var hasFileType = false;
            var hasMovieMetadata = false;
            var hasMediaData = false;
            Span<byte> header = stackalloc byte[ExtendedBoxHeaderBytes];

            while (stream.Position <= stream.Length - BoxHeaderBytes)
            {
                var boxStart = stream.Position;
                if (!ReadExactly(stream, header[..BoxHeaderBytes]))
                {
                    return false;
                }

                var shortSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var type = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
                long boxSize;
                var headerSize = BoxHeaderBytes;

                if (shortSize == 1)
                {
                    if (!ReadExactly(stream, header.Slice(BoxHeaderBytes, 8)))
                    {
                        return false;
                    }

                    var extendedSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(BoxHeaderBytes, 8));
                    if (extendedSize > long.MaxValue)
                    {
                        return false;
                    }

                    boxSize = (long)extendedSize;
                    headerSize = ExtendedBoxHeaderBytes;
                }
                else if (shortSize == 0)
                {
                    boxSize = stream.Length - boxStart;
                }
                else
                {
                    boxSize = shortSize;
                }

                if (boxSize < headerSize || boxSize > stream.Length - boxStart)
                {
                    return false;
                }

                switch (type)
                {
                    case 0x66747970: // ftyp
                        hasFileType = true;
                        break;
                    case 0x6D6F6F76: // moov
                        hasMovieMetadata = true;
                        break;
                    case 0x6D646174: // mdat
                        hasMediaData = boxSize > headerSize;
                        break;
                }

                stream.Position = checked(boxStart + boxSize);
            }

            return hasFileType && hasMovieMetadata && hasMediaData;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
