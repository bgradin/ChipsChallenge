using System.Security.Cryptography;
using Translator.Binary;
using Translator.ChipsChallenge;
using Translator.CodeGen;
using Translator.IR;
using Win16_Runtime;

namespace Translator.Linker;

public sealed record NeLevelState(byte[] State, string BoardSha256, int PlayerX, int PlayerY, int TimeLimit, int Chips, string Title, string Password, byte[] Inventory);
/// <summary>Runs statically translated original loader/reset/init bodies; host supplies file bytes only.</summary>
public static class NeLevelInitialization
{
    public static readonly string[] Functions = ChipsLevelProfile.Functions;
    public const string DataHash = ChipsLevelProfile.DataHash;

    public static void ValidateData(byte[] data) => ChipsLevelProfile.ValidateData(data);
    public static NeLevelState Load(byte[] original, byte[] data, ushort levelNumber = 1)
    {
        if (levelNumber is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(levelNumber), "Only the audited LESSON 1/2 initialization is supported.");
        }

        original = original.ToArray();
        data = data.ToArray();
        ChipsLifecycleProfile.Validate(original);
        ValidateData(data);
        var contracts = BoundedChipsFileFixture.Contracts;
        var rejected = new Dictionary<string, Win16CodeAddress>
        {
            {
                "USER.#110",
                new(0xf000, 0x8000)
            }
        };
        var bindings = contracts.ToDictionary(p => p.Key, p => new Win16CodeAddress(0xf000, p.Value.EntryOffset));
        foreach (var b in rejected)
        {
            bindings.Add(b.Key, b.Value);
        }

        using var runtime = NeRuntimeProgram.Load(original, new(AllowIncompleteRelocations: true), nativeImportBindings: bindings);
        var artifact = NeNativeTranslation.Compile(original, "4:0A56", runtime.Image, nativeContracts: contracts, additionalRoots: Functions.Concat(levelNumber == 2
            ? new[] { "3:01A4", "3:0228" }
            : Array.Empty<string>()).ToArray(), includedFunctions: Functions.Concat(levelNumber == 2
            ? new[] { "3:01A4", "3:0228" }
            : Array.Empty<string>()).Select(NeBinaryAddress.Parse).ToHashSet(), rejectedImports: rejected, resolveIndirectTransfers: true);
        using var body = new NativeWin16Body(artifact.LinkedCode, artifact.EntryPoints);
        foreach (var f in artifact.Functions)
        {
            body.Register(runtime.Dispatcher, runtime.Image.Memory, f.Name, runtime.Image.Resolve(f.Address.SegmentIndex, f.Address.Offset), f.Distance, f.NativeSymbol);
        }

        var files = new BoundedChipsFileFixture(data);
        files.Register(runtime.Dispatcher);
        var m = runtime.Image.Memory;
        var originalDs = m.GetSegment(runtime.Image.InitialState.DS);
        var initial = originalDs.Memory.ToArray();
        m.Unmap(originalDs.Selector);
        using var expanded = m.CreateSegment(originalDs.Selector, 0x9000, initialized: initial);
        m.Map(expanded);
        try
        {
            ushort ds = expanded.Selector;
            m.WriteWord(ds, 0x1680, 0x4000);

            ushort Invoke(string address, params ushort[] arguments)
            {
                var state = runtime.Image.InitialState;
                state.SS = state.DS = ds;
                state.SP = 0x8000;
                foreach (var arg in arguments.Reverse())
                {
                    Win16Stack.Push(ref state, m, arg);
                }

                var function = artifact.Functions.Single(f => f.Address == NeBinaryAddress.Parse(address));
                runtime.Dispatcher.Call(ref state, m, function.Name, CallDistance.Far);
                if (state.SP != 0x8000 - arguments.Length * 2)
                {
                    throw new InvalidDataException("Original level initializer stack invariant failed.");
                }

                return state.AX;
            }


            Invoke("4:0320", 1);
            Invoke("3:1734", 0);
            Invoke("4:00D8", levelNumber);
            if (files.IsOpen || m.ReadWord(ds, 0x4800) != levelNumber)
            {
                throw new InvalidDataException("Original loader failed or leaked file ownership.");
            }

            Invoke("3:054C");
            byte[] stateBytes = m.ReadBytes(ds, 0x4000, 0xa42);

            ushort Word(int offset) => BitConverter.ToUInt16(stateBytes, offset);


            string Text(int offset) => System.Text.Encoding.ASCII.GetString(stateBytes.Skip(offset).TakeWhile(b => b != 0).ToArray());

            return new(stateBytes, Convert.ToHexString(SHA256.HashData(stateBytes.AsSpan(0, 0x800))).ToLowerInvariant(), Word(0x808), Word(0x80a), Word(0x804), Word(0x806), Text(0x95a), Text(0xa1a), m.ReadBytes(ds, 0x1682, 16));
        }
        finally
        {
            m.Unmap(expanded.Selector);
            m.Map(originalDs);
        }
    }
}
