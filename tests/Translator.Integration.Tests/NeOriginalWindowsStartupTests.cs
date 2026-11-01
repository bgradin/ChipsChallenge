using System.Runtime.InteropServices;
using Translator.Linker;
using Win16_Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Translator.Integration.Tests;

[Collection("Native modal desktop")]
public sealed class NeOriginalWindowsStartupTests(ITestOutputHelper output)
{
    private sealed class Backend : IWin16GuiBackend, IDisposable
    {
        private readonly WindowsTaskGuiBackend _native = new();
        private nint _root;
        private int _polls, _command;
        private bool _closing;
        private readonly ushort[] _commands = [0x64, 0x73, 0x77, 0x7a, 0x75, 0x76, 0x74, 0x74, 0x71];
        public bool ExerciseFeatures
        {
            get; init;
        }
        public int DialogsClosed
        {
            get; private set;
        }

        public readonly List<ushort> CommandsPosted = [];
        public readonly List<string> Calls = [];

        public nint Call(string module, string name, ReadOnlySpan<nint> args)
        {
            Calls.Add(name);
            // External verification action only: close the original top-level window
            // after its genuine message loop has had time to initialize and paint.
            if (name == "PeekMessageW" && _root != 0 && !_closing && ++_polls % 10 == 0)
            {
                if (ExerciseFeatures && _command < _commands.Length)
                {
                    ushort id = _commands[_command++];
                    CommandsPosted.Add(id);
                    _native.Call("USER32", "PostMessageW", [_root, 0x111, id, 0]);
                }
                else
                {
                    _closing = true;
                    _native.Call("USER32", "PostMessageW", [_root, 0x10, 0, 0]);
                }
            }

            if (name == "DialogBoxIndirectParamW")
            {
                var callback = Marshal.GetDelegateForFunctionPointer<Win16TaskWindowHost.WindowProcedure>(args[3]);
                Win16TaskWindowHost.WindowProcedure automation = (window, message, wParam, lParam) =>
                {
                    nint answer = callback(window, message, wParam, lParam);
                    if (message == 0x110)
                    {
                        DialogsClosed++;
                        _native.Call("USER32", "PostMessageW", [window, 0x111, 2, 0]);
                    }

                    return answer;
                };
                var converted = args.ToArray();
                converted[3] = Marshal.GetFunctionPointerForDelegate(automation);
                nint dialogResult = _native.Call(module, name, converted);
                GC.KeepAlive(automation);
                return dialogResult;
            }

            nint result = _native.Call(module, name, args);
            if (name == "CreateWindowExW" && args[8] == 0 && result != 0 && _root == 0)
            {
                _root = result;
            }

            return result;
        }

        public void Dispose() => _native.Dispose();
    }

    private sealed class Multimedia : IWin16MultimediaBackend
    {
        private readonly WindowsTaskMultimediaBackend _backend = new();
        private readonly string _log;
        public readonly List<string> Procedures = [];
        public readonly List<uint> MciResults = [];

        public Multimedia(string log)
        {
            _log = log;
        }

        public bool Load() => _backend.Load();

        public bool HasExport(string name) => _backend.HasExport(name);

        public nint Call(string name, ReadOnlySpan<nint> args)
        {
            File.AppendAllText(_log, name + " " + string.Join(",", args.ToArray().Select(a => a.ToString("X"))) + Environment.NewLine);
            Procedures.Add(name);
            nint result = _backend.Call(name, args);
            if (name == "mciSendCommandW")
            {
                MciResults.Add(unchecked((uint)(nuint)result));
            }

            return result;
        }

        public void Unload() => _backend.Unload();

        public void Dispose() => _backend.Dispose();
    }

    [WindowsSuppliedNeFact]
    public void OriginalStartupWithAllServiceProvidersClosesThroughOriginalShutdown() => Run(false);

    [WindowsSuppliedNeFact]
    public void OriginalMenusDialogsAudioTogglesPauseAndRestartRunThroughWholeTaskServices() => Run(true);
    private void Run(bool features)
    {
        string source = Environment.GetEnvironmentVariable("CHIPS_NE_INPUT")!, folder = Path.GetDirectoryName(source)!;
        string directory = Path.Combine(Path.GetTempPath(), "whole-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(folder, "CHIPS.DAT"), Path.Combine(directory, "CHIPS.DAT"));
            foreach (string file in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p).ToUpperInvariant() is ".WAV" or ".MID" or ".HLP"))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            using var backend = new Backend
            {
                ExerciseFeatures = features
            };
            string log = Path.Combine(Environment.GetEnvironmentVariable("CHIPS_SERVICE_EVIDENCE") ?? directory, "multimedia-calls.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            using var multimedia = new Multimedia(log);
            using var program = new NeWholeProgramControlProgram(File.ReadAllBytes(source), initializeKernelServices: true, workingDirectory: directory, guiBackend: backend, maximumTransfers: 2000000, taskServicesOptions: new(File.ReadAllBytes(Path.Combine(folder, "WEP4UTIL.DLL")), Path.Combine(directory, "CHIPS.EXE"), multimedia, WindowsDirectory: directory));
            var state = program.InitialState;
            Exception? failure = Record.Exception(() => program.EnterStartup(ref state));
            output.WriteLine("Unique imports: " + string.Join(",", program.ServiceCalls.Distinct().Order()));
            output.WriteLine("Unique Windows APIs: " + string.Join(",", backend.Calls.Distinct().Order()));
            output.WriteLine("Outcome: " + failure);
            output.WriteLine("Commands: " + string.Join(",", backend.CommandsPosted) + "; dialogs closed: " + backend.DialogsClosed);
            Assert.NotNull(failure);
            Assert.IsType<Win16TaskExitException>(failure.GetBaseException());
            Assert.True(program.LifecycleServices!.Terminated);
            Assert.Contains("USER.#5", program.ServiceCalls);
            Assert.Contains("CreateWindowExW", backend.Calls);
            Assert.Equal((ushort)0, program.LifecycleServices.ExitCode);
            Assert.Equal(0, program.FileServices!.OpenCount);
            if (features)
            {
                Assert.Equal(9, backend.CommandsPosted.Count);
                Assert.True(backend.DialogsClosed >= 3);
            }

            string? evidence = Environment.GetEnvironmentVariable("CHIPS_SERVICE_EVIDENCE");
            if (evidence is not null)
            {
                Directory.CreateDirectory(evidence);
                var result = new
                {
                    SourceSha256 = program.Artifact.Report.ExecutableSha256,
                    NativeCodeSha256 = program.Artifact.Report.NativeCodeSha256,
                    WindowsHost = true,
                    OriginalCodeExercised = true,
                    ExecutionMode = "Managed verification host",
                    Features = features,
                    SourceAdaptersBound = program.InstalledImports.Count,
                    Imports = program.ServiceCalls.GroupBy(x => x).OrderBy(g => g.Key).Select(g => new { Identity = g.Key, Calls = g.Count() }).ToArray(),
                    NativeApis = backend.Calls.Distinct().Order().ToArray(),
                    DynamicProcedures = multimedia.Procedures.Distinct().Order().ToArray(),
                    MciResults = multimedia.MciResults,
                    Commands = backend.CommandsPosted,
                    backend.DialogsClosed,
                    ExitCode = program.LifecycleServices.ExitCode,
                    ApplicationAccepted = false
                };
                File.WriteAllText(Path.Combine(evidence, features
                    ? "original-features.json"
                    : "original-startup.json"), System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            NeApplicationTests.DeleteProcessDirectory(directory);
        }
    }
}
