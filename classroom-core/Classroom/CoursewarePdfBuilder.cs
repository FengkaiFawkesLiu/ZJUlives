using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ZJUSS.Core.Classroom;

public enum CoursewarePdfError
{
    None,
    NoImages,
    UnsupportedFormat,
    CorruptedImage,
    ImageTooLarge,
    TooManyPages
}

public sealed class CoursewarePdfException : Exception
{
    public CoursewarePdfException(CoursewarePdfError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public CoursewarePdfError Error { get; }
}

/// <summary>
/// 把一次课节的幻灯片图片按顺序合成为一份 PDF。纯托管实现、无第三方依赖：
/// JPEG 直接以 DCTDecode 嵌入；PNG（8 位灰度/RGB/RGBA、非隔行）解码后以 FlateDecode 嵌入。
/// 图片顺序即页面顺序；失败时抛出 <see cref="CoursewarePdfException"/>，不产生半成品。
/// </summary>
public static class CoursewarePdfBuilder
{
    public const int MaxPages = 500;
    public const long MaxPixelsPerImage = 40_000_000;

    private const int PageWidthPoints = 842;
    private const int MaxPageHeightPoints = 14400;

    public static byte[] Build(IReadOnlyList<byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        if (images.Count == 0)
        {
            throw new CoursewarePdfException(CoursewarePdfError.NoImages, "这一节课没有可导出的课件页。");
        }

        if (images.Count > MaxPages)
        {
            throw new CoursewarePdfException(
                CoursewarePdfError.TooManyPages,
                $"课件页数超过上限（{MaxPages} 页）。");
        }

        var parsed = new List<PdfImage>(images.Count);
        foreach (var image in images)
        {
            if (image is null || image.Length == 0)
            {
                throw new CoursewarePdfException(CoursewarePdfError.CorruptedImage, "课件图片内容为空。");
            }

            parsed.Add(ParseImage(image));
        }

        return WritePdf(parsed);
    }

    private static PdfImage ParseImage(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return ParseJpeg(bytes);
        }

        if (bytes.Length > 8 && IsPng(bytes))
        {
            return ParsePng(bytes);
        }

        throw new CoursewarePdfException(
            CoursewarePdfError.UnsupportedFormat,
            "课件包含暂不支持的图片格式（仅支持 JPEG 和 8 位 PNG）。");
    }

    private static PdfImage ParseJpeg(byte[] bytes)
    {
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                offset += 2;
                continue;
            }

