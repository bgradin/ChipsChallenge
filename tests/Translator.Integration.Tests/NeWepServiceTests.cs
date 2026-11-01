using System.Runtime.InteropServices;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeWepServiceTests
{
    private sealed class Backend : IWin16GuiBackend
    {
        public Func<string, nint[], nint> Handler = (_, _) => 1;

        public nint Call(string module, string function, ReadOnlySpan<nint> args) => Handler(function, args.ToArray());
    }

    [SuppliedNeFact]
    public void ConvertedHelpUsesGenericKeywordContextAndContentsRoutes()
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        string directory = Path.Combine(Path.GetTempPath(), "help-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string module = Path.Combine(directory, "sample.exe"), converted = Path.ChangeExtension(module, ".CHM");
            File.WriteAllBytes(converted, Translator.ChipsChallenge.SupportedChipsHelp.Convert(SampleWinHelp.Create(contentsId: 1, detailsId: 2)));
            var memory = new SegmentMemory();
            using var data = memory.CreateSegment(1, 2048);
            memory.Map(data);
            var routes = new List<(string? File, nint Command)>();
            var backend = new Backend();
            backend.Handler = (name, args) =>
            {
                Assert.Equal("HtmlHelpW", name);
                routes.Add((Marshal.PtrToStringUni(args[1]), args[2]));
                return 1;
            };
            using var utility = new Win16TaskWep(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(source)!, "WEP4UTIL.DLL")), File.ReadAllBytes(source), memory, backend, new Win16GuiHandles(), 1, module);
            var state = new MachineState
            {
                DS = 1,
                SS = 1,
                SP = 1024
            };

            void Request(ushort command, uint value)
            {
                var contract = Win16ExportContracts.Get("WEP4UTIL.#5");
                uint[] args = [1, 0, command, value];
                for (int i = 0; i < args.Length; i++)
                {
                    var argument = contract.Arguments[i];
                    ushort at = (ushort)(state.SP + 4 + argument.StackWord * 2);
                    if (argument.Bytes == 4)
                    {
                        memory.WriteDword(1, at, args[i]);
                    }
                    else
                    {
                        memory.WriteWord(1, at, (ushort)args[i]);
                    }
                }

                utility.Invoke("WEP4UTIL.#5", ref state);
            }


            memory.WriteBytes(1, 64, "sAmPlE-kEy\0"u8);
            Request(0x101, 0x10040);
            Assert.Equal((converted + "::/keywords/" + Translator.Core.HelpKeywordRoute.File("sample-key"), (nint)0), routes[^1]);
            Request(1, 2);
            Assert.Equal((converted + "::/html/topic2.htm", (nint)0), routes[^1]);
            Request(3, 0);
            Assert.Equal((converted, (nint)0), routes[^1]);
            memory.WriteByte(1, 64, 0);
            Request(0x105, 0x10040);
            Assert.Equal((converted, (nint)2), routes[^1]);
            int count = routes.Count;
            Request(0xffff, 0);
            Assert.Equal((ushort)0, state.AX);
            Assert.Equal(count, routes.Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [SuppliedNeFact]
    public void VerifiedUtilityExportsUseTheirOriginalArgumentsAndNativeObjects()
    {
        string path = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        byte[] exe = File.ReadAllBytes(path), dll = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "WEP4UTIL.DLL"));
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 2048);
        memory.Map(data);
        var backend = new Backend();
        var handles = new Win16GuiHandles();
        ushort window = handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window), dc = handles.Add(unchecked((nint)0x223456789L), Win16GuiHandleKind.Dc);
        using var wep = new Win16TaskWep(dll, exe, memory, backend, handles, 1, "C:\\APP\\CHIPS.EXE");
        var state = new MachineState
        {
            DS = 1,
            SS = 1,
            SP = 1024,
            AX = 0x8888,
            DX = 0x7777
        };
        int warnings = 0, background = 0, mode = 0;
        var calls = new List<string>();
        backend.Handler = (name, a) =>
        {
            calls.Add(name);
            switch (name)
            {
                case "MessageBoxW":
                    warnings++;
                    Assert.Contains("newer version", Marshal.PtrToStringUni(a[1]));
                    return 1;
                case "GetStockObject":
                    Assert.Equal((nint)1, a[0]);
                    return unchecked((nint)0x323456789L);
                case "SetBkColor":
                    Assert.Equal((nint)0xc0c0c0, a[1]);
                    background++;
                    return 0;
                case "SetBkMode":
                    Assert.Equal((nint)1, a[1]);
                    mode++;
                    return 1;
                case "GetWindowRect":
                    Marshal.WriteInt32(a[1], 8, 200);
                    Marshal.WriteInt32(a[1], 12, 100);
                    return 1;
                case "GetSystemMetrics":
                    return a[0] == 0
                        ? 800
                        : 600;
                case "SetWindowPos":
                    Assert.Equal((nint)296, a[2]);
                    Assert.Equal((nint)166, a[3]);
                    Assert.Equal((nint)0x11, a[6]);
                    return 1;
                case "WinHelpW":
                    Assert.Equal("C:\\APP\\CHIPS.HLP", Marshal.PtrToStringUni(a[1]));
                    Assert.Equal((nint)0x101, a[2]);
                    Assert.Equal("Contents", Marshal.PtrToStringUni(a[3]));
                    return 1;
                default:
                    throw new Exception(name);
            }
        };

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

            return wep.Invoke(id, ref state);
        }


        Assert.Equal((ushort)2, Call("WEP4UTIL.#2", 0x218));
        Assert.Equal((ushort)1, state.AX);
        Call("WEP4UTIL.#2", 0x301);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal(0, warnings);
        Call("WEP4UTIL.#2", 0x300);
        Assert.Equal(1, warnings);
        Assert.Equal((ushort)10, Call("WEP4UTIL.#1202", window, 0x19, dc, 1u << 16));
        Assert.NotEqual((ushort)0, state.AX);
        Assert.Equal(1, background);
        Assert.Equal(1, mode);
        Call("WEP4UTIL.#1202", window, 0x19, dc, 2u << 16);
        Assert.Equal((ushort)0, state.AX);
        Assert.Equal((ushort)0x7777, state.DX);
        Call("WEP4UTIL.#103", window);
        memory.WriteBytes(1, 64, System.Text.Encoding.ASCII.GetBytes("Contents\0"));
        Call("WEP4UTIL.#5", 1, window, 0x101, 0x10040);
        Assert.Equal((ushort)1, state.AX);
        byte[] altered = dll.ToArray();
        altered[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => new Win16TaskWep(altered, exe, memory, backend, handles, 1, path));
        wep.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Call("WEP4UTIL.#2", 0x218));
    }

    [SuppliedNeFact]
    public void AboutUsesTheOriginalDialogAndApplicationStringsAndReleasesItsIcon()
    {
        string path = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        byte[] exe = File.ReadAllBytes(path), dll = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "WEP4UTIL.DLL"));
        var memory = new SegmentMemory();
        using var data = memory.CreateSegment(1, 2048);
        memory.Map(data);
        var backend = new Backend();
        var handles = new Win16GuiHandles();
        ushort window = handles.Add(unchecked((nint)0x123456789L), Win16GuiHandleKind.Window);
        using var wep = new Win16TaskWep(dll, exe, memory, backend, handles, 1, path);
        var state = new MachineState
        {
            DS = 1,
            SS = 1,
            SP = 1024
        };
        var labels = new Dictionary<int, string>();
        int iconsFreed = 0, ended = 0;
        backend.Handler = (name, a) =>
        {
            switch (name)
            {
                case "CreateIconFromResourceEx":
                    return unchecked((nint)0x223456789L);
                case "DestroyIcon":
                    iconsFreed++;
                    return 1;
                case "GetModuleHandleW":
                    return 0x4444;
                case "DialogBoxIndirectParamW":
                    var callback = Marshal.GetDelegateForFunctionPointer<Win16TaskWindowHost.WindowProcedure>(a[3]);
                    Assert.Equal((nint)1, callback(a[2], 0x110, 0, 0));
                    callback(a[2], 0x111, 1, 0);
                    return 1;
                case "SetDlgItemTextW":
                    labels[(int)a[1]] = Marshal.PtrToStringUni(a[2])!;
                    return 1;
                case "SetWindowTextW":
                    Assert.Contains("Chip", Marshal.PtrToStringUni(a[1]), StringComparison.OrdinalIgnoreCase);
                    return 1;
                case "GetWindowRect":
                    Marshal.WriteInt32(a[1], 8, 200);
                    Marshal.WriteInt32(a[1], 12, 100);
                    return 1;
                case "GetSystemMetrics":
                    return 800;
                case "EndDialog":
                    ended++;
                    return 1;
                default:
                    return 1;
            }
        };
        memory.WriteWord(1, 1028, window);
        memory.WriteWord(1, 1030, 1);
        Assert.Equal((ushort)4, wep.Invoke("WEP4UTIL.#4", ref state));
        Assert.Equal(1, ended);
        Assert.Equal(1, iconsFreed);
        Assert.Equal(3, labels.Count);
        Assert.Contains("Chip", labels[702], StringComparison.OrdinalIgnoreCase);
    }
}
