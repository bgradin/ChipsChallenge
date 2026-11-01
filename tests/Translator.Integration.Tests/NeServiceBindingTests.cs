using Translator.Analysis;
using System.Text.Json;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeServiceBindingTests
{
    [Fact]
    public void CatalogMatchesActualAdapterIdentitySetsWithoutTreatingTheConstantAsCallable()
    {
        var providers = new Dictionary<string, IReadOnlySet<string>>
        {
            [nameof(Win16TaskKernelServices)] = Win16TaskKernelServices.Implemented,
            [nameof(Win16TaskResources)] = Win16TaskResources.Implemented,
            [nameof(Win16TaskFiles)] = Win16TaskFiles.Implemented,
            [nameof(Win16TaskProfiles)] = Win16TaskProfiles.Implemented,
            [nameof(Win16TaskSystem)] = Win16TaskSystem.Implemented,
            [nameof(Win16TaskInstances)] = Win16TaskInstances.Implemented,
            [nameof(Win16TaskGui)] = Win16TaskGui.Implemented,
            [nameof(Win16TaskGraphics)] = Win16TaskGraphics.Implemented,
            [nameof(Win16TaskUserStructures)] = Win16TaskUserStructures.Implemented,
            [nameof(Win16TaskGuiResources)] = Win16TaskGuiResources.Implemented,
            [nameof(Win16TaskWindowHost)] = Win16TaskWindowHost.Implemented,
            [nameof(Win16TaskModules)] = Win16TaskModules.Implemented,
            [nameof(Win16TaskLifecycle)] = Win16TaskLifecycle.Implemented,
            [nameof(Win16TaskWep)] = Win16TaskWep.Implemented,
        };
        Assert.Equal(122, Win16TaskServiceBindings.All.Count);
        Assert.DoesNotContain("KERNEL.#178", Win16TaskServiceBindings.All.Keys);
        foreach (var provider in providers)
        {
            Assert.Equal(provider.Value.Order(), Win16TaskServiceBindings.All.Values.Where(b => b.ServiceClass == provider.Key).Select(b => b.Identity).Order());
        }

        Assert.All(Win16TaskServiceBindings.All.Values, binding =>
        {
            Assert.NotEqual("Constant", Win16ExportContracts.Get(binding.Identity).Kind);
            Assert.InRange(binding.NativeModule, (ushort)1, (ushort)4);
            Assert.True(Win16TaskServiceBindings.IsNativeBound(binding, NativeWholeTaskRuntime.Exports), binding.Identity);
            Assert.NotEmpty(binding.ContractTests);
            Assert.NotEmpty(binding.Limitations);
        });
    }

    [Fact]
    public void NativeBindingRequiresBothTheDispatcherAndTheConcreteAdapterExport()
    {
        var binding = Win16TaskServiceBindings.All["KERNEL.#91"];
        var exports = NativeWholeTaskRuntime.Exports.ToDictionary(p => p.Key, p => p.Value);
        Assert.True(Win16TaskServiceBindings.IsNativeBound(binding, exports));
        exports.Remove(Win16TaskServiceBindings.Dispatcher);
        Assert.False(Win16TaskServiceBindings.IsNativeBound(binding, exports));
        exports[Win16TaskServiceBindings.Dispatcher] = 0;
        exports.Remove(binding.NativeExport);
        Assert.False(Win16TaskServiceBindings.IsNativeBound(binding, exports));
        Assert.True(Win16TaskServiceBindings.IsManagedOwnerWired("KERNEL.#5"));
        Assert.False(Win16TaskServiceBindings.IsManagedOwnerWired("KERNEL.#91"));
    }

    [SuppliedNeFact]
    public void ReconciledInventorySeparatesSourceNativeBindingOwnerWiringAndWindowsEvidence()
    {
        var bytes = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        var report = NeImportContractAuditBuilder.Build(bytes);
        Assert.Equal("NeImportContracts.v2", report.Format);
        Assert.Equal(123, report.TotalImports);
        Assert.Equal(1, report.ConstantExports);
        Assert.Equal(122, report.SourceAdapters);
        Assert.Equal(122, report.NativeBoundUnverified);
        Assert.Equal(63, report.ManagedOwnerAdapters);
        Assert.Equal(5, report.DynamicServices.Count);
        Assert.All(report.DynamicServices, service =>
        {
            Assert.Equal("implemented/unverified", service.ImplementationStatus);
            Assert.True(service.NativeExportPresent);
        });
        Assert.Equal(2, report.Dependencies.Count);
        Assert.Equal(64, report.Dependencies.Single(d => d.Module == "WEP4UTIL").SourceIdentity.Length);
        Assert.Equal(0, report.WindowsVerifiedAdapters);
        Assert.Equal(0, report.ProductionVerifiedAdapters);
        Assert.False(report.CompleteApplicationServiceCoverage);
        Assert.Equal(0, report.RecognizedButUnsupported);
        Assert.Equal(122, report.ImplementedButNotExercised);
        Assert.All(report.Imports.Where(i => i.Binding is not null), row =>
        {
            Assert.Equal("implemented/unverified", row.ImplementationStatus);
            Assert.True(row.NativeDispatcherBound);
            Assert.False(row.WindowsVerified);
            Assert.NotEmpty(row.Sources);
        });
        var constant = Assert.Single(report.Imports, i => i.ImplementationStatus == "constant");
        Assert.Null(constant.Binding);
        Assert.False(constant.NativeDispatcherBound);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(NeImportContractAuditBuilder.Build(bytes)));
    }
}
