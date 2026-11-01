using Translator.Assembly;
using Xunit;

namespace Translator.Analysis.Tests;

public class ControlFlowTests
{
    [Fact]
    public void CreatesConditionalEdges()
    {
        var p = Path.GetTempFileName();
        try
        {
            File.WriteAllText(p, "func F\n jz .done\n nop\n.done:\n ret\nendfunc");
            var f = new AssemblyParser().Parse(p).Module.Functions[0];
            var b = new ControlFlowAnalyzer().BuildBlocks(f);
            Assert.Contains(b.SelectMany(x => x.Successors), e => e.Kind == "true");
        }
        finally
        {
            File.Delete(p);
        }
    }
}
