using Translator.Analysis;
using Translator.Linker;
using Win16_Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Translator.Integration.Tests;

public sealed class NeWholeTaskOwnerTests(ITestOutputHelper output)
{
    private sealed class Gui : IWin16GuiBackend
    {
        public readonly List<string> Calls = [];

        public nint Call(string module, string name, ReadOnlySpan<nint> args)
        {
            Calls.Add(name);
            if (name == "PeekMessageW")
            {
                return 0; // empty recording-provider queue, not an InitApp replacement
            }

            throw new NotSupportedException("Recording backend has no " + name);
        }
    }

    private sealed class Multimedia : IWin16MultimediaBackend
    {
        public bool Load() => false;

        public bool HasExport(string name) => false;

        public nint Call(string name, ReadOnlySpan<nint> args) => throw new NotSupportedException(name);

        public void Unload()
        {
        }

        public void Dispose()
        {
        }
    }

    [SuppliedNeFact]
    public void UtilityDependencyPolicyPinsTheDllAndRetainsItsOriginalTransitiveImports()
    {
        string path = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        byte[] image = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "WEP4UTIL.DLL"));
        var audit = NeUtilityDependencyAuditBuilder.Build(image);
        Assert.True(audit.RequiredAtTranslation);
        Assert.False(audit.RequiredAtRuntime);
        Assert.NotEmpty(audit.OriginalTransitiveImports);
        Assert.Equal(64, audit.Sha256.Length);
        image[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => NeUtilityDependencyAuditBuilder.Build(image));
    }

    [SuppliedNeFact]
    public void CompleteOwnerBindsEveryCallableIdentityAndOriginalStartupCrossesInitApp()
    {
        string path = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!;
        byte[] original = File.ReadAllBytes(path), utility = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "WEP4UTIL.DLL"));
        var gui = new Gui();
        using var multimedia = new Multimedia();
        using var program = new NeWholeProgramControlProgram(original, initializeKernelServices: true, guiBackend: gui, taskServicesOptions: new(utility, path, multimedia), maximumTransfers: 100000);
        Assert.Equal(122, program.InstalledImports.Count);
        Assert.DoesNotContain("KERNEL.#178", program.InstalledImports);
        var state = program.InitialState;
        var failure = Record.Exception(() => program.EnterStartup(ref state));
        output.WriteLine("Calls: " + string.Join(",", program.ServiceCalls));
        output.WriteLine("Failure: " + failure);
        Assert.NotNull(failure);
        Assert.Contains("USER.#5", program.ServiceCalls);
        Assert.Contains("PeekMessageW", gui.Calls);
        Assert.True(program.LifecycleServices!.Initialized);
        Assert.DoesNotContain("Unimplemented original import", failure.ToString());
    }
}
