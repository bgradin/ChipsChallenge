using Translator.Assembly;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class CallbackIntegrationTests
{
    private static IrModule Fixtures() => new AssemblyParser().Parse(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm")).Module;

    private static (SegmentMemory, Win16CallDispatcher, Win16CallbackMachine) EnvironmentForCallback()
    {
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        var calls = new Win16CallDispatcher();
        var state = new MachineState
        {
            AX = 7,
            BX = 8,
            CX = 9,
            DX = 10,
            SI = 11,
            DI = 12,
            BP = 13,
            SP = 0x8000,
            IP = 0x50,
            CS = 0x4000,
            DS = 0x1000,
            ES = 0x1000,
            SS = 0x2000,
            Flags = 0xC01,
            AXHigh = 0x1234,
            SPHigh = 0x5678
        };
        return (memory, calls, new(memory, calls, state));
    }

    private static NativeWin16Body Register(IrFunction function, Win16CallDispatcher calls, SegmentMemory memory, Win16CodeAddress target)
    {
        var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function));
        body.Register(calls, memory, function.Name, target, CallDistance.Far);
        return body;
    }

    [Fact]
    public void CallbackRestoresEveryRegisterAndSegmentAndKeepsApplicationData()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        int segments = memory.Segments.Count;
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "VALUEWNDPROC"), calls, memory, new(0x4000, 0x200));
        using var handles = new Win16WindowHandles();
        ushort id = handles.Register(0x123);
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        Assert.Equal((nint)unchecked((int)0x81235678), callback.InvokeWindow(0x123, 0x9001, 0xBEEF, -123));
        Assert.Null(callback.LastError);
        Assert.Equal(saved, machine.State);
        Assert.Equal(segments, memory.Segments.Count);
        Assert.Equal(0, machine.Depth);
        Assert.Equal(id, memory.ReadWord(0x1000, 0x110));
        Assert.Equal((ushort)0x9001, memory.ReadWord(0x1000, 0x112));
        Assert.Equal((ushort)0xBEEF, memory.ReadWord(0x1000, 0x114));
        Assert.Equal(unchecked((uint)-123), memory.ReadDword(0x1000, 0x116));
        Assert.Equal((nint)0, callback.InvokeWindow(0x123, 15, 0, 0));
        Assert.IsType<NotSupportedException>(callback.LastError);
        Assert.Equal(saved, machine.State);
        callback.Dispose();
        Assert.Throws<ObjectDisposedException>(() => callback.NativePointer);
        Assert.Equal((nint)0, callback.InvokeWindow(0x123, 0x9001, 0, 0));
        Assert.IsType<ObjectDisposedException>(callback.LastError);
    }

    [Fact]
    public void InvalidTargetsSignaturesHandlesAndWidthConversionsAreControlled()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        using var handles = new Win16WindowHandles();
        Assert.Throws<ArgumentException>(() => new Win16CallbackBinding(machine, handles, new(new(0x9999, 0), Win16CallbackSignature.EnumWindows)));
        calls.RegisterRuntimeService("Service", new(0xF000, 0x100), CallDistance.Far, (ref MachineState _, SegmentMemory _, Win16CallDispatcher _, Win16CallContext _) => 0);
        Assert.Throws<ArgumentException>(() => new Win16CallbackBinding(machine, handles, new(new(0xF000, 0x100), Win16CallbackSignature.EnumWindows)));
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "VALUEWNDPROC"), calls, memory, new(0x4000, 0x200));
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        Assert.Equal((nint)0, callback.InvokeWindow(0x123, 0x9001, 0, 0));
        Assert.IsType<KeyNotFoundException>(callback.LastError);
        handles.Register(0x123);
        Assert.Equal((nint)0, callback.InvokeWindow(0x123, 0x9001, 0x10000, 0));
        Assert.IsType<NotSupportedException>(callback.LastError);
    }

    [Fact]
    public void NestedCallbacksHavePrivateStacksAndRestoreTheOuterInvocation()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var handles = new Win16WindowHandles();
        handles.Register(0x123);
        var function = new AssemblyParser().ParseText("func_nods Nested\n%assign %$argsize 10\n%arg value:dword,w:word,m:word,h:word\ncall far Reenter\nmov ax,[value]\nmov [0x120],ax\nmov dx,0\nendfunc").Module.Functions.Single();
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(function, targets: new Dictionary<string, Win16CodeAddress> { ["Reenter"] = new(0xF200, 0x10) }));
        body.Register(calls, memory, "Nested", new(0x4000, 0x300), CallDistance.Far);
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x300), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        int deepest = 0;
        calls.RegisterRuntimeService("Reenter", new(0xF200, 0x10), CallDistance.Far, (ref MachineState state, SegmentMemory m, Win16CallDispatcher d, Win16CallContext c) =>
        {
            deepest = Math.Max(deepest, machine.Depth);
            if (machine.Depth == 1)
            {
                Assert.Equal((nint)0x2222, callback.InvokeWindow(0x123, 0x9001, 0, 0x2222));
                Assert.Null(callback.LastError);
            }

            return 0;
        });
        Assert.Equal((nint)0x1111, callback.InvokeWindow(0x123, 0x9001, 0, 0x1111));
        Assert.Null(callback.LastError);
        Assert.Equal(2, deepest);
        Assert.Equal(saved, machine.State);
        Assert.Equal((ushort)0x1111, memory.ReadWord(0x1000, 0x120));
        Assert.Equal(2, memory.Segments.Count);
    }

    [Fact]
    public void CallbackDepthLimitRestoresTheOuterScope()
    {
        var (memory, calls, original) = EnvironmentForCallback();
        var machine = new Win16CallbackMachine(memory, calls, original.State, maxDepth: 1);
        var reference = new Win16CallbackReference(new(0x4000, 0x500), Win16CallbackSignature.EnumWindows);
        calls.Register("DepthProbe", reference.Target, CallDistance.Far, (ref MachineState state, SegmentMemory m, Win16CallDispatcher d, Win16CallContext c) =>
        {
            Assert.Throws<InvalidOperationException>(() => machine.Invoke(reference, new ushort[] { 0, 0, 1 }));
            state.AX = 1;
            return 6;
        });
        Assert.Equal(1u, machine.Invoke(reference, new ushort[] { 0, 0, 1 }));
        Assert.Equal(original.State, machine.State);
        Assert.Equal(0, machine.Depth);
        Assert.Equal(2, memory.Segments.Count);
    }

    [WindowsFact]
    public void WindowsEnumerationEntersGeneratedCallbackAndReturnsBool()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var parent = new WindowsCallbackTestWindow();
        using var child = new WindowsCallbackTestWindow(parent.Handle);
        using var child2 = new WindowsCallbackTestWindow(parent.Handle);
        using var handles = new Win16WindowHandles();
        ushort id = handles.Register(child.Handle), id2 = handles.Register(child2.Handle);
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "ENUMCALLBACK"), calls, memory, new(0x4000, 0x100));
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x100), Win16CallbackSignature.EnumWindows));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        WindowsCallbackHost.EnumerateChildren(parent.Handle, callback, 0x12345678);
        Assert.Null(callback.LastError);
        Assert.Equal(2, callback.InvocationCount);
        Assert.Equal((ushort)2, memory.ReadWord(0x1000, 0x106));
        Assert.Contains(memory.ReadWord(0x1000, 0x100), new[] { id, id2 });
        Assert.Equal(0x12345678u, memory.ReadDword(0x1000, 0x102));
        Assert.Equal(saved, machine.State);
    }

    [WindowsFact]
    public void FalseEnumReturnStopsNativeEnumeration()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        using var parent = new WindowsCallbackTestWindow();
        using var first = new WindowsCallbackTestWindow(parent.Handle);
        using var second = new WindowsCallbackTestWindow(parent.Handle);
        using var handles = new Win16WindowHandles();
        handles.Register(first.Handle);
        handles.Register(second.Handle);
        var fn = Fixtures().Functions.Single(f => f.Name == "ENUMCALLBACK");
        var code = fn.Instructions.ToArray();
        int at = Array.FindLastIndex(code, i => i.Operation == "MOV" && i.Operands[0].Register == X86Register.AX);
        code[at] = code[at] with
        {
            Operands = new[]
            {
                code[at].Operands[0],
                new IrOperand("Immediate", "0", ImmediateValue: 0)
            }
        };
        using var body = Register(fn with
        {
            Instructions = code
        }, calls, memory, new(0x4000, 0x100));
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x100), Win16CallbackSignature.EnumWindows));
        WindowsCallbackHost.EnumerateChildren(parent.Handle, callback, 1);
        Assert.Null(callback.LastError);
        Assert.Equal(1, callback.InvocationCount);
        Assert.Equal((ushort)1, memory.ReadWord(0x1000, 0x106));
    }

    [Fact]
    public void WrongValidTargetAndDisposedCodeFailWithoutLeakingCallbackState()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var handles = new Win16WindowHandles();
        handles.Register(0x123);
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "VALUEWNDPROC"), calls, memory, new(0x4000, 0x200));
        using var wrong = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.EnumWindows));
        Assert.Equal(0, wrong.InvokeEnum(0x123, 1));
        Assert.IsType<InvalidOperationException>(wrong.LastError);
        Assert.Equal(saved, machine.State);
        Assert.Equal(2, memory.Segments.Count);
        body.Dispose();
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        Assert.Equal((nint)0, callback.InvokeWindow(0x123, 0x9001, 0, 0));
        Assert.NotNull(callback.LastError);
        Assert.Equal(saved, machine.State);
        Assert.Equal(2, memory.Segments.Count);
    }

    [Fact]
    public void CrossThreadEntryIsRejectedBeforeTranslatedCodeRuns()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var handles = new Win16WindowHandles();
        handles.Register(0x123);
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "VALUEWNDPROC"), calls, memory, new(0x4000, 0x200));
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        nint result = -1;
        var thread = new Thread(() => result = callback.InvokeWindow(0x123, 0x9001, 0, 0))
        {
            IsBackground = true
        };
        thread.Start();
        Assert.True(thread.Join(5000));
        Assert.Equal((nint)0, result);
        Assert.IsType<NotSupportedException>(callback.LastError);
        Assert.Equal(saved, machine.State);
    }

    [WindowsFact]
    public void WindowsWindowProcedureCallConvertsTheSignedDwordReturn()
    {
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var window = new WindowsCallbackTestWindow();
        using var handles = new Win16WindowHandles();
        handles.Register(window.Handle);
        using var body = Register(Fixtures().Functions.Single(f => f.Name == "VALUEWNDPROC"), calls, memory, new(0x4000, 0x200));
        using var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 });
        Assert.Equal((nint)unchecked((int)0x81235678), WindowsCallbackHost.CallWindowProcedure(callback, window.Handle, 0x9001, 0xBEEF, -123));
        Assert.Null(callback.LastError);
        Assert.Equal(saved, machine.State);
    }

    [WindowsFact]
    public void SyntheticProcedureDefaultPathRunsThroughWindowsCallback()
    {
        var parsed = new AssemblyParser().ParseSelected(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm"), new[] { "SAMPLEWNDPROC" });
        Assert.Empty(parsed.Diagnostics);
        var original = CounterCallbackProfile.Configure(Assert.Single(parsed.Module.Functions));
        var (memory, calls, machine) = EnvironmentForCallback();
        var saved = machine.State;
        using var window = new WindowsCallbackTestWindow();
        using var handles = new Win16WindowHandles();
        handles.Register(window.Handle);
        _ = new Win16UserServices(calls, handles);
        using var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(original, symbols: CounterCallbackProfile.Symbols, targets: CounterCallbackProfile.Targets));
        body.Register(calls, memory, original.Name, CounterCallbackProfile.Entry, CallDistance.Far);
        using var callback = new Win16CallbackBinding(machine, handles, new(CounterCallbackProfile.Entry, Win16CallbackSignature.WindowProcedure), new uint[] { 0, 0x87 });
        nint expected = WindowsCallbackHost.DefaultWindowProcedure(window.Handle, 0x87, 0, 0);
        Assert.Equal(expected, WindowsCallbackHost.CallWindowProcedure(callback, window.Handle, 0x87, 0, 0));
        Assert.Null(callback.LastError);
        Assert.Equal(saved, machine.State);
        Assert.Equal((nint)0, callback.InvokeWindow(window.Handle, 0x87, 0, 1));
        Assert.IsType<NotSupportedException>(Assert.IsType<InvalidOperationException>(callback.LastError).InnerException);
        Assert.Equal(saved, machine.State);
        Assert.Equal((nint)0, WindowsCallbackHost.CallWindowProcedure(callback, window.Handle, 15, 0, 0));
        Assert.IsType<NotSupportedException>(callback.LastError);
    }
}
