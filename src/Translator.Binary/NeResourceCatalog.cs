using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Translator.Binary;

public sealed record NeExtractedResource(string ExecutableSha256, int Index, NeResource Metadata, string PayloadSha256, [property: JsonIgnore] byte[] Bytes);
public sealed record NeResourceInspection(int Index, string TypeName, NeResource Metadata, string PayloadSha256, string DecodeStatus, object? Decoded, string? Diagnostic);
public sealed record NeResourceReport(string ExecutableSha256, IReadOnlyList<NeResourceInspection> Resources);
public sealed record NeStringEntry(int Id, string Value);
public sealed record NeStringTable(int BlockId, string Encoding, IReadOnlyList<NeStringEntry> Strings);
public sealed record NeIconGroupEntry(int Width, int Height, byte ColorCount, ushort Planes, ushort BitCount, uint ImageBytes, ushort ImageResourceId);
public sealed record NeIconGroup(IReadOnlyList<NeIconGroupEntry> Images);
/// <summary>Owns an immutable snapshot of the user-supplied executable. Extraction never loses source identity.</summary>
public sealed class NeResourceCatalog : IDisposable
{
    private readonly byte[] _image;
    private readonly NeExecutable _executable;
    private readonly IReadOnlyList<NeResource> _resources;
    private bool _disposed;
    public string ExecutableSha256 => _executable.Sha256;
    public IReadOnlyList<NeResource> Resources => _resources;

    public NeResourceCatalog(ReadOnlySpan<byte> bytes)
    {
        _image = bytes.ToArray();
        _executable = new NeExecutableReader().Read(_image);
        _resources = Array.AsReadOnly(_executable.Resources.ToArray());
    }

    public NeExtractedResource Extract(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 1 || index > Resources.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Resource indices are one-based table order.");
        }

        var resource = Resources[index - 1];
        if (resource.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Resource exceeds extraction limit.");
        }

        ResourceBounds(_image.Length, resource.FileOffset, resource.Length);
        byte[] bytes = _image.AsSpan(resource.FileOffset, resource.Length).ToArray();
        return new(_executable.Sha256, index, resource, Hash(bytes), bytes);
    }

    public NeResourceInspection Inspect(int index)
    {
        var raw = Extract(index);
        try
        {
            object decoded = raw.Metadata.Type.Id switch
            {
                2 => NeBitmapDecoder.Decode(raw.Bytes),
                6 => Strings(raw.Bytes, raw.Metadata.Identity.Id),
                14 => IconGroup(raw.Bytes),
                _ => throw new NotSupportedException("Raw extraction is supported; decoding this resource type is deferred.")
            };
            return new(index, TypeName(raw.Metadata.Type), raw.Metadata, raw.PayloadSha256, "Decoded", decoded, null);
        }
        catch (NotSupportedException error)
        {
            return new(index, TypeName(raw.Metadata.Type), raw.Metadata, raw.PayloadSha256, "Unsupported", null, error.Message);
        }
        catch (InvalidDataException error)
        {
            return new(index, TypeName(raw.Metadata.Type), raw.Metadata, raw.PayloadSha256, "Invalid", null, error.Message);
        }
    }

    public NeResourceReport InspectAll() => new(ExecutableSha256, Enumerable.Range(1, Resources.Count).Select(Inspect).ToArray());

    private static NeStringTable Strings(byte[] data, int? blockId)
    {
        if (blockId is null or < 1)
        {
            throw new InvalidDataException("ANSI string tables require a positive ordinal block ID.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var entries = new List<NeStringEntry>();
        int p = 0;
        for (int i = 0; i < 16; i++)
        {
            ResourceBounds(data.Length, p, 1);
            int length = data[p++];
            ResourceBounds(data.Length, p, length);
            entries.Add(new((blockId.Value - 1) * 16 + i, ansi.GetString(data, p, length)));
            p += length;
        }

        return new(blockId.Value, "Windows-1252, Win16 byte-length entries", entries.AsReadOnly());
    }

    private NeIconGroup IconGroup(byte[] data)
    {
        ResourceBounds(data.Length, 0, 6);

        ushort W(int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));

        if (W(0) != 0 || W(2) != 1)
        {
            throw new InvalidDataException("Invalid group-icon header.");
        }

        int count = W(4);
        if (count is < 1 or > 1024)
        {
            throw new InvalidDataException("Invalid group-icon count.");
        }

        ResourceBounds(data.Length, 6, count * 14);
        var entries = new List<NeIconGroupEntry>();
        for (int i = 0; i < count; i++)
        {
            int p = 6 + i * 14;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8));
            ushort id = W(p + 12);
            var target = Resources.FirstOrDefault(r => r.Type.Id == 3 && r.Identity.Id == id);
            if (target is null || size == 0 || size > target.Length)
            {
                throw new InvalidDataException($"Group icon references invalid image resource #{id}.");
            }

            entries.Add(new(data[p] == 0
                ? 256
                : data[p], data[p + 1] == 0
                ? 256
                : data[p + 1], data[p + 2], W(p + 4), W(p + 6), size, id));
        }

        return new(entries.AsReadOnly());
    }

    public static string TypeName(NeResourceIdentity type) => type.Id switch
    {
        1 => "Cursor",
        2 => "Bitmap",
        3 => "Icon",
        4 => "Menu",
        5 => "Dialog",
        6 => "StringTable",
        9 => "Accelerator",
        10 => "RawData",
        12 => "GroupCursor",
        14 => "GroupIcon",
        16 => "Version",
        _ => type.Name ?? $"Type#{type.Id}"
    };

    internal static void ResourceBounds(int limit, int offset, long size)
    {
        if (offset < 0 || size < 0 || offset > limit || size > limit - offset)
        {
            throw new InvalidDataException($"Truncated or out-of-range resource payload at 0x{offset:X}, size {size}.");
        }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Array.Clear(_image);
    }
}