            if (offset + 4 > bytes.Length)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2, 2));
            if (length < 2 || offset + 2 + length > bytes.Length)
            {
                throw Corrupted("JPEG 段长度不正确。");
            }

            if (IsStartOfFrame(marker))
            {
                if (offset + 9 >= bytes.Length)
                {
                    throw Corrupted("JPEG 帧头不完整。");
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 7, 2));
                var components = bytes[offset + 9];
                EnsureSize(width, height);
                return new PdfImage(
                    PdfImageKind.Jpeg,
                    width,
                    height,
                    components switch
                    {
                        1 => 1,
                        4 => 4,
                        _ => 3
                    },
                    bytes,
                    null);
            }

            offset += length + 2;
        }

        throw Corrupted("无法读取 JPEG 尺寸。");
    }

    private static PdfImage ParsePng(byte[] bytes)
    {
        var offset = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        var interlace = 0;
        var seenHeader = false;
        using var idat = new MemoryStream();

        while (offset + 12 <= bytes.Length)
        {
            var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (declaredLength > int.MaxValue)
            {
                throw Corrupted("PNG 块长度不正确。");
            }

            var length = (int)declaredLength;
            if (offset + 12 + length > bytes.Length)
            {
                throw Corrupted("PNG 块长度不正确。");
            }

            var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        throw Corrupted("PNG 头不完整。");
                    }

                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    seenHeader = true;
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    offset = bytes.Length;
                    break;
            }

            if (offset >= bytes.Length || type == "IEND")
            {
                break;
            }

            offset += length + 12;
        }

        if (!seenHeader)
        {
            throw Corrupted("PNG 缺少 IHDR。");
        }

        if (bitDepth != 8 || colorType is not (0 or 2 or 4 or 6) || interlace != 0)
        {
            throw new CoursewarePdfException(
                CoursewarePdfError.UnsupportedFormat,
                "仅支持 8 位、非隔行的灰度或 RGB PNG。");
        }

        EnsureSize(width, height);
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            _ => 4
        };
        var colors = channels <= 2 ? 1 : 3;

        var raw = Inflate(idat.ToArray());
        var stride = checked(width * channels);
        var expected = checked((long)(stride + 1) * height);
        if (raw.LongLength < expected)
        {
            throw Corrupted("PNG 像素数据不完整。");
        }

        var pixels = Unfilter(raw, width, height, channels);
        var image = new byte[checked(width * height * colors)];
        var alpha = channels is 2 or 4 ? new byte[checked(width * height)] : null;

        var source = 0;
        var target = 0;
        var mask = 0;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            for (var channel = 0; channel < colors; channel++)
            {
                image[target++] = pixels[source + channel];
            }

            if (alpha is not null)
            {
                alpha[mask++] = pixels[source + channels - 1];
            }

            source += channels;
        }

        return new PdfImage(
            PdfImageKind.Png,
            width,
            height,
            colors,
            Deflate(image),
            alpha is null ? null : Deflate(alpha));
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var output = new byte[checked(stride * height)];
        var source = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = raw[source++];
            var row = y * stride;
            var prior = row - stride;
            for (var x = 0; x < stride; x++)
            {
                var value = raw[source++];
                var left = x >= channels ? output[row + x - channels] : 0;
                var up = y > 0 ? output[prior + x] : 0;
                var upperLeft = y > 0 && x >= channels ? output[prior + x - channels] : 0;
                output[row + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) >> 1)),
                    4 => (byte)(value + Paeth(left, up, upperLeft)),
                    _ => throw Corrupted($"不支持的 PNG 过滤器：{filter}。")
                };
            }
        }

        return output;
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpperLeft = Math.Abs(estimate - upperLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpperLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpperLeft ? up : upperLeft;
    }

    private static byte[] WritePdf(IReadOnlyList<PdfImage> images)
    {
        var objects = new Dictionary<int, byte[]>();
        var pageIds = new List<int>();
        var nextId = 3;

        foreach (var image in images)
        {
            var pageId = nextId++;
            var contentId = nextId++;
            var imageId = nextId++;
            var alphaId = image.SoftMask is not null ? nextId++ : 0;
            pageIds.Add(pageId);

            var imageName = $"/Im{pageIds.Count}";
            var colorSpace = image.Colors switch
            {
                1 => "/DeviceGray",
                4 => "/DeviceCMYK",
                _ => "/DeviceRGB"
            };
            var filter = image.Kind == PdfImageKind.Jpeg ? "/DCTDecode" : "/FlateDecode";
            var alphaReference = alphaId != 0 ? $" /SMask {alphaId} 0 R" : string.Empty;

            objects[imageId] = Stream(
                $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} " +
                $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter {filter}{alphaReference}",
                image.Data);

            if (alphaId != 0)
            {
                objects[alphaId] = Stream(
                    $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                    image.SoftMask!);
            }

            var pageHeight = PageHeight(image);
            var content = Encoding.ASCII.GetBytes(
                $"q\n{PageWidthPoints} 0 0 {pageHeight} 0 0 cm\n{imageName} Do\nQ\n");
            objects[contentId] = Stream(string.Empty, content);
            objects[pageId] = Encoding.ASCII.GetBytes(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidthPoints} {pageHeight}] " +
                $"/Resources << /XObject << {imageName} {imageId} 0 R >> >> /Contents {contentId} 0 R >>");
        }

        objects[1] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[2] = Encoding.ASCII.GetBytes(
            $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] /Count {pageIds.Count} >>");

        var maxId = nextId - 1;
        using var output = new MemoryStream();
        output.Write(new byte[]
        {
            0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A,
            0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A
        });
        var offsets = new long[maxId + 1];

        for (var id = 1; id <= maxId; id++)
        {
            offsets[id] = output.Position;
            Write(output, $"{id} 0 obj\n");
            output.Write(objects[id]);
            Write(output, "\nendobj\n");
        }

        var xrefOffset = output.Position;
        Write(output, $"xref\n0 {maxId + 1}\n");
        Write(output, "0000000000 65535 f \n");
        for (var id = 1; id <= maxId; id++)
        {
            Write(output, $"{offsets[id]:D10} 00000 n \n");
        }

        Write(output, $"trailer\n<< /Size {maxId + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static int PageHeight(PdfImage image)
    {
        var height = (int)Math.Round(PageWidthPoints * (double)image.Height / image.Width);
        return Math.Clamp(height, 1, MaxPageHeightPoints);
    }

    private static byte[] Stream(string dictionary, byte[] data)
    {
        using var output = new MemoryStream();
        Write(output, $"<< {dictionary} /Length {data.Length} >>\nstream\n");
        output.Write(data);
        Write(output, "\nendstream");
        return output.ToArray();
    }

    private static void Write(Stream stream, string text) =>
        stream.Write(Encoding.ASCII.GetBytes(text));

    private static bool IsPng(byte[] bytes) =>
        bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
        bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static void EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw Corrupted("图片尺寸无效。");
        }

        if (width > 20000 || height > 20000 || (long)width * height > MaxPixelsPerImage)
        {
            throw new CoursewarePdfException(
                CoursewarePdfError.ImageTooLarge,
                "单张课件图片尺寸过大，已停止合成。");
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflater.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw Corrupted("PNG 像素数据无法解压。", exception);
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflater = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(data);
        }

        return output.ToArray();
    }

    private static CoursewarePdfException Corrupted(string message, Exception? inner = null) =>
        new(CoursewarePdfError.CorruptedImage, message, inner);

    private enum PdfImageKind
    {
        Jpeg,
        Png
    }

    private sealed record PdfImage(
        PdfImageKind Kind,
        int Width,
        int Height,
        int Colors,
        byte[] Data,
        byte[]? SoftMask);
}
