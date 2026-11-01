# Runtime fixtures

These generated payloads and checkpoint API contracts support incremental
compiler/application acceptance tests. They are compiled into
`Translator.TestSupport`, not `Win16.Runtime`. The existing `Win16_Runtime`
namespace is retained for the fixture types so moving their assembly does not
require changing unrelated test callers.

`BoundedChipsFileFixture` supplies one injected, read-only `CHIPS.DAT` file with
a fixed handle. Production file services are implemented by `Win16TaskFiles`.

Regenerate payloads using `python3 src/Win16.Runtime/Native/rebuild.py` from the
repository root. Edit the native sources and contract JSON rather than generated
payload bytes. The production payload remains `NativeWholeTaskRuntime` in the
runtime project. Audit/report construction belongs to `Translator.Analysis`.
