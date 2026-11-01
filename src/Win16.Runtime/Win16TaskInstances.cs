namespace Win16_Runtime;

/// <summary>Original FARPROC values refer only to approved compiled entries or
/// instance thunks with captured original selectors, never host callbacks.</summary>
public sealed class Win16TaskInstances(Win16ProgramControl control, SegmentMemory memory) : IDisposable
{
    private bool _disposed;
    public static IReadOnlySet<string> Implemented
    {
        get;
    } = new HashSet<string>
    {
        "KERNEL.#51",
        "KERNEL.#52"
    };

    public ushort Invoke(string identity, ref MachineState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Implemented.Contains(identity))
        {
            throw new NotSupportedException(identity);
        }

        var contract = Win16ExportContracts.Get(identity);
        uint value = contract.Argument(0, memory, state);
        var pointer = new Win16CodeAddress((ushort)(value >> 16), (ushort)value);
        if (identity == "KERNEL.#51")
        {
            Win16CodeAddress thunk = default;
            if (value != 0)
            {
                try
                {
                    thunk = control.CreateInstanceThunk(pointer, (ushort)contract.Argument(1, memory, state));
                }
                catch (InvalidOperationException)
                {
                    thunk = default;
                }
            }

            state.AX = thunk.Offset;
            state.DX = thunk.Segment;
        }
        else
        {
            control.FreeInstanceThunk(pointer);
        }

        return contract.CleanupBytes;
    }

    public void Dispose() => _disposed = true;
}
