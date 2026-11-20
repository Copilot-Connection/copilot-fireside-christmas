using System.Buffers.Binary;

namespace Fireside;

public static class LandingImages
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const string DefaultUrl = "/branding/christmas-fireside.png";
    public const string DefaultDescription = "Copilot Fireside Chat, 16 December at 16:00 GMT. Caroline Kallin, Claire Edgson, Mark Rackley and Jon Jarvis. Register at events.empowering.cloud.";

    public static string? ContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 45 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.Slice(12, 4).SequenceEqual("IHDR"u8) &&
            data.Slice(data.Length - 8, 4).SequenceEqual("IEND"u8) &&
            ValidDimensions(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4))))
            return "image/png";

        if (data.Length < 12 || data[0] != 255 || data[1] != 216 ||
            data[^2] != 255 || data[^1] != 217) return null;
        var offset = 2;
        while (offset + 4 < data.Length)
        {
            if (data[offset++] != 255) return null;
            while (offset < data.Length && data[offset] == 255) offset++;
            if (offset >= data.Length) return null;
            var marker = data[offset++];
            if (marker is 218 or 217 || offset + 2 > data.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (length < 2 || offset + length > data.Length) return null;
            if (marker is >= 192 and <= 207 and not 196 and not 200 and not 204)
            {
                if (length < 8) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return ValidDimensions(width, height) ? "image/jpeg" : null;
            }
            offset += length;
        }
        return null;
    }

    private static bool ValidDimensions(uint width, uint height) =>
        width is > 0 and <= 8192 && height is > 0 and <= 8192 && (ulong)width * height <= 24_000_000;
}
