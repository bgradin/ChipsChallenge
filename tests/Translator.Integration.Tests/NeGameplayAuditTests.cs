using Translator.Analysis;
using Translator.Binary;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeGameplayAuditTests
{
    private static (byte[] Bytes, NeProgramGraph Graph, NeJumpTableAudit Table) Audit(byte[]? supplied = null)
    {
        // CMP AX,1; JA default; SHL AX,1; XCHG AX,BX; JMP CS:[BX+20h].
        byte[] bytes = supplied ?? NeDiscoveryTests.Fixture(0x83, 0xf8, 1, 0x77, 0x0b, 0xd1, 0xe0, 0x93, 0x2e, 0xff, 0xa7, 0x20, 0, 0xcb);
        if (supplied is null)
        {
            bytes[0x210] = 0xcb;
            bytes[0x218] = 0xcb;
            bytes[0x219] = 0xcb;
            NeLoaderTests.W(bytes, 0x220, 0x18);
            NeLoaderTests.W(bytes, 0x222, 0x19);
        }

        var graph = new NeProgramAnalyzer().Analyze(bytes, "1:0", auditJumpTables: true);
        var function = graph.Functions.Single();
        var site = function.Instructions.Single(i => i.FlowControl == "IndirectBranch").Address;
        return (bytes, graph, NeJumpTableAnalyzer.Analyze(bytes, new NeExecutableReader().Read(bytes), function.Instructions, site));
    }

    [Fact]
    public void BoundedTableEnumeratesFirstLastDefaultAndKeepsNativeBoundaryFatal()
    {
        var audit = Audit();
        Assert.True(audit.Table.Resolved, audit.Table.Diagnostic);
        Assert.Equal(new ushort[] { 0x18, 0x19 }, audit.Table.Entries.Select(e => e.Target.Offset));
        Assert.Equal(0x10, audit.Table.DefaultTarget!.Value.Offset);
        Assert.Contains(audit.Graph.Functions.Single().Instructions, i => i.Address.Offset == 0x19);
        Assert.False(audit.Graph.ClosureComplete); // 6.1 analysis cannot authorize 6.2 emission.
        Assert.Contains(audit.Graph.Dependencies, d => d.Kind == "IndirectBranch" && !d.Resolved);
        Assert.DoesNotContain(new NeProgramAnalyzer().Analyze(audit.Bytes, "1:0").Functions.Single().Instructions, i => i.Address.Offset == 0x19);
    }

    [Fact]
    public void MutatedBoundScaleTargetsAndSharedEntriesAreAuditedCausally()
    {
        byte[] bytes = Audit().Bytes;
        NeLoaderTests.W(bytes, 0x222, 0x18);
        Assert.Equal(2, Audit(bytes).Table.Entries.Count);
        Assert.Single(Audit(bytes).Table.Entries.Select(e => e.Target).Distinct());
        NeLoaderTests.W(bytes, 0x222, 0xffff);
        Assert.False(Audit(bytes).Table.Resolved);
        NeLoaderTests.W(bytes, 0x222, 1); // interior of CMP
        Assert.False(Audit(bytes).Table.Resolved);
        bytes[0x206] = 0xe3; // SHL BX,1 instead of AX
        Assert.False(Audit(bytes).Table.Resolved);
        bytes = Audit().Bytes;
        bytes[0x203] = 0x7f; // signed JG is not an unsigned range proof
        Assert.False(Audit(bytes).Table.Resolved);
        bytes = Audit().Bytes;
        bytes[0x202] = 0xff; // sign-extended FFFF cannot bound AX*2
        Assert.False(Audit(bytes).Table.Resolved);
        bytes = Audit().Bytes;
        bytes[0x20a] = 0x27; // CS:[BX], no table displacement; becomes unproven
        Assert.False(Audit(bytes).Table.Resolved);
    }
}
