using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Translator.ChipsChallenge;
using Translator.Linker;
using Win16_Runtime;
using Xunit;

namespace Translator.Integration.Tests;

public sealed class NeApplicationTests
{
    private static byte[] Binary(params byte[] code)
    {
        var bytes = NeDiscoveryTests.Fixture(code.Length == 0
            ? [0xb8, 7, 0, 0x31, 0xd2, 0xcb]
            : code);
        NeLoaderTests.W(bytes, 0x92, 512); // declared stack, not an invented SP override
        return bytes;
    }

    private static NeApplicationProgram Build(byte[]? bytes = null, NeApplicationStartup? startup = null, NeRuntimeOptions? options = null) => NeApplicationProgram.Build(bytes ?? Binary(), startup ?? new("1:0", [], 7, IntervalMilliseconds: 20), runtimeOptions: options);

    [Fact]
    public void GuiLifecyclePackagingIsDeterministicAndSeparateFromConsoleAcceptance()
    {
        var first = Build();
        var second = Build();
        Assert.Equal(first.Executable, second.Executable);
        Assert.Equal(JsonSerializer.Serialize(first.Manifest), JsonSerializer.Serialize(second.Manifest));
        using var pe = new PEReader(new MemoryStream(first.Executable));
        Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader!.Subsystem);
        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        Assert.Equal(PEMagic.PE32Plus, pe.PEHeaders.PEHeader.Magic);
        Assert.False(pe.HasMetadata);
        Assert.Null(pe.PEHeaders.CorHeader);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.Executable)).ToLowerInvariant(), first.Manifest.PeSha256);
        Assert.Equal(0x3541504eu, BinaryPrimitives.ReadUInt32LittleEndian(first.Descriptor));
        Assert.Contains(first.Imports, i => i.Name == "GetMessageW");
        Assert.Contains(first.Imports, i => i.Name == "PostQuitMessage");
        Assert.DoesNotContain(first.Imports, i => i.Name == "CreateWindowExW");
        var console = NeStandaloneProgram.Build(Binary(), "1:0", """{"Arguments":[],"ExpectedRegisters":{"AX":7}}""");
        using var oldPe = new PEReader(new MemoryStream(console.Executable));
        Assert.Equal(Subsystem.WindowsCui, oldPe.PEHeaders.PEHeader!.Subsystem);
        Assert.Contains(first.Manifest.DeferredCapabilities, x => x.Contains("WinMain"));
    }

    [Fact]
    public void RejectsUnsupportedStartupContractsLimitsAndMismatchedGameIdentity()
    {
        Assert.Throws<ArgumentException>(() => Build(startup: new("1:0", [], 7, Invocations: 1)));
        Assert.Throws<ArgumentException>(() => Build(startup: new("1:0", [], 7, IntervalMilliseconds: 0)));
        Assert.Throws<NotSupportedException>(() => Build(options: new(NeMemoryBackend.Managed)));
        Assert.Throws<NotSupportedException>(() => Build(Binary(0xc3)));
        var tooSmall = Binary();
        NeLoaderTests.W(tooSmall, 0x92, 16);
        Assert.Throws<NotSupportedException>(() => Build(tooSmall));
        Assert.Throws<InvalidDataException>(() => ChipsLifecycleProfile.Validate(Binary()));
        Assert.Throws<NotSupportedException>(() => Build(Binary(0xca, 2, 0)));
    }

    [WindowsFact]
    public void NativeLifecycleRetainsOriginalDataAndTaskAcrossRealThreadMessages()
    {
        Assert.Equal(0, Execute(Build().Executable));
        // Read the task's startup show word through original synthetic instructions.
        var showState = Binary(0xb8, 0, 0xe0, 0x8e, 0xc0, 0x26, 0xa1, 2, 0, 0x31, 0xd2, 0xcb);
        Assert.Equal(0, Execute(Build(showState, new("1:0", [], 0, IntervalMilliseconds: 20)).Executable, ProcessWindowStyle.Hidden));
        // INC word [0]; MOV AX,[0]; XOR DX,DX; RETF. Each original invocation
        // mutates the same DS mapping; resetting the runtime per message fails the oracle.
        var persistent = Binary(0xff, 0x06, 0, 0, 0xa1, 0, 0, 0x31, 0xd2, 0xcb);
        Assert.Equal(0, Execute(Build(persistent, new("1:0", [], 0x8181, 1, 4, 20)).Executable));
        Assert.Equal(40, Execute(Build(persistent, new("1:0", [], 0x8181, 0, 4, 20)).Executable));
        persistent[0x400] ^= 1; // source initialized data mutation
        Assert.Equal(40, Execute(Build(persistent, new("1:0", [], 0x8181, 1, 4, 20)).Executable));
    }

    [WindowsFact]
    public void NativeLifecycleDetectsDescriptorCodeBindingStackAndPartialInitializationFailures()
    {
        var program = Build();
        Assert.Equal(40, Execute(Build(startup: new("1:0", [], 8, IntervalMilliseconds: 20)).Executable));
        var changed = Binary();
        changed[0x201] = 8;
        Assert.Equal(40, Execute(Build(changed).Executable));
        foreach (int offset in new[]
        {
            0,
            4,
            8,
            12,
            24,
            44,
            79,
            80,
            92,
            94,
            96,
            98,
            100
        }

        )
        {
            byte[] pe = MutateDescriptor(program, offset, value => (byte)(value ^ 0xff));
            Assert.Equal(10, Execute(pe));
        }

        // Limit permits one segment but not the image/task/bindings: cleanup must preserve exit 20, not 50.
        byte[] limited = program.Executable.ToArray();
        int at = Find(limited, program.Descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(limited.AsSpan(at + 52), 1);
        Assert.Equal(20, Execute(limited));
        // Binding selector changed to an unmapped selector. Segment serialization is fixed at the descriptor boundary.
        int binding = BindingOffset(program.Descriptor);
        Assert.Equal(10, Execute(MutateDescriptor(program, binding, _ => 0xff)));
        var bounds = Binary(0xbb, 0xff, 0xff, 0x8b, 0x07, 0x31, 0xd2, 0xcb);
        Assert.Equal(30, Execute(Build(bounds).Executable));
    }

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [WindowsFact]
    public void PrematureQuitReleasesPersistentOwnerAndFailsTheLifetimeOracle()
    {
        var program = Build(startup: new("1:0", [], 7, Invocations: 10, IntervalMilliseconds: 100));
        string directory = Path.Combine(Path.GetTempPath(), "application-quit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "lifecycle.exe");
        try
        {
            File.WriteAllBytes(path, program.Executable);
            using var process = Process.Start(new ProcessStartInfo(path, "--lifecycle-test") { UseShellExecute = false, WorkingDirectory = directory })!;
            try
            {
                bool posted = false;
                for (int i = 0; i < 100 && !process.HasExited; i++)
                {
                    process.Refresh();
                    if (process.Threads.Count > 0 && PostThreadMessage((uint)process.Threads[0].Id, 0x12, 0, 0))
                    {
                        posted = true;
                        break;
                    }

                    Thread.Sleep(5);
                }

                Assert.True(posted, "The native UI thread did not establish its message queue.");
                Assert.True(process.WaitForExit(10000), "Early WM_QUIT did not terminate the lifecycle.");
                Assert.Equal(60, process.ExitCode);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
        }
        finally
        {
            DeleteProcessDirectory(directory);
        }
    }

    [WindowsSuppliedNeFact]
    public void OriginalDigitInitializationHelperRunsAsClrFreeGuiWithoutSourceOrResources()
    {
        var original = File.ReadAllBytes(Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!);
        ChipsLifecycleProfile.Validate(original);
        var program = NeApplicationProgram.Build(original, new(ChipsLifecycleProfile.Root, ChipsLifecycleProfile.Arguments, ChipsLifecycleProfile.ExpectedDxAx, Invocations: 4, IntervalMilliseconds: 50));
        Assert.Single(program.Manifest.Translation.Functions);
        Assert.Equal("0009:0028", program.Manifest.Translation.Graph.Root.Identity);
        Assert.Equal(0, Execute(program.Executable));
        Assert.Equal(40, Execute(NeApplicationProgram.Build(original, new(ChipsLifecycleProfile.Root, ChipsLifecycleProfile.Arguments, 277)).Executable));
        var changed = original.ToArray();
        changed[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => ChipsLifecycleProfile.Validate(changed));
    }

    private static byte[] MutateDescriptor(NeApplicationProgram program, int offset, Func<byte, byte> mutation)
    {
        var pe = program.Executable.ToArray();
        int at = Find(pe, program.Descriptor);
        Assert.True(at >= 0);
        pe[at + offset] = mutation(pe[at + offset]);
        return pe;
    }

    private static int BindingOffset(byte[] descriptor)
    {
        int at = 148 + (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(100));
        int segments = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16));
        for (int i = 0; i < segments; i++)
        {
            at += 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(at + 8));
        }

        return at;
    }

    private static int Find(byte[] bytes, byte[] value) => bytes.AsSpan().IndexOf(value);

    // WaitForExit confirms process termination, but Windows may briefly retain an image
    // section/file handle. Retry only transient Windows deletion errors on our owned directory;
    // persistent permission/security failures must still fail rather than being ignored.
    internal static void DeleteProcessDirectory(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 7 && error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) is 5 or 32 or 33 or 145)
            {
                Thread.Sleep(Math.Min(10 << attempt, 250));
            }
        }
    }

    [WindowsFact]
    public void ProcessDirectoryCleanupWaitsForATransientWindowsFileLock()
    {
        string directory = Path.Combine(Path.GetTempPath(), "application-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var held = new FileStream(Path.Combine(directory, "lifecycle.exe"), FileMode.Create, FileAccess.Write, FileShare.Read);
        var release = new Thread(() =>
        {
            Thread.Sleep(100);
            held.Dispose();
        });
        release.Start();
        try
        {
            DeleteProcessDirectory(directory);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            release.Join();
            held.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static int Execute(byte[] bytes, ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal, string? originalPath = null, IReadOnlyList<string>? additionalArguments = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "application-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "lifecycle.exe");
        try
        {
            File.WriteAllBytes(path, bytes);
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
                WindowStyle = windowStyle
            };
            start.ArgumentList.Add("--lifecycle-test");
            if (originalPath is not null)
            {
                start.ArgumentList.Add("--original");
                start.ArgumentList.Add(originalPath);
            }

            if (additionalArguments is not null)
            {
                foreach (var argument in additionalArguments)
                {
                    start.ArgumentList.Add(argument);
                }
            }

            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("Native GUI lifecycle timed out.");
            }

            return process.ExitCode;
        }
        finally
        {
            DeleteProcessDirectory(directory);
        }
    }
}
