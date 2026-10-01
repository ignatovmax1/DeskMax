using System.IO;

namespace DeskMax.Windows;

public static class JpegFrame
{
    // Read the SOF dimensions before handing untrusted bytes to the image decoder.
    public static (int Width, int Height) ReadSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) throw new InvalidDataException("Не JPEG-кадр.");
        var offset = 2;
        while (offset < data.Length)
        {
            if (data[offset++] != 0xff) break;
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) break;
            var marker = data[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > data.Length) break;
            var length = (data[offset] << 8) | data[offset + 1];
            if (length < 2 || offset + length > data.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) break;
                var height = (data[offset + 3] << 8) | data[offset + 4];
                var width = (data[offset + 5] << 8) | data[offset + 6];
                if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException("Недопустимое разрешение кадра.");
                return (width, height);
            }
            offset += length;
        }
        throw new InvalidDataException("Повреждённый JPEG-кадр.");
    }
}
