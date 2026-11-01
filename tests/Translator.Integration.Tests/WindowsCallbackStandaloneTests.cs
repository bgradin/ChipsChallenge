using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Translator.Assembly;
using Translator.IR;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class WindowsCallbackStandaloneTests
{
    public static WindowsCallbackAcceptanceProgram Build(IrModule? fixtures = null, IrModule? graphics = null)
    {
        var parser = new AssemblyParser();
        var ui = parser.ParseSelected(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Ui.asm"), new[] { "IsPointVisible", "InvertViewportTile", "SAMPLEWNDPROC" });
        Assert.Empty(ui.Diagnostics);
        var sample = parser.Parse(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm"));
        Assert.Empty(sample.Diagnostics);
        return new(CompilerAcceptanceTests.ParseAcceptance(), graphics ?? ui.Module, fixtures ?? sample.Module, ui.Module);
    }

    private static int Execute(byte[] image)
    {
        string path = Path.Combine(Path.GetTempPath(), $"windows_callback-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, image);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Windows API standalone acceptance timed out.");
            }

            return process.ExitCode;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImageHasThreeNativeDllsExpectedImportsAndNoClrHeader()
    {
        var program = Build();
        byte[] image = program.WriteExecutable();
        using var pe = new PEReader(new MemoryStream(image));
        Assert.Equal(System.Reflection.PortableExecutable.Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(0, pe.PEHeaders.PEHeader!.CorHeaderTableDirectory.Size);
        int rva = pe.PEHeaders.PEHeader.ImportTableDirectory.RelativeVirtualAddress;
        var modules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        string Text(int address)
        {
            var b = pe.GetSectionData(address).GetContent().ToArray();
            return System.Text.Encoding.ASCII.GetString(b, 0, Array.IndexOf(b, (byte)0));
        }


        for (int offset = 0; ; offset += 20)
        {
            var descriptor = pe.GetSectionData(rva + offset).GetContent(0, 20).ToArray();
            int lookup = BinaryPrimitives.ReadInt32LittleEndian(descriptor);
            if (lookup == 0)
            {
                break;
            }

            string module = Text(BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(12)));
            var names = new List<string>();
            for (int at = 0; ; at += 8)
            {
                long name = BinaryPrimitives.ReadInt64LittleEndian(pe.GetSectionData(lookup + at).GetContent(0, 8).AsSpan());
                if (name == 0)
                {
                    break;
                }

                names.Add(Text(checked((int)name) + 2));
            }

            modules.Add(module, names);
        }

        Assert.Equal(3, modules.Count);
        Assert.Contains("ExitProcess", modules["KERNEL32.dll"]);
        Assert.Contains("PatBlt", modules["GDI32.dll"]);
        Assert.Contains("EnumChildWindows", modules["USER32.dll"]);
        Assert.Contains("CallWindowProcW", modules["USER32.dll"]);
        Assert.Contains("DefWindowProcW", modules["USER32.dll"]);
        foreach (var import in WindowsCallbackAcceptanceProgram.Imports)
        {
            Assert.Contains(import.Name, modules[import.Module]);
        }

        Assert.Contains(program.Functions, f => f.Name == "SAMPLEWNDPROC");
        Assert.Contains(program.Functions, f => f.Name == "ENUMCALLBACK");
        Assert.Equal(NativeWindowsCallbackRuntime.TargetFixups.Keys.Count(k => k.StartsWith("native_", StringComparison.Ordinal)), WindowsCallbackAcceptanceProgram.Imports.Count);
    }

    [WindowsFact]
    public void FreestandingWindowsCallbackRunsApiGraphicsAndActualWindowsCallbacks() => Assert.Equal(0, Execute(Build().WriteExecutable()));
    [WindowsFact]
    public void RedirectingACallbackToTheWrongBodyIsDetected()
    {
        var program = Build();
        var functions = program.Functions.Select(f => f.Name != "native_windows_callback_runtime"
            ? f
            : f with
            {
                RelativeCallFixups = f.RelativeCallFixups!.Select(p => p.TargetFunction == "ENUMCALLBACK"
                ? p with
                {
                    TargetFunction = "VALUEWNDPROC"
                }
                : p).ToArray()
            }).ToArray();
        var image = new Pe32PlusWriter().WriteProgram(program.Entry, functions, WindowsCallbackAcceptanceProgram.Imports);
        Assert.Equal(143, Execute(image));
    }

    [WindowsFact]
    public void SignedWindowReturnMutationFailsTheNativeOracle()
    {
        var module = new AssemblyParser().Parse(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm")).Module;
        var function = module.Functions.Single(f => f.Name == "VALUEWNDPROC");
        var code = function.Instructions.ToArray();
        int at = Array.FindLastIndex(code, i => i.Operation == "MOV" && i.Operands[0].Register == X86Register.DX);
        code[at] = code[at] with
        {
            Operands = new[]
            {
                code[at].Operands[0],
                new IrOperand("Immediate", "0x8124", ImmediateValue: 0x8124)
            }
        };
        module = module with
        {
            Functions = module.Functions.Select(f => f.Name == function.Name
                ? f with
                {
                    Instructions = code
                }
                : f).ToArray()
        };
        Assert.Equal(147, Execute(Build(module).WriteExecutable()));
    }

    [WindowsFact]
    public void CallbackRecordFieldOffsetMutationFailsTheNativeOracle()
    {
        var module = new AssemblyParser().Parse(Path.GetFullPath("../../../../../tests/Fixtures/WindowsCallback/Callbacks.asm")).Module;
        var function = module.Functions.Single(f => f.Name == "VALUEWNDPROC");
        var code = function.Instructions.ToArray();
        int at = Array.FindIndex(code, i => i.Operation == "MOV" && i.Operands[0].Memory?.Displacement == 0x116);
        var operand = code[at].Operands[0];
        code[at] = code[at] with
        {
            Operands = new[]
            {
                operand with
                {
                    Memory = operand.Memory!with
                    {
                        Displacement = 0x118
                    }
                },
                code[at].Operands[1]
            }
        };
        module = module with
        {
            Functions = module.Functions.Select(f => f.Name == function.Name
                ? f with
                {
                    Instructions = code
                }
                : f).ToArray()
        };
        Assert.Equal(147, Execute(Build(module).WriteExecutable()));
    }
}
