using System.Buffers;
using System.Runtime.InteropServices;

namespace Win16_Runtime;

public interface IWin16MemoryAllocator
{
    string Name
    {
        get;
    }

    IMemoryOwner<byte> Allocate(int bytes);
}

public sealed record Win16NativeMemoryLimits(long MaximumBytes = 64 * 1024 * 1024, int MaximumAllocations = 32768);
public sealed record Win16MemoryStatistics(string Backend, long LiveBytes, int LiveAllocations, long PeakBytes);
/// <summary>Owned, zero-initialized native storage. Runtime access/disposal is serialized by its owner.</summary>
public sealed class Win16NativeMemoryAllocator : IWin16MemoryAllocator, IDisposable
{
    private readonly Win16NativeMemoryLimits _limits;
    private readonly HashSet<Block> _blocks = new();
    private readonly object _sync = new();
    private bool _disposed;
    private long _liveBytes, _peakBytes;
    public string Name => "Native";
    public bool FailNextAllocation
    {
        get; set;
    }
    public Win16NativeMemoryLimits Limits => _limits;

    public Win16MemoryStatistics Statistics
    {
        get
        {
            lock (_sync)
            {
                return new(Name, _liveBytes, _blocks.Count, _peakBytes);
            }
        }
    }

    public Win16NativeMemoryAllocator(Win16NativeMemoryLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MaximumBytes < 1 || _limits.MaximumAllocations < 1)
        {
            throw new ArgumentException("Native memory limits must be positive.");
        }
    }

    public IMemoryOwner<byte> Allocate(int bytes)
    {
        if (bytes < 1 || bytes > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool fail = FailNextAllocation;
            FailNextAllocation = false;
            if (fail || _blocks.Count >= _limits.MaximumAllocations || bytes > _limits.MaximumBytes - _liveBytes)
            {
                throw new OutOfMemoryException("Win16 native storage allocation rejected by configured limits or allocator failure.");
            }

            var block = new Block(this, bytes);
            try
            {
                _blocks.Add(block);
            }
            catch
            {
                block.Free();
                throw;
            }

            _liveBytes += bytes;
            _peakBytes = Math.Max(_peakBytes, _liveBytes);
            return block;
        }
    }

    private void Release(Block block)
    {
        lock (_sync)
        {
            if (!_blocks.Remove(block))
            {
                return;
            }

            _liveBytes -= block.Size;
            block.Free();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var block in _blocks)
            {
                block.Free();
            }

            _blocks.Clear();
            _liveBytes = 0;
        }
    }

    private sealed unsafe class Block : MemoryManager<byte>
    {
        private readonly Win16NativeMemoryAllocator _owner;
        private byte* _address;
        public int Size
        {
            get;
        }

        public Block(Win16NativeMemoryAllocator owner, int size)
        {
            _owner = owner;
            Size = size;
            _address = (byte*)NativeMemory.AllocZeroed((nuint)size);
            if (_address == null)
            {
                throw new OutOfMemoryException("Native allocator returned null.");
            }
        }

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_address == null, this);
            return new(_address, Size);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_address == null, this);
            if ((uint)elementIndex > (uint)Size)
            {
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            }

            return new(_address + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing) => _owner.Release(this);

        public void Free()
        {
            if (_address == null)
            {
                return;
            }

            new Span<byte>(_address, Size).Clear();
            NativeMemory.Free(_address);
            _address = null;
        }
    }
}
