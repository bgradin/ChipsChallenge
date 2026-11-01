using System.Text;

namespace Win16_Runtime;

/// <summary>Explicit Win3.1 compatibility environment, independent of host x64 ABI/version.
/// Paths are supplied by the task owner; callers still make all application decisions.</summary>
public sealed class Win16TaskSystem(SegmentMemory memory, ushort instance, string modulePath, string windowsDirectory, ushort windowsVersion = 0x0a03, ushort dosVersion = 0x0500) : IDisposable
{
    private bool _disposed;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#3",
        "KERNEL.#49",
        "KERNEL.#134"
    };

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var contract = Win16ExportContracts.Get(identity);
        var saved = state;

        uint Arg(int n) => contract.Argument(n, memory, saved);

        uint result = 0;
        if (identity == "KERNEL.#3")
        {
            result = (uint)dosVersion << 16 | windowsVersion;
        }
        else
        {
            bool module = identity == "KERNEL.#49";
            uint target = Arg(module
                ? 1
                : 0);
            int capacity = module
                ? unchecked((short)Arg(2))
                : (ushort)Arg(1);
            if (!module || Arg(0) == 0 || Arg(0) == instance)
            {
                byte[] text = Encoding.Latin1.GetBytes(module
                    ? modulePath
                    : windowsDirectory);
                if (text.Length >= 65535)
                {
                    throw new NotSupportedException("Environment path exceeds original-width API length.");
                }

                if (!module && capacity <= text.Length)
                {
                    result = (uint)text.Length + 1;
                }
                else if (capacity > 0)
                {
                    memory.ValidateBufferWrite((ushort)(target >> 16), (ushort)target, capacity);
                    int copied = Math.Min(text.Length, capacity - 1);
                    byte[] bytes = new byte[copied + 1];
                    text.AsSpan(0, copied).CopyTo(bytes);
                    Win16TaskKernelServices.WriteBuffer(memory, target, bytes);
                    result = (uint)copied;
                }
            }
        }

        state.AX = (ushort)result;
        if (contract.ReturnBytes == 4)
        {
            state.DX = (ushort)(result >> 16);
        }

        return contract.CleanupBytes;
    }

    public void Dispose() => _disposed = true;
}
