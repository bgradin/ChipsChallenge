using System.Runtime.InteropServices;
using Translator.IR;
using Win16_Runtime;

namespace Translator.CodeGen;

/// <summary>Provides Win16 memory and far-service calls to the generated GrowArray body.</summary>
public sealed class GrowArrayContext
{
    public GrowArrayContext(Win16CallDispatcher dispatcher) => Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    public Win16CallDispatcher Dispatcher
    {
        get;
    }
}

/// <summary>Native AMD64 body entry; returns RETF cleanup bytes for the five word arguments.</summary>
public delegate ushort NativeGrowArray(GrowArrayContext context, ref MachineState state, SegmentMemory memory);
/// <summary>Lowers GrowArray instruction IR into independently executable AMD64.</summary>
public static class GrowArrayCodeGenerator
{
    public const string ReadWordHelperSymbol = Win16HelperSymbols.ReadWordHelperSymbol;
    public const string WriteWordHelperSymbol = Win16HelperSymbols.WriteWordHelperSymbol;
    public const string CallServiceHelperSymbol = Win16HelperSymbols.CallServiceHelperSymbol;

    public static NativeGrowArray Generate(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("GrowArray native lowering currently targets AMD64 hosts.");
        }

        if (!function.Name.Equals("GrowArray", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected GrowArray.", nameof(function));
        }

        var body = new NativeWin16Body(GenerateArtifact(function, X64Abi.Windows));
        return (GrowArrayContext context, ref MachineState state, SegmentMemory memory) => body.Invoke(ref state, memory, context.Dispatcher);
    }

    /// <summary>
    /// Emits the GrowArray algorithm as AMD64. Its PE imports use Win64 ABI helper signatures:
    /// read(ctx,seg,off), write(ctx,seg,off,value), and call_service(ctx,state,id).
    /// </summary>
    public static MachineCodeArtifact GenerateArtifact(IrFunction function, X64Abi abi)
    {
        ArgumentNullException.ThrowIfNull(function);
        if (!function.Name.Equals("GrowArray", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected GrowArray.", nameof(function));
        }

        if (abi != X64Abi.Windows)
        {
            throw new NotSupportedException("Relocatable GrowArray artifacts currently use the Windows AMD64 ABI.");
        }

        return new Win16BodyCodeGenerator().Generate(function, abi);
    }
}
