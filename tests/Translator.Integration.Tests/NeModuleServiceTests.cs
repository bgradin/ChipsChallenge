using System.Runtime.InteropServices;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeModuleServiceTests
{
    private sealed class Backend : IWin16MultimediaBackend
    {
        public bool Available = true;
        public int Loads, Unloads, Disposals;
        public readonly List<string> Names = [];
        public Func<string, nint[], nint> Handler = (_, _) => 0;

        public bool Load()
        {
            Loads++;
            return Available;
        }

        public bool HasExport(string name) => Available;

        public nint Call(string name, ReadOnlySpan<nint> args)
        {
            Names.Add(name);
            return Handler(name, args.ToArray());
        }

        public void Unload() => Unloads++;

        public void Dispose() => Disposals++;
    }

    [Fact]
    public void ModuleProceduresAreBoundedRetainedAndRejectCallsAfterUnload()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 4096);
        memory.Map(data);
        using var control = new Win16ProgramControl([], memory, new());
        var backend = new Backend();
        using var modules = new Win16TaskModules(memory, control, backend, new(), Path.GetTempPath());
        var state = new MachineState
        {
            DS = 1,
            SS = 1,
            SP = 2048,
            DX = 0x7777
        };
        memory.WriteBytes(1, 64, System.Text.Encoding.ASCII.GetBytes("MMSYSTEM.DLL\0"));
        memory.WriteBytes(1, 96, System.Text.Encoding.ASCII.GetBytes("midiOutGetNumDevs\0"));

        ushort Call(string id, params uint[] args)
        {
            var c = Win16ExportContracts.Get(id);
            for (int i = 0; i < c.Arguments.Count; i++)
            {
                var a = c.Arguments[i];
                ushort at = (ushort)(state.SP + 4 + a.StackWord * 2);
                if (a.Bytes == 4)
                {
                    memory.WriteDword(1, at, args[i]);
                }
                else
                {
                    memory.WriteWord(1, at, (ushort)args[i]);
                }
            }

            return modules.Invoke(id, ref state);
        }


        backend.Available = false;
        Call("KERNEL.#95", 0x10040);
        Assert.True(state.AX < 32);
        backend.Available = true;
        Call("KERNEL.#95", 0x10040);
        Assert.Equal((ushort)0x600, state.AX);
        Call("KERNEL.#50", 0x600, 0x10060);
        Assert.Equal(new Win16CodeAddress(0xf100, 201), new(state.DX, state.AX));
        backend.Handler = (name, args) =>
        {
            Assert.Equal("midiOutGetNumDevs", name);
            Assert.Empty(args);
            return 2;
        };
        modules.InvokeProcedure("midiOutGetNumDevs", ref state);
        Assert.Equal((ushort)2, state.AX);
        Call("KERNEL.#50", 0x600, 999);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0, state.DX);
        backend.Handler = (_, _) => 1;
        Call("KERNEL.#95", 0x10040);
        Call("KERNEL.#96", 0x600);
        Assert.Equal(0, backend.Unloads);
        Call("KERNEL.#96", 0x600);
        Assert.Equal(1, backend.Unloads);
        Assert.Throws<InvalidOperationException>(() => modules.InvokeProcedure("midiOutGetNumDevs", ref state));
        Assert.Throws<InvalidOperationException>(() => control.RegisterModuleProcedure(new(0xf100, 999), "MMSYSTEM.unapproved", (ref MachineState s, SegmentMemory m) => 0));
        modules.Dispose();
        Assert.Equal(0, backend.Disposals); // injected providers stay owned by their caller.
    }

    [Fact]
    public void MciOpenStatusPlayAndCloseMarshalStructuresAndKeepNativeDeviceIdsOutOfGuestMemory()
    {
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 4096);
        memory.Map(data);
        using var control = new Win16ProgramControl([], memory, new());
        var backend = new Backend();
        var handles = new Win16GuiHandles();
        ushort window = handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window);
        using var modules = new Win16TaskModules(memory, control, backend, handles, Path.GetTempPath());
        var state = new MachineState
        {
            DS = 1,
            SS = 1,
            SP = 2048
        };
        memory.WriteBytes(1, 64, System.Text.Encoding.ASCII.GetBytes("MMSYSTEM.DLL\0"));
        memory.WriteDword(1, 2052, 0x10040);
        modules.Invoke("KERNEL.#95", ref state);
        memory.WriteBytes(1, 96, System.Text.Encoding.ASCII.GetBytes("sequencer\0"));
        memory.WriteBytes(1, 128, System.Text.Encoding.ASCII.GetBytes("CANYON.MID\0"));
        memory.WriteDword(1, 264, 0x10060);
        memory.WriteDword(1, 268, 0x10080);
        backend.Handler = (name, a) =>
        {
            if (name == "sndPlaySoundW")
            {
                return 1;
            }

            Assert.Equal("mciSendCommandW", name);
            switch ((int)a[1])
            {
                case 0x803:
                    Assert.Equal("sequencer", Marshal.PtrToStringUni(Marshal.ReadIntPtr(a[3], 12)));
                    Assert.EndsWith("CANYON.MID", Marshal.PtrToStringUni(Marshal.ReadIntPtr(a[3], 20)));
                    Marshal.WriteInt32(a[3], 8, 0x12345678);
                    return 0;
                case 0x814:
                    Assert.Equal((nint)0x12345678, a[0]);
                    Assert.Equal(0x4003, Marshal.ReadInt32(a[3], 16));
                    Marshal.WriteIntPtr(a[3], 8, (nint)42);
                    return 0;
                case 0x806:
                    Assert.Equal(unchecked((nint)0x123456789L), Marshal.ReadIntPtr(a[3]));
                    Assert.Equal(17, Marshal.ReadInt32(a[3], 8));
                    return 0;
                case 0x804:
                    return 0;
                default:
                    throw new Exception();
            }
        };

        void Mci(ushort device, ushort command, uint flags, uint pointer)
        {
            ushort[] words =
            {
                (ushort)pointer,
                (ushort)(pointer >> 16),
                (ushort)flags,
                (ushort)(flags >> 16),
                command,
                device
            };
            for (int i = 0; i < words.Length; i++)
            {
                memory.WriteWord(state.SS, (ushort)(state.SP + 4 + i * 2), words[i]);
            }

            Assert.Equal((ushort)12, modules.InvokeProcedure("mciSendCommand", ref state));
        }


        ushort sp = state.SP;
        Mci(0, 0x803, 0x2200, 0x10100);
        Assert.Equal(sp, state.SP);
        Assert.Equal((ushort)0, state.AX);
        ushort device = memory.ReadWord(1, 260);
        Assert.NotEqual((ushort)0, device);
        Assert.NotEqual((ushort)0x5678, device);
        Assert.Equal(device, modules.GuestDevice(0x12345678));
        memory.WriteDword(1, 296, 0x4003);
        Mci(device, 0x814, 0x100, 0x10120);
        Assert.Equal(42u, memory.ReadDword(1, 292));
        memory.WriteDword(1, 320, 0xabcd0000u | window);
        memory.WriteDword(1, 324, 17);
        Mci(device, 0x806, 5, 0x10140);
        Assert.Equal((ushort)0, state.AX);
        Mci(device, 0x804, 0, 0);
        Mci(device, 0x806, 5, 0x10140);
        Assert.Equal((ushort)257, state.AX);
    }
}
