# Native payload sources

The general repository conventions apply to C, headers and Python as well as C#.
Use clang-format 18 and the root `.clang-format` for native sources, keeping each
ternary condition and its two expressions on separate lines. Use Black 24 for
Python. Header generation and payload rebuilds require clang-format on `PATH`
alongside GCC and GNU binutils; ordinary .NET builds use the checked-in payloads.

`runtime.c` is the production whole-task translation unit. `runtime_common.h`
contains the shared machine, memory, control-flow, and service implementation.
`acceptance.c` adds the fixture and milestone entry points used by tests.

Run `python3 src/Win16.Runtime/Native/rebuild.py --production` from the repository
root to regenerate `src/Win16.Runtime/NativeWholeTaskRuntime.g.cs`. Run without
`--production` to regenerate all variants; the other nine payloads are written to
`tests/Translator.TestSupport/RuntimeFixtures`.

`Win16ExportContracts.json` supplies the original ABI evidence.
`Win16TaskServiceBindings.json` supplies service routes, and `Win16GuiPlans.json`
supplies scalar GUI adaptation plans. Run
`python3 src/Win16.Runtime/Native/generate_service_bindings.py` after changing
these inputs. Its `--check` option and the payload rebuild validate that both
`task_import_dispatch.h` and `task_gui_plans.h` match their inputs. Native GUI
argument widths and stack offsets come from the same export contracts used by
the managed adapters.

Managed services remain useful for in-process translation and recording-backend
tests; the native implementation supplies the standalone executable. Both paths
have consumers. Share contract metadata and behavioral cases across them rather
than treating either implementation as unused.

Run `verify_ne_native.py --output <directory>` for native contracts and causal
mutation checks. Run Windows .NET tests with `TEMP` and `TMP` pointing to the
repository's `bin/test-temp` directory so generated executables use that location.
