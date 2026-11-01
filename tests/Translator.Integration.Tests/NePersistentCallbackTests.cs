using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NePersistentCallbackTests
{
    // A real synthetic NE WNDPROC; both imported calls are resolved from original fixups.
    // Every WM_TIMER mutates DS data. A positive wParam recursively sends the same message
    // with wParam-1 while DS is deliberately null, then checks data, DS and its local frame.
    private static byte[] Fixture(uint? timerResult = null, bool corruptFrame = false, bool corruptCanary = false, bool omitMutation = false, bool wrongFinalCount = false)
    {
        var code = new List<byte>();
        var labels = new Dictionary<string, int>();
        var branches = new List<(int At, string Target)>();

        void Emit(params byte[] bytes) => code.AddRange(bytes);


        void Jump(byte opcode, string target)
        {
            Emit(opcode, 0);
            branches.Add((code.Count - 1, target));
        }


        void Label(string name) => labels.Add(name, code.Count);

        Emit(0x55, 0x8b, 0xec, 0x56, 0x83, 0xec, 2); // BP frame, saved SI, one local word
        Emit(0x83, 0x7e, 12, 2);
        Jump(0x75, "timer"); // final WM_DESTROY verifies persistence across messages
        // 32 native timer probes at depth eight + two queued timers at depth two.
        ushort expected = (ushort)(0x8180 + 32 * 8 + 2 * 2 + (wrongFinalCount
            ? 1
            : 0));
        Emit(0x81, 0x3e, 0, 0, (byte)expected, (byte)(expected >> 8));
        Jump(0x75, "fail");
        Label("timer");
        Emit(0x81, 0x7e, 12, 0x13, 1);
        Jump(0x75, "forward"); // CMP uMsg,WM_TIMER
        Emit(0xc7, 0x46, 0xfc, 0x5a, 0x5a); // local sentinel at BP-4
        if (!omitMutation)
        {
            Emit(0xff, 0x06, 0, 0); // INC word [DS:0]
        }

        Emit(0x8b, 0x36, 0, 0); // MOV SI,[DS:0]
        Emit(0x83, 0x7e, 10, 0);
        Jump(0x74, "forward"); // zero wParam terminates recursion
        Emit(0x1e, 0x31, 0xc0, 0x8e, 0xd8); // save DS, set DS=0: callback must establish automatic DS itself
        Emit(0xff, 0x76, 14, 0x68, 0x13, 1, 0x8b, 0x46, 10, 0x48, 0x50, 0x6a, 0, 0x6a, 0);
        int sendFixup = code.Count + 1;
        Emit(0x9a, 0xff, 0xff, 0, 0);
        Emit(0x8c, 0xd8, 0x09, 0xc0);
        Jump(0x75, "failPopDs"); // outer DS must still be zero after nested entry
        Emit(0x1f, 0xa1, 0, 0, 0x29, 0xf0, 0x3b, 0x46, 10);
        Jump(0x75, "fail"); // DS delta equals nested invocation count
        Emit(0x81, 0x7e, 0xfc, 0x5a, 0x5a);
        Jump(0x75, "fail");
        Jump(0xeb, "forward");
        Label("failPopDs");
        Emit(0x1f);
        Label("fail");
        Emit(0xb8, 1, 0, 0x31, 0xd2);
        Jump(0xeb, "end");
        Label("forward");
        Emit(0xff, 0x76, 14, 0xff, 0x76, 12, 0xff, 0x76, 10, 0xff, 0x76, 8, 0xff, 0x76, 6);
        int defaultFixup = code.Count + 1;
        Emit(0x9a, 0xff, 0xff, 0, 0);
        if (timerResult.HasValue)
        {
            Emit(0x81, 0x7e, 12, 0x13, 1);
            Jump(0x75, "end");
            uint value = timerResult.Value;
            Emit(0xb8, (byte)value, (byte)(value >> 8), 0xba, (byte)(value >> 16), (byte)(value >> 24));
        }

        Label("end");
        if (corruptCanary)
        {
            Emit(0x36, 0xc7, 0x06, 0, 0, 0, 0); // corrupt callback SS canary, not application memory
        }

        Emit(0x8b, 0x76, 0xfe, 0x8b, 0xe5, 0x5d); // restore SI, SP and BP
        if (corruptFrame)
        {
            Emit(0x83, 0xc4, 2);
        }

        Emit(0xca, 10, 0);
        foreach (var branch in branches)
        {
            int delta = labels[branch.Target] - branch.At - 1;
            if (delta is < -128 or > 127)
            {
                throw new InvalidOperationException("Synthetic short branch exceeds range.");
            }

            code[branch.At] = unchecked((byte)(sbyte)delta);
        }

        byte[] bytes = NeLoaderTests.Fixture();
        // Expand code into its unused 0x200..0x3ff envelope; put fixups after initialized code.
        bytes.AsSpan(0x200, 0x200).Clear();
        code.ToArray().CopyTo(bytes, 0x200);
        NeLoaderTests.W(bytes, 0xc2, 256);
        NeLoaderTests.W(bytes, 0xc6, 256);
        NeLoaderTests.W(bytes, 0xc4, 0x100);
        NeLoaderTests.W(bytes, 0x12d, 240);
        bytes[0x2f0] = 0xcb;
        NeLoaderTests.W(bytes, 0x92, 1024);
        bytes.AsSpan(0x112, 7).Clear();
        bytes[0x112] = 4;
        Encoding.ASCII.GetBytes("USER").CopyTo(bytes, 0x113);
        NeLoaderTests.W(bytes, 0x300, 2);
        foreach (var (source, ordinal, index) in new[]
        {
            (sendFixup, 111, 0),
            (defaultFixup, 107, 1)
        }

        )
        {
            int at = 0x302 + index * 8;
            bytes[at] = 3;
            bytes[at + 1] = 1;
            NeLoaderTests.W(bytes, at + 2, (ushort)source);
            NeLoaderTests.W(bytes, at + 4, 1);
            NeLoaderTests.W(bytes, at + 6, (ushort)ordinal);
        }

        return bytes;
    }

    private static NeApplicationProgram Build(byte[] bytes, NeWindowCallbackOptions? options = null) => NeApplicationProgram.Build(bytes, new("1:0", new ushort[5], 0, WindowMessages: true, Callbacks: options));

    [Fact]
    public void CallbackPolicyIsExplicitDeterministicAndPreservesRejectedPainting()
    {
        var first = Build(Fixture());
        var second = Build(Fixture());
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(first.Descriptor.AsSpan(4)));
        Assert.Contains(first.Imports, i => i.Name == "SendMessageW");
        var send = Win16WindowContracts.All.Single(a => a.Name == "SendMessage");
        Assert.Equal((ushort?)111, send.Ordinal);
        Assert.Equal(10, send.CleanupBytes);
        Assert.Contains("MaximumDepth", JsonSerializer.Serialize(first.Manifest.WindowCompatibility));
        Assert.DoesNotContain(first.Manifest.DeferredCapabilities, s => s.Contains("5.4"));
        Assert.Throws<ArgumentException>(() => Build(Fixture(), new(MaximumDepth: 9)));
        Assert.Throws<ArgumentException>(() => Build(Fixture(), new(StackBytes: 1000)));
    }

    [WindowsFact]
    public void NestedSendMessagePreservesOriginalMemoryLocalsCpuAndSelectorState()
    {
        Assert.Equal(0, NeApplicationTests.Execute(Build(Fixture()).Executable));
        Assert.Equal(0, NeApplicationTests.Execute(Build(Fixture(), new(StackBytes: 1024)).Executable));
        Assert.Equal(0, NeApplicationTests.Execute(Build(Fixture(), new(StackBytes: 65536)).Executable));
        Assert.Equal(40, NeApplicationTests.Execute(Build(Fixture(omitMutation: true)).Executable));
        Assert.Equal(40, NeApplicationTests.Execute(Build(Fixture(wrongFinalCount: true)).Executable));
    }

    [WindowsFact]
    public void SignedLongResultsReturnThroughTheNativeWndprocAbi()
    {
        const uint negative = 0xffff8001;
        Assert.Equal(0, NeApplicationTests.Execute(Build(Fixture(negative), new(TimerResult: negative)).Executable));
        Assert.Equal(40, NeApplicationTests.Execute(Build(Fixture(negative), new(TimerResult: 0)).Executable));
    }

    [WindowsFact]
    public void CallbackDepthStackCorruptionAndPartialPoolAllocationFailWithCleanup()
    {
        Assert.Equal(60, NeApplicationTests.Execute(Build(Fixture(), new(MaximumDepth: 7)).Executable));
        Assert.Equal(40, NeApplicationTests.Execute(Build(Fixture(corruptFrame: true)).Executable));
        Assert.Equal(50, NeApplicationTests.Execute(Build(Fixture(corruptCanary: true)).Executable));
        var program = Build(Fixture());
        var damaged = program.Executable.ToArray();
        int at = damaged.AsSpan().IndexOf(program.Descriptor);
        Assert.True(at >= 0);
        uint segments = BinaryPrimitives.ReadUInt32LittleEndian(program.Descriptor.AsSpan(16));
        uint functions = BinaryPrimitives.ReadUInt32LittleEndian(program.Descriptor.AsSpan(20));
        // Image + task + procedures + owner + two stacks; fail on a later stack allocation.
        BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(at + 52), segments + functions + 4);
        Assert.Equal(20, NeApplicationTests.Execute(damaged));
    }

    [WindowsSuppliedNeFact]
    public void LiveOriginalCounterHandlesNestedDefaultCloseWithoutTheFormerDeferral()
    {
        byte[] bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var program = NeApplicationProgram.Build(bytes, new("2:296a", new ushort[5], 0, WindowMessages: true));
        Assert.Equal(0, NeApplicationTests.Execute(program.Executable));
        // WM_CLOSE synchronously calls WM_DESTROY; one permitted frame cannot execute this path.
        var limited = NeApplicationProgram.Build(bytes, new("2:296a", new ushort[5], 0, WindowMessages: true, Callbacks: new(MaximumDepth: 1)));
        Assert.Equal(60, NeApplicationTests.Execute(limited.Executable));
    }
}
