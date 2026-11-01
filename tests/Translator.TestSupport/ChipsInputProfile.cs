namespace Translator.ChipsChallenge;

public enum InputDriverOperation : ushort
{
    Message,
    AdvanceTicks,
    AdvanceMilliseconds,
    CallOriginal,
    ProbeNestedTimer,
    ProbeTimerContracts
}

public sealed record InputEvent(ushort Message, ushort Key, uint Lparam, ushort Window = 0, InputDriverOperation Operation = InputDriverOperation.Message);
public sealed record InputFarCheck(ushort PointerOffset, byte[] Bytes);
public sealed record InputModalPlan(string Text, IReadOnlyList<InputCheck> Checks, IReadOnlyList<InputFarCheck> FarChecks, IReadOnlyList<ScenePixels> Pixels);
public sealed record InputWindowCheck(ushort Window, ushort Index, ushort Value);
public sealed record InputCheck(ushort Offset, byte[] Bytes);
public sealed record InputCase(string Name, IReadOnlyList<SceneWrite> InitialWrites, IReadOnlyList<InputEvent> Events, IReadOnlyList<InputCheck> Checks)
{
    public IReadOnlyList<string>? Completion
    {
        get; init;
    }
    public InputModalPlan? Modal
    {
        get; init;
    }
    public IReadOnlyList<InputFarCheck> FarChecks { get; init; } = [];
    public IReadOnlyList<InputWindowCheck> WindowChecks { get; init; } = [];
    public IReadOnlyList<ScenePixels> Pixels { get; init; } = [];
}

public sealed record InputWindowBinding(ushort Offset, ushort Window);
public sealed record ChipsInputPlan(string Procedure, ushort Window, ushort FocusWindow, IReadOnlyList<InputWindowBinding> WindowBindings, IReadOnlyList<ushort> Keys, IReadOnlyList<SceneWrite> InitialWrites, IReadOnlyList<InputCase> Cases)
{
    public bool Movement
    {
        get; init;
    }
    public string Policy => Movement
        ? "Original MAINWNDPROC, MoveChip, BOARDWNDPROC and DoTick own input consumption, movement, collisions and painting; seeded fixtures only in automation"
        : "Original MAINWNDPROC -> MoveChip buffers the first voluntary arrow when ChipHasMoved=1. Key-up and later arrows preserve that buffer. No timer, player movement or gameplay is claimed.";
}

/// <summary>Original input ABI and authored acceptance vectors; native code never computes a game direction.</summary>
public static class ChipsInputProfile
{
    public static string[] Functions => ChipsSceneProfile.Functions.Concat(new[] { "0002:225C", "0007:1184" }).ToArray();

    public static ChipsInputPlan Inspect()
    {
        SceneWrite Word(ushort offset, ushort value, string why) => new(offset, [(byte)value, (byte)(value >> 8)], why);


        InputCheck Check(ushort offset, ushort value) => new(offset, [(byte)value, (byte)(value >> 8)]);

        SceneWrite[] initial = [Word(0x22, 0, "Enable original MoveChip input gate; no timer is installed"), Word(0x4a40, 1, "GameState.ChipHasMoved selects the original buffered-input branch"), Word(0x480e, 0, "IsBuffered initially clear"), Word(0x4812, 0x1234, "BufferedX sentinel"), Word(0x4814, 0x5678, "BufferedY sentinel")];
        ushort[] keys = [0x25, 0x26, 0x27, 0x28];
        var vectors = new[]
        {
            ("Left", 0xffff, 0, 0x4b),
            ("Up", 0, 0xffff, 0x48),
            ("Right", 1, 0, 0x4d),
            ("Down", 0, 1, 0x50)
        };
        var cases = vectors.Select((v, i) => new InputCase(v.Item1, initial, new InputEvent[] { new(0x100, keys[i], (uint)(v.Item4 << 16) | 1), new(0x101, keys[i], 0xc0000001u | (uint)(v.Item4 << 16)), new(0x100, keys[(i + 1) % 4], 0x40000001), new(0x101, keys[(i + 1) % 4], 0xc0000001), new(0x100, keys[i], 0x40000002), new(0x101, keys[i], 0xc0000001) }, new[] { Check(0x480e, 1), Check(0x4812, (ushort)v.Item2), Check(0x4814, (ushort)v.Item3), Check(0x4808, 4), Check(0x480a, 4), Check(0x4a40, 1) })).ToArray();
        return new("0002:225C", 0, 0, new InputWindowBinding[] { new(0x10, 0), new(0x12, 1) }, keys, initial, cases);
    }
}
