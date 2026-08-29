using System.Buffers.Binary;
using System.Text;

namespace OhMyBot.Plugins.ImageConverter;

public readonly record struct DetectedMediaFormat(
    string Extension,
    string ContentType,
    bool IsAnimated);

public static class MediaHeaderDetector
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static DetectedMediaFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 6
            && (content[..6].SequenceEqual("GIF87a"u8) || content[..6].SequenceEqual("GIF89a"u8)))
        {
            return new DetectedMediaFormat("gif", "image/gif", IsAnimated: true);
        }

        if (content.StartsWith(PngSignature))
        {
            return new DetectedMediaFormat("png", "image/png", IsPngAnimated(content));
        }

        if (content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff)
        {
            return new DetectedMediaFormat("jpg", "image/jpeg", IsAnimated: false);
        }

        if (content.Length >= 12
            && content[..4].SequenceEqual("RIFF"u8)
            && content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return new DetectedMediaFormat("webp", "image/webp", IsWebpAnimated(content));
        }

        if (content.Length >= 4
            && content[0] == 0x1a
            && content[1] == 0x45
            && content[2] == 0xdf
            && content[3] == 0xa3)
        {
            return DetectEbml(content);
        }

        if (content.Length >= 12 && content.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return DetectIsoBaseMedia(content.Slice(8, 4));
        }

        if (content.Length >= 2 && content[..2].SequenceEqual("BM"u8))
        {
            return new DetectedMediaFormat("bmp", "image/bmp", IsAnimated: false);
        }

        if (content.Length >= 4
            && ((content[0] == 0x49 && content[1] == 0x49 && content[2] == 0x2a && content[3] == 0x00)
                || (content[0] == 0x4d && content[1] == 0x4d && content[2] == 0x00 && content[3] == 0x2a)))
        {
            return new DetectedMediaFormat("tiff", "image/tiff", IsAnimated: false);
        }

        return null;
    }

    private static DetectedMediaFormat? DetectEbml(ReadOnlySpan<byte> content)
    {
        const int ebmlIdLength = 4;
        if (!TryReadEbmlSize(content[ebmlIdLength..], out var headerLength, out var sizeLength)
            || headerLength > int.MaxValue)
        {
            return null;
        }

        var headerStart = ebmlIdLength + sizeLength;
        if (headerStart > content.Length || headerLength > (ulong)(content.Length - headerStart))
        {
            return null;
        }

        var header = content.Slice(headerStart, (int)headerLength);
        for (var offset = 0; offset + 2 < header.Length; offset++)
        {
            // EBML DocType element ID (0x4282).
            if (header[offset] != 0x42 || header[offset + 1] != 0x82)
            {
                continue;
            }

            var valueOffset = offset + 2;
            if (!TryReadEbmlSize(header[valueOffset..], out var valueLength, out var valueSizeLength)
                || valueLength > 16)
            {
                return null;
            }

            valueOffset += valueSizeLength;
            if (valueLength > (ulong)(header.Length - valueOffset))
            {
                return null;
            }

            var docType = Encoding.ASCII.GetString(header.Slice(valueOffset, (int)valueLength));
            return docType.ToLowerInvariant() switch
            {
                "webm" => new DetectedMediaFormat("webm", "video/webm", IsAnimated: true),
                "matroska" => new DetectedMediaFormat("mkv", "video/x-matroska", IsAnimated: true),
                _ => null
            };
        }

        return null;
    }

    private static bool TryReadEbmlSize(
        ReadOnlySpan<byte> content,
        out ulong value,
        out int encodedLength)
    {
        value = 0;
        encodedLength = 0;
        if (content.IsEmpty || content[0] == 0)
        {
            return false;
        }

        var marker = 0x80;
        while ((content[0] & marker) == 0)
        {
            marker >>= 1;
            encodedLength++;
        }

        encodedLength++;
        if (encodedLength > 8 || content.Length < encodedLength)
        {
            return false;
        }

        value = (ulong)(content[0] & (marker - 1));
        for (var index = 1; index < encodedLength; index++)
        {
            value = (value << 8) | content[index];
        }

        return true;
    }

    private static DetectedMediaFormat DetectIsoBaseMedia(ReadOnlySpan<byte> majorBrandBytes)
    {
        var majorBrand = Encoding.ASCII.GetString(majorBrandBytes);
        return majorBrand switch
        {
            "avif" => new DetectedMediaFormat("avif", "image/avif", IsAnimated: false),
            "avis" => new DetectedMediaFormat("avif", "image/avif", IsAnimated: true),
            "heic" or "heix" or "hevc" or "hevx" or "mif1" =>
                new DetectedMediaFormat("heif", "image/heif", IsAnimated: false),
            "msf1" => new DetectedMediaFormat("heif", "image/heif", IsAnimated: true),
            _ => new DetectedMediaFormat("mp4", "video/mp4", IsAnimated: true)
        };
    }

    private static bool IsPngAnimated(ReadOnlySpan<byte> content)
    {
        var offset = PngSignature.Length;
        while (offset + 12 <= content.Length)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(offset, 4));
            var nextOffset = (long)offset + 12 + chunkLength;
            if (nextOffset > content.Length)
            {
                return false;
            }

            var chunkType = content.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual("acTL"u8))
            {
                return true;
            }

            if (chunkType.SequenceEqual("IEND"u8))
            {
                return false;
            }

            offset = checked((int)nextOffset);
        }

        return false;
    }

    private static bool IsWebpAnimated(ReadOnlySpan<byte> content)
    {
        var offset = 12;
        while (offset + 8 <= content.Length)
        {
            var chunkType = content.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset + 4, 4));
            var nextOffset = (long)offset + 8 + chunkLength + (chunkLength & 1);
            if (nextOffset > content.Length)
            {
                return false;
            }

            if (chunkType.SequenceEqual("ANIM"u8))
            {
                return true;
            }

            if (chunkType.SequenceEqual("VP8X"u8)
                && chunkLength >= 1
                && (content[offset + 8] & 0x02) != 0)
            {
                return true;
            }

            offset = checked((int)nextOffset);
        }

        return false;
    }
}
