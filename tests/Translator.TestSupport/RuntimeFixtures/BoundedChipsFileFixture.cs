using System.Text;
using Translator.IR;

namespace Win16_Runtime;

/// <summary>Read-only bounded file/ANSI compatibility; no container or level decoding.
/// Contracts: Wine krnl386.exe16.spec ordinals 74,81,82,84,88; original Pascal call sites.</summary>
public sealed class BoundedChipsFileFixture(byte[] bytes)
{
    private bool _open;
    private int _position;
    public bool IsOpen => _open;
    public static IReadOnlyDictionary<string, Win16ApiMetadata> Contracts
    {
        get;
    } = new[]
    {
        Api(74, "OpenFile", 0x300, Win16ValueType.SignedWord, new Win16ApiArgument("name", Win16ValueType.FarPointer, 3), new Win16ApiArgument("ofstruct", Win16ValueType.FarPointer, 1), new Win16ApiArgument("style", Win16ValueType.Word, 0)),
        Api(81, "_lclose", 0x304, Win16ValueType.SignedWord, new Win16ApiArgument("handle", Win16ValueType.Handle, 0)),
        Api(82, "_lread", 0x308, Win16ValueType.Word, new Win16ApiArgument("handle", Win16ValueType.Handle, 3), new Win16ApiArgument("buffer", Win16ValueType.FarPointer, 1), new Win16ApiArgument("count", Win16ValueType.Word, 0)),
        Api(84, "_llseek", 0x30c, Win16ValueType.SignedDword, new Win16ApiArgument("handle", Win16ValueType.Handle, 3), new Win16ApiArgument("offset", Win16ValueType.Dword, 1), new Win16ApiArgument("origin", Win16ValueType.Word, 0)),
        Api(88, "lstrcpy", 0x310, Win16ValueType.FarPointer, new Win16ApiArgument("destination", Win16ValueType.FarPointer, 2), new Win16ApiArgument("source", Win16ValueType.FarPointer, 0))
    }.ToDictionary(a => $"KERNEL.#{a.Ordinal}");

    private static Win16ApiMetadata Api(ushort ordinal, string name, ushort offset, Win16ValueType result, params Win16ApiArgument[] args) => new((ushort)(300 + ordinal), "KERNEL", name, ordinal, Win16ImportKind.Procedure, Win16ApiConvention.Pascal, Win16ArgumentOrder.LeftToRight, result, Win16Marshalling.StackValues, true, true, offset, args);
    public void Register(Win16CallDispatcher dispatcher)
    {
        foreach (var api in Contracts.Values)
        {
            dispatcher.RegisterRuntimeService(api.Identity.CanonicalName, new(0xf000, api.EntryOffset), CallDistance.Far, (ref MachineState s, SegmentMemory m, Win16CallDispatcher _, Win16CallContext call) =>
            {
                var saved = s;

                uint Arg(string n) => api.ReadArgument(n, m, saved, call);

                uint result = 0;
                if (api.Name == "OpenFile")
                {
                    if (_open || Arg("style") != 0x20 || !String(m, Arg("name")).Equals("CHIPS.DAT", StringComparison.OrdinalIgnoreCase))
                    {
                        result = 0xffff;
                    }
                    else
                    {
                        uint p = Arg("ofstruct");
                        m.WriteBytes((ushort)(p >> 16), (ushort)p, new byte[136]);
                        m.WriteByte((ushort)(p >> 16), (ushort)p, 136);
                        _open = true;
                        _position = 0;
                        result = 1;
                    }
                }
                else if (api.Name == "lstrcpy")
                {
                    uint source = Arg("source"), destination = Arg("destination");
                    var text = Encoding.Latin1.GetBytes(String(m, source) + "\0");
                    m.WriteBytes((ushort)(destination >> 16), (ushort)destination, text);
                    result = destination;
                }
                else if (!_open || Arg("handle") != 1)
                {
                    result = api.Name == "_llseek"
                        ? uint.MaxValue
                        : 0xffff;
                }
                else if (api.Name == "_lclose")
                {
                    _open = false;
                    result = 0;
                }
                else if (api.Name == "_llseek")
                {
                    long at = (Arg("origin") switch
                    {
                        0 => 0,
                        1 => _position,
                        2 => bytes.Length,
                        _ => long.MinValue
                    }) + unchecked((int)Arg("offset"));
                    if (at < 0 || at > bytes.Length)
                    {
                        result = uint.MaxValue;
                    }
                    else
                    {
                        _position = (int)at;
                        result = (uint)at;
                    }
                }
                else
                {
                    int count = Math.Min((int)Arg("count"), bytes.Length - _position);
                    uint dest = Arg("buffer");
                    m.WriteBytes((ushort)(dest >> 16), (ushort)dest, bytes.AsSpan(_position, count));
                    _position += count;
                    result = (uint)count;
                }

                s.AX = (ushort)result;
                s.DX = (ushort)(result >> 16);
                return api.CleanupBytes;
            });
        }
    }

    private static string String(SegmentMemory memory, uint pointer)
    {
        var text = new List<byte>();
        for (int i = 0; i < 256; i++)
        {
            if ((ushort)pointer + i > 65535)
            {
                break;
            }

            byte b = memory.ReadByte((ushort)(pointer >> 16), (ushort)(pointer + i));
            if (b == 0)
            {
                return Encoding.Latin1.GetString(text.ToArray());
            }

            text.Add(b);
        }

        throw new InvalidDataException("Unterminated or excessive bounded ANSI string.");
    }
}
