using Iced.Intel;
using Translator.Binary;

namespace Translator.Analysis;

public sealed record NeRegisterApiCallback(NeBinaryAddress Site, NeBinaryAddress Target, string Evidence);
/// <summary>Constant propagation over discovered original instructions, not a byte-pattern scan.
/// KERNEL.DOS3Call/INT 21h AH=25h registers DS:DX as an interrupt procedure.</summary>
internal static class NeRegisterApiCallbacks
{
    private sealed record Facts(ushort? Ax = null, ushort? Dx = null, int? Ds = null, int? AxSelector = null, int? StackSelector = null)
    {
        public Facts Meet(Facts other) => new(Ax == other.Ax
            ? Ax
            : null, Dx == other.Dx
            ? Dx
            : null, Ds == other.Ds
            ? Ds
            : null, AxSelector == other.AxSelector
            ? AxSelector
            : null, StackSelector == other.StackSelector
            ? StackSelector
            : null);
    }

    public static IReadOnlyList<NeRegisterApiCallback> Find(IReadOnlyList<NeDiscoveredFunction> functions, NeExecutable executable)
    {
        var rows = functions.SelectMany(f => f.Instructions).DistinctBy(i => i.Address).ToDictionary(i => i.Address);
        var decoded = rows.ToDictionary(p => p.Key, p =>
        {
            var d = Decoder.Create(16, new ByteArrayCodeReader(Convert.FromHexString(p.Value.BytesHex)));
            d.IP = p.Key.Offset;
            return d.Decode();
        });
        var imports = functions.SelectMany(f => f.Dependencies).Where(d => d.Import is not null).GroupBy(d => d.Site).ToDictionary(g => g.Key, g => g.Select(d => d.Import!).Distinct().ToArray());
        var states = new Dictionary<NeBinaryAddress, Facts>();
        var pending = new Queue<NeBinaryAddress>();
        var callbacks = new Dictionary<NeBinaryAddress, NeRegisterApiCallback>();
        var factory = new InstructionInfoFactory();

        void Next(NeBinaryAddress address, Facts facts)
        {
            if (!rows.ContainsKey(address))
            {
                return;
            }

            if (!states.TryGetValue(address, out var old))
            {
                states.Add(address, facts);
                pending.Enqueue(address);
            }
            else
            {
                var meet = old.Meet(facts);
                if (meet != old)
                {
                    states[address] = meet;
                    pending.Enqueue(address);
                }
            }
        }


        foreach (var function in functions)
        {
            Next(function.Address, new());
        }

        while (pending.TryDequeue(out var address))
        {
            var ins = decoded[address];
            var before = states[address];
            callbacks.Remove(address);
            bool dos = imports.TryGetValue(address, out var targets) && targets.Any(i => i.Module.Equals("KERNEL", StringComparison.OrdinalIgnoreCase) && (i.Ordinal == 102 || i.Name?.Equals("DOS3Call", StringComparison.OrdinalIgnoreCase) == true));
            dos |= ins.Mnemonic == Mnemonic.Int && ins.Immediate8 == 0x21;
            if (dos && before.Ax is { } ax && (ax >> 8) == 0x25 && before.Dx is { } dx && before.Ds is { } ds)
            {
                var target = new NeBinaryAddress(ds, dx);
                if (ds >= 1 && ds <= executable.Segments.Count && !executable.Segments[ds - 1].IsData && dx < executable.Segments[ds - 1].FileLength)
                {
                    callbacks[address] = new(address, target, "Verified DOS Set Interrupt Vector contract: original register dataflow proves AH=25h and code DS:DX; runtime instructions are not generated or interpreted");
                }
            }

            ushort? valueAx = before.Ax, valueDx = before.Dx;
            int? dsValue = before.Ds, axSelector = before.AxSelector, stack = null;
            var info = factory.GetInfo(ins);
            foreach (var used in info.GetUsedRegisters())
            {
                if (used.Access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite)
                {
                    if (used.Register.GetFullRegister() == Register.RAX)
                    {
                        valueAx = null;
                        axSelector = null;
                    }

                    if (used.Register.GetFullRegister() == Register.RDX)
                    {
                        valueDx = null;
                    }

                    if (used.Register == Register.DS)
                    {
                        dsValue = null;
                    }
                }
            }

            int? Segment(Register register) => register switch
            {
                Register.CS => address.SegmentIndex,
                Register.DS => before.Ds,
                Register.AX => before.AxSelector,
                _ => null
            };

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Immediate16)
            {
                if (ins.Op0Register == Register.AX)
                {
                    valueAx = ins.Immediate16;
                }

                if (ins.Op0Register == Register.DX)
                {
                    valueDx = ins.Immediate16;
                }

                var relocation = executable.Relocations.FirstOrDefault(r => r.SourceSegmentIndex == address.SegmentIndex && r.SourceOffsets.Any(o => o >= address.Offset && o < address.Offset + ins.Length));
                if (relocation is not null)
                {
                    if (ins.Op0Register == Register.AX)
                    {
                        valueAx = null;
                        if (relocation.Supported && !relocation.Additive && relocation.Import is null && relocation.RawAddressType == 2)
                        {
                            axSelector = relocation.TargetSegmentIndex;
                        }
                    }

                    if (ins.Op0Register == Register.DX)
                    {
                        valueDx = null;
                    }
                }
            }

            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Register)
            {
                if (ins.Op0Register == Register.AX)
                {
                    axSelector = Segment(ins.Op1Register);
                }

                if (ins.Op0Register == Register.DS)
                {
                    dsValue = Segment(ins.Op1Register);
                }
            }

            if (ins.Mnemonic == Mnemonic.Push && ins.Op0Kind == OpKind.Register)
            {
                stack = Segment(ins.Op0Register);
            }

            if (ins.Mnemonic == Mnemonic.Pop && ins.Op0Register == Register.DS)
            {
                dsValue = before.StackSelector;
            }

            var after = new Facts(valueAx, valueDx, dsValue, axSelector, stack);
            var fallthrough = new NeBinaryAddress(address.SegmentIndex, unchecked((ushort)(address.Offset + ins.Length)));
            if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            {
                Next(fallthrough, new());
            }
            else if (ins.FlowControl == FlowControl.ConditionalBranch)
            {
                Next(fallthrough, after);
                if (ins.Op0Kind == OpKind.NearBranch16)
                {
                    Next(new(address.SegmentIndex, ins.NearBranch16), after);
                }
            }
            else if (ins.FlowControl == FlowControl.UnconditionalBranch)
            {
                if (ins.Op0Kind == OpKind.NearBranch16)
                {
                    Next(new(address.SegmentIndex, ins.NearBranch16), after);
                }
            }
            else if (ins.FlowControl is FlowControl.Next or FlowControl.Interrupt)
            {
                Next(fallthrough, after);
            }
        }

        return callbacks.Values.OrderBy(c => c.Site).ToArray();
    }
}
