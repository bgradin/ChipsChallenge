using System.Text.Json;
using Translator.Linker;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeKeyboardInputTests
{
    private static byte[] Original() => File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);

    private static NeApplicationProgram Build() => NeApplicationProgram.Build(Original(), new("0002:296A", new ushort[5], 0, WindowMessages: true, CounterPainting: true, SceneComposition: true, KeyboardInput: true));

    private static int Run(byte[] pe) => NeApplicationTests.Execute(pe, originalPath: Environment.GetEnvironmentVariable("CHIPS_NE_INPUT"));

    [SuppliedNeFact]
    public void InputLinksOriginalMainProcedureAndMoveChipWithExplicitBufferedStateContract()
    {
        var a = Build();
        var b = Build();
        Assert.Equal(a.Executable, b.Executable);
        Assert.Equal(JsonSerializer.Serialize(a.Manifest), JsonSerializer.Serialize(b.Manifest));
        Assert.Equal(17, a.Manifest.Translation.Functions.Count);
        Assert.Contains(a.Manifest.Translation.Functions, f => f.Address.Identity == "0002:225C");
        Assert.Contains(a.Manifest.Translation.Functions, f => f.Address.Identity == "0007:1184");
        Assert.Equal(new ushort[] { 0x25, 0x26, 0x27, 0x28 }, a.Manifest.Input!.Keys);
        Assert.Equal(4, a.Manifest.Input.Cases.Count);
        Assert.All(a.Manifest.Input.Cases, c => Assert.Contains(c.Events, e => e.Message == 0x101));
        Assert.Contains(a.Manifest.Translation.FatalBoundaries, b => b.Contains("0003:1A56"));
        Assert.Contains(a.Imports, i => i.Name == "GetFocus");
        Assert.Contains(a.Imports, i => i.Name == "ReleaseDC");
        Assert.False(a.Manifest.Translation.Graph.ClosureComplete);
    }

    [WindowsSuppliedNeFact]
    public void FocusedOriginalArrowPathBuffersAllDirectionsPreservesKeyReleaseAndRepaints() => Assert.Equal(0, Run(Build().Executable));

    [WindowsSuppliedNeFact]
    public void MissingOriginalInputTargetWrongKeyAndWrongExpectedStateAreDetected()
    {
        var program = Build();
        byte[] descriptor = program.Descriptor;
        // The input extension follows the scene's final pixel digest.
        byte[] digest = Convert.FromHexString(program.Manifest.Scene!.Pixels[^1].Sha256);
        int start = descriptor.AsSpan().IndexOf(digest) + 32;
        Assert.True(start >= 32);

        byte[] Mutate(int at)
        {
            var pe = program.Executable.ToArray();
            int image = pe.AsSpan().IndexOf(descriptor);
            Assert.True(image >= 0);
            pe[image + at] ^= 1;
            return pe;
        }


        Assert.Equal(10, Run(Mutate(start))); // No binding for MAINWNDPROC+1.
        byte[] wrongTarget = program.Executable.ToArray();
        int image = wrongTarget.AsSpan().IndexOf(descriptor);
        // COUNTERWNDPROC is executable but cannot replace MAINWNDPROC's original key behavior.
        wrongTarget[image + start] = 0x6a;
        wrongTarget[image + start + 1] = 0x29;
        Assert.Equal(40, Run(wrongTarget));
        byte[] firstEvent = [0, 1, 0x25, 0, 1, 0, 0x4b, 0];
        int key = descriptor.AsSpan().IndexOf(firstEvent);
        Assert.True(key >= 0);
        Assert.Equal(40, Run(Mutate(key + 2))); // Changed LEFT event becomes HOME while expected original direction stays LEFT.
        // Changing expected BufferedX independently is detected as well.
        byte[] check = [0x12, 0x48, 2, 0, 0xff, 0xff];
        int expected = descriptor.AsSpan().IndexOf(check);
        Assert.True(expected >= 0);
        Assert.Equal(40, Run(Mutate(expected + 4)));
    }
}
