using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

/// <summary>CLR-free API, GDI and Windows callback acceptance over the existing native bodies and PE linker.</summary>
public sealed class WindowsCallbackAcceptanceProgram
{
    public static IReadOnlyList<PeNativeImport> Imports { get; } = Array.AsReadOnly(NativeWindowsCallbackRuntime.Imports.Select(i => new PeNativeImport(i.Module, i.Name, i.Symbol)).ToArray());
    public PeFunctionBody Entry
    {
        get;
    }
    public IReadOnlyList<PeFunctionBody> Functions
    {
        get;
    }

    public WindowsCallbackAcceptanceProgram(IrModule baseline, IrModule graphics, IrModule fixtures, IrModule callbacks)
    {
        var previous = new WindowsCallbackGraphicsProgram(baseline, graphics);
        Entry = new("windows_callback_entry", new byte[] { 0xE9, 0, 0, 0, 0 }, new[] { new PeRelativeCallFixup(1, "windows_callback_acceptance") });
        var runtime = new MachineCodeArtifact(NativeWindowsCallbackRuntime.Bytes, "AMD64", X64Abi.Windows, "int windows_callback_acceptance()", NativeWindowsCallbackRuntime.TargetFixups.Select(f => new MachineCodeRelocation(f.Value, f.Key, MachineCodeRelocationKind.Relative32Call)).ToArray());
        var functions = new List<PeFunctionBody>
        {
            PeFunctionBody.FromArtifact("native_windows_callback_runtime", runtime)with
            {
                Exports = NativeWindowsCallbackRuntime.Exports
            }
        };
        functions.AddRange(previous.Functions.Where(f => f.Name != "native_graphics_runtime"));
        foreach (string name in new[]
        {
            "ENUMCALLBACK",
            "STOPCALLBACK",
            "VALUEWNDPROC"
        }

        )
        {
            var function = fixtures.Functions.SingleOrDefault(f => f.Name == name) ?? throw new InvalidDataException("Missing callback fixture " + name);
            functions.Add(PeFunctionBody.FromArtifact(name, new Win16BodyCodeGenerator().Generate(function)));
        }

        var original = callbacks.Functions.SingleOrDefault(f => f.Name == "SAMPLEWNDPROC") ?? throw new InvalidDataException("Missing synthetic SAMPLEWNDPROC callback.");
        functions.Add(PeFunctionBody.FromArtifact(original.Name, new Win16BodyCodeGenerator().Generate(CounterCallbackProfile.Configure(original), symbols: CounterCallbackProfile.Symbols, targets: CounterCallbackProfile.Targets)));
        Functions = functions.AsReadOnly();
    }

    public byte[] WriteExecutable() => new Pe32PlusWriter().WriteProgram(Entry, Functions, Imports);
}
