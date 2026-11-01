using Translator.IR;

namespace Win16_Runtime;

public enum Win16CallbackReturn
{
    Eax,
    Word,
    DxAx
}

public enum Win16CallbackStack
{
    Caller,
    DataSegment,
    SeparateSegment
}

/// <summary>Persistent memory services for one loaded task. No application state is fabricated.</summary>
public sealed class Win16TaskMemory : IDisposable
{
    private readonly SegmentMemory _memory;
    private readonly ushort _dataSelector;
    private bool _disposed;
    public Win16LocalHeap Local
    {
        get;
    }
    public Win16GlobalMemoryServices Global
    {
        get;
    }

    public Win16TaskMemory(NeLoadedProgram image, Win16CallDispatcher dispatcher)
    {
        _memory = image.Memory;
        _dataSelector = image.InitialState.DS;
        var initial = image.Report.InitialRegisters;
        if (_dataSelector == 0 || initial.HeapOffset is null)
        {
            throw new NotSupportedException("Task has no automatic data/local heap segment.");
        }

        Local = new(_memory, _dataSelector, initial.HeapOffset.Value, initial.HeapSize);
        Global = new(_memory, dispatcher, wholeProgram: true);
    }

    public void GrowDataSegment(int bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes < _memory.GetSegment(_dataSelector).Size)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        _memory.ResizeDataSegment(_dataSelector, bytes);
        Local.ExtendTo(bytes);
    }

    /// <summary>Reverse entry saves the complete suspended CPU state. Guest memory remains persistent.
    /// SS=DS is selected explicitly for routines which use stack-local near pointers through DS.</summary>
    public uint Callback(Win16ProgramControl control, ref MachineState suspended, Win16CodeAddress target, ReadOnlySpan<ushort> arguments = default, Win16CallbackStack stack = Win16CallbackStack.Caller, int stackBytes = 1024, ushort separateSelector = 0, ushort separateTop = 0, Win16CallbackReturn returnKind = Win16CallbackReturn.Eax)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MachineState saved = suspended, callback = suspended;
        ushort lease = 0;
        try
        {
            switch (stack)
            {
                case Win16CallbackStack.Caller:
                    break;
                case Win16CallbackStack.DataSegment:
                    if (stackBytes < 32 || stackBytes > 65530 || (stackBytes & 1) != 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(stackBytes));
                    }

                    lease = Local.Allocate((uint)(stackBytes + 4), 0x40);
                    if (lease == 0)
                    {
                        throw new InvalidOperationException("Local heap has no callback stack space.");
                    }

                    _memory.WriteWord(_dataSelector, lease, 0xa55a);
                    _memory.WriteWord(_dataSelector, (ushort)(lease + stackBytes + 2), 0x5aa5);
                    callback.DS = callback.SS = _dataSelector;
                    callback.SP = (ushort)(lease + stackBytes + 2);
                    break;
                case Win16CallbackStack.SeparateSegment:
                    if (separateSelector == 0 || _memory.SharesOrigin(_dataSelector, separateSelector))
                    {
                        throw new ArgumentException("A separate callback stack requires distinct storage.");
                    }

                    callback.SS = separateSelector;
                    callback.SP = separateTop;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stack));
            }

            ushort expectedSs = callback.SS, expectedSp = callback.SP;
            control.Invoke(ref callback, target, CallDistance.Far, arguments);
            if (returnKind != Win16CallbackReturn.Eax && (callback.SS != expectedSs || callback.SP != expectedSp))
            {
                throw new InvalidOperationException("Callback did not clean its declared Pascal arguments.");
            }

            if (lease != 0 && (_memory.ReadWord(_dataSelector, lease) != 0xa55a || _memory.ReadWord(_dataSelector, (ushort)(lease + stackBytes + 2)) != 0x5aa5))
            {
                throw new InvalidOperationException("Callback exceeded its local stack reservation.");
            }

            return returnKind switch
            {
                Win16CallbackReturn.Word => callback.AX,
                Win16CallbackReturn.DxAx => (uint)callback.DX << 16 | callback.AX,
                _ => callback.EAX
            };
        }
        finally
        {
            suspended = saved;
            if (lease != 0)
            {
                Local.Free(lease);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Global.Dispose();
        _disposed = true;
    }
}
