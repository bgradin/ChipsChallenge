using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

public static class CallbackAcceptanceHarness
{
    public static void Run(IrModule fixtures, IrModule? original)
    {
        var memory = new SegmentMemory();
        memory.Map(new(0x1000));
        memory.Map(new(0x2000));
        var calls = new Win16CallDispatcher();
        var initial = new MachineState
        {
            DS = 0x1000,
            SS = 0x2000,
            ES = 0x1000,
            CS = 0x4000,
            SP = 0x8000,
            BP = 0x1234,
            Flags = 0xC01,
            AXHigh = 0xABCD
        };
        var machine = new Win16CallbackMachine(memory, calls, initial);
        using var parent = new WindowsCallbackTestWindow();
        using var child = new WindowsCallbackTestWindow(parent.Handle);
        using var handles = new Win16WindowHandles();
        ushort childId = handles.Register(child.Handle);
        handles.Register(parent.Handle);
        var bodies = new List<NativeWin16Body>();

        void Check(bool success, string message)
        {
            if (!success)
            {
                throw new InvalidDataException("Callback acceptance failed: " + message);
            }
        }


        try
        {
            foreach (var (name, offset) in new[]
            {
                ("ENUMCALLBACK", 0x100),
                ("VALUEWNDPROC", 0x200)
            }

            )
            {
                var fn = fixtures.Functions.SingleOrDefault(f => f.Name == name) ?? throw new InvalidDataException("Missing synthetic callback fixture " + name);
                var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(fn));
                bodies.Add(body);
                body.Register(calls, memory, name, new(0x4000, (ushort)offset), CallDistance.Far);
            }

            using (var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x100), Win16CallbackSignature.EnumWindows)))
            {
                WindowsCallbackHost.EnumerateChildren(parent.Handle, callback, 0x12345678);
                Check(callback.LastError is null && callback.InvocationCount == 1 && memory.ReadWord(0x1000, 0x100) == childId && memory.ReadDword(0x1000, 0x102) == 0x12345678 && memory.ReadWord(0x1000, 0x106) == 1, "EnumChildWindows arguments/BOOL");
            }

            using (var callback = new Win16CallbackBinding(machine, handles, new(new(0x4000, 0x200), Win16CallbackSignature.WindowProcedure), new uint[] { 0x9001 }))
            {
                nint result = WindowsCallbackHost.CallWindowProcedure(callback, parent.Handle, 0x9001, 0xBEEF, -123);
                Check(callback.LastError is null && result == (nint)unchecked((int)0x81235678) && memory.ReadWord(0x1000, 0x114) == 0xBEEF && memory.ReadDword(0x1000, 0x116) == unchecked((uint)-123), "window arguments/signed LRESULT");
            }

            if (original is not null)
            {
                var fn = CounterCallbackProfile.Configure(original.Functions.Single(f => f.Name == "SAMPLEWNDPROC"));
                var body = new NativeWin16Body(new Win16BodyCodeGenerator().Generate(fn, symbols: CounterCallbackProfile.Symbols, targets: CounterCallbackProfile.Targets));
                bodies.Add(body);
                body.Register(calls, memory, fn.Name, CounterCallbackProfile.Entry, CallDistance.Far);
                _ = new Win16UserServices(calls, handles);
                using var callback = new Win16CallbackBinding(machine, handles, new(CounterCallbackProfile.Entry, Win16CallbackSignature.WindowProcedure), new uint[] { 0, 0x87 });
                nint expected = WindowsCallbackHost.DefaultWindowProcedure(parent.Handle, 0x87, 0, 0);
                Check(WindowsCallbackHost.CallWindowProcedure(callback, parent.Handle, 0x87, 0, 0) == expected && callback.LastError is null, "synthetic SAMPLEWNDPROC non-paint path");
                Check(WindowsCallbackHost.CallWindowProcedure(callback, parent.Handle, 15, 0, 0) == 0 && callback.LastError is NotSupportedException, "explicit rejection of unsupported painting");
            }

            Check(initial.Equals(machine.State) && machine.Depth == 0 && memory.Segments.Count == 2, "state restoration and callback stack cleanup");
        }
        finally
        {
            foreach (var body in bodies)
            {
                body.Dispose();
            }
        }
    }
}
