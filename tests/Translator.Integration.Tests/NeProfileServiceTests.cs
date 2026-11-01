using System.Text;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeProfileServiceTests
{
    [Fact]
    public void ProfileRoundTripsDeletesEnumeratesAndPreservesOtherSectionsWithDefinedFailures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "p7p-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "STATE.INI");
            File.WriteAllText(file, "; comment\n[Other]\nKeep=9\n[Player]\nNumber=0x10\nWord=\"quoted\"\n", Encoding.Latin1);
            var memory = new SegmentMemory();
            using var segment = memory.CreateSegment(1, 4096);
            memory.Map(segment);
            var state = new MachineState
            {
                DS = 1,
                SS = 1,
                SP = 2048,
                DX = 0xabcd
            };
            var profile = new Win16TaskProfiles(memory, directory);
            ushort cursor = 64;

            uint Text(string text)
            {
                ushort at = cursor;
                var bytes = Encoding.Latin1.GetBytes(text + '\0');
                cursor += (ushort)bytes.Length;
                memory.WriteBytes(1, at, bytes);
                return 0x10000u | at;
            }


            ushort Call(string identity, params uint[] args)
            {
                var c = Win16ExportContracts.Get(identity);
                foreach (var a in c.Arguments)
                {
                    uint value = args[int.Parse(a.Name.AsSpan(3))];
                    ushort at = (ushort)(state.SP + 4 + a.StackWord * 2);
                    if (a.Bytes == 4)
                    {
                        memory.WriteDword(1, at, value);
                    }
                    else
                    {
                        memory.WriteWord(1, at, (ushort)value);
                    }
                }

                return profile.Invoke(identity, ref state);
            }

            uint app = Text("Player"), number = Text("Number"), path = Text("STATE.INI"), word = Text("Word"), fallback = Text("default  "), output = 0x10400;
            Assert.Equal((ushort)14, Call("KERNEL.#127", app, number, 7, path));
            Assert.Equal((ushort)16, state.AX);
            Assert.Equal((ushort)0xabcd, state.DX);
            Call("KERNEL.#127", app, Text("Absent"), 7, path);
            Assert.Equal((ushort)7, state.AX);
            Assert.Equal((ushort)22, Call("KERNEL.#128", app, word, fallback, output, 64, path));
            Assert.Equal((ushort)6, state.AX);
            Assert.Equal("quoted", Win16TaskKernelServices.ReadString(memory, output));
            Call("KERNEL.#128", app, Text("Absent"), fallback, output, 64, path);
            Assert.Equal("default", Win16TaskKernelServices.ReadString(memory, output));
            Call("KERNEL.#128", app, word, 0, output, 4, path);
            Assert.Equal((ushort)3, state.AX);
            Assert.Equal("quo", Win16TaskKernelServices.ReadString(memory, output));
            Call("KERNEL.#128", app, word, 0, 0, 0, path);
            Assert.Equal((ushort)0, state.AX);
            Assert.Equal((ushort)16, Call("KERNEL.#129", app, number, Text("-2"), path));
            Assert.Equal((ushort)1, state.AX);
            Call("KERNEL.#127", app, number, 0, path);
            Assert.Equal((ushort)0xfffe, state.AX);
            Call("KERNEL.#129", app, number, Text("bad"), path);
            Call("KERNEL.#127", app, number, 7, path);
            Assert.Equal((ushort)0, state.AX);
            Call("KERNEL.#129", app, number, 0, path);
            Call("KERNEL.#127", app, number, 7, path);
            Assert.Equal((ushort)7, state.AX);
            Call("KERNEL.#128", 0, 0, 0, output, 64, path);
            var sections = memory.ReadBytes(1, 1024, state.AX + 1);
            Assert.Equal(new byte[] { 0, 0 }, sections[^2..]);
            Assert.Contains("Other", Encoding.Latin1.GetString(sections));
            Assert.Contains("Player", Encoding.Latin1.GetString(sections));
            Call("KERNEL.#129", app, 0, 0, path);
            Assert.DoesNotContain("[Player]", File.ReadAllText(file));
            Assert.Contains("Keep=9", File.ReadAllText(file));
            Call("KERNEL.#129", 0, 0, 0, path);
            Assert.Equal((ushort)0, state.AX);
            Call("KERNEL.#129", app, word, Text("x"), Text("absent/STATE.INI"));
            Assert.Equal((ushort)0, state.AX);
            Assert.Single(Directory.GetFiles(directory));
            Assert.Throws<InvalidDataException>(() => Call("KERNEL.#128", app, word, 0, 0x10fff, 20, path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
