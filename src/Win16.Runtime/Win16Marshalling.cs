using System.Buffers.Binary;
using System.Text;

namespace Win16_Runtime;

public readonly record struct Win16Point(short X, short Y);
public readonly record struct Win16Rect(short Left, short Top, short Right, short Bottom);
/// <summary>API buffers are bounded, contiguous ranges; CPU accesses retain their separate 16-bit wrap semantics.</summary>
public sealed class Win16Marshaller
{
    private readonly SegmentMemory _memory;
    private static readonly Encoding _ansi = CreateEncoding();

    public Win16Marshaller(SegmentMemory memory) => _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static sbyte SignedByte(uint value) => unchecked((sbyte)value);

    public static short SignedWord(uint value) => unchecked((short)value);

    public static int SignedDword(uint value) => unchecked((int)value);

    public static bool Bool(ushort value) => value != 0;
    public static ushort Bool(bool value) => value
        ? (ushort)1
        : (ushort)0;

    public static void WriteDwordReturn(ref MachineState state, uint value)
    {
        state.AX = (ushort)value;
        state.DX = (ushort)(value >> 16);
    }

    public byte[] CopyInput(FarPointer pointer, int length)
    {
        if (pointer.IsNull && length == 0)
        {
            return Array.Empty<byte>();
        }

        Validate(pointer, length, false);
        return _memory.ReadBytes(pointer.Segment, pointer.Offset, length);
    }

    public byte[] CopyNearInput(in MachineState state, ushort offset, int length) => CopyInput(offset == 0
        ? default
        : new(offset, state.DS), length);

    public void CopyOutput(FarPointer pointer, ReadOnlySpan<byte> data)
    {
        if (pointer.IsNull && data.Length == 0)
        {
            return;
        }

        Validate(pointer, data.Length, true);
        _memory.WriteBytes(pointer.Segment, pointer.Offset, data);
    }

    private void Validate(FarPointer pointer, int length, bool write)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (pointer.IsNull)
        {
            throw new ArgumentException("Null Win16 API buffer.", nameof(pointer));
        }

        var segment = _memory.GetSegment(pointer.Segment);
        if ((write && !segment.CanWrite) || (!write && !segment.CanRead))
        {
            throw new InvalidOperationException("Win16 API buffer permission violation.");
        }

        if ((long)pointer.Offset + length > segment.Size)
        {
            throw new ArgumentException("Win16 API buffer exceeds its segment; API ranges cannot wrap.");
        }
    }

    public string ReadAnsi(FarPointer pointer, int maxBytes = 4096)
    {
        if (maxBytes <= 0 || maxBytes > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        Validate(pointer, 0, false);
        int bound = Math.Min(maxBytes, _memory.GetSegment(pointer.Segment).Size - pointer.Offset);
        var bytes = new List<byte>();
        for (int i = 0; i < bound; i++)
        {
            byte value = _memory.ReadByte(pointer.Segment, (ushort)(pointer.Offset + i));
            if (value == 0)
            {
                return _ansi.GetString(bytes.ToArray());
            }

            bytes.Add(value);
        }

        throw new InvalidDataException("Win16 ANSI string has no terminator inside the validated bound.");
    }

    public void WriteAnsi(FarPointer pointer, string value, int capacity)
    {
        byte[] bytes = _ansi.GetBytes(value);
        if (bytes.Length + 1 > capacity)
        {
            throw new ArgumentException("ANSI output buffer is too small.");
        }

        Validate(pointer, capacity, true);
        byte[] output = new byte[bytes.Length + 1];
        bytes.CopyTo(output, 0);
        CopyOutput(pointer, output);
    }

    public Win16Point ReadPoint(FarPointer pointer)
    {
        var buffer = CopyInput(pointer, 4);
        return new(BinaryPrimitives.ReadInt16LittleEndian(buffer), BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(2)));
    }

    public void WritePoint(FarPointer pointer, Win16Point value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value.X);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2), value.Y);
        CopyOutput(pointer, buffer);
    }

    public Win16Rect ReadRect(FarPointer pointer)
    {
        var buffer = CopyInput(pointer, 8);
        return new(BinaryPrimitives.ReadInt16LittleEndian(buffer), BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(2)), BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(4)), BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(6)));
    }

    public void WriteRect(FarPointer pointer, Win16Rect value)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, value.Left);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2), value.Top);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(4), value.Right);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(6), value.Bottom);
        CopyOutput(pointer, buffer);
    }
}

/// <summary>Per-type handle ownership; a Win16 ID never contains the native handle value.</summary>
public sealed class Win16HandleTable<T> : IDisposable where T : notnull
{
    private readonly Dictionary<ushort, T> _values = new();
    private readonly Action<T>? _release;
    private int _next = 1;
    private bool _disposed;

    public Win16HandleTable(Action<T>? release = null) => _release = release;

    public ushort Add(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (_next > ushort.MaxValue)
        {
            throw new InvalidOperationException("Win16 handle namespace exhausted.");
        }

        ushort id = (ushort)_next++;
        _values.Add(id, value);
        return id;
    }

    public T Get(ushort id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _values.TryGetValue(id, out var value)
            ? value
            : throw new KeyNotFoundException($"Invalid Win16 {typeof(T).Name} handle 0x{id:X4}.");
    }

    public bool Remove(ushort id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_values.Remove(id, out var value))
        {
            return false;
        }

        _release?.Invoke(value);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Exception>? failures = null;
        foreach (var value in _values.Values)
        {
            try
            {
                _release?.Invoke(value);
            }
            catch (Exception error)
            {
                // Attempt every release before reporting cleanup failures.
                (failures ??= new()).Add(error);
            }
        }

        _values.Clear();
        if (failures is not null)
        {
            throw new AggregateException("Failed to release Win16 handles.", failures);
        }
    }
}
