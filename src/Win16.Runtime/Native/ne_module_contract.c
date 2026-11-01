#include "acceptance.c"
/* A real packed MCI_OPEN bridge call under AddressSanitizer. The native test
 * provider returns an ordinary WinMM error, independent of hardware/assets. */

static ABI iptr module_probe_open(iptr device, iptr command, iptr flags, iptr parameters)
{
    if (device || command != 0x803 || flags != 0x1000)
    {
        return 999;
    }
    const u8 *bytes = (const u8 *)parameters;
    for (u32 i = 0; i < 36; i++)
    {
        if (bytes[i] != (i == 12
            ? 0x0b
            : i == 13
                ? 0x02
                : 0))
        {
            return 998;
        }
    }
    return 275;
}

int main(void)
{
    static FixtureStorage storage;
    init_storage(&storage);
    Runtime r;
    init_runtime(&r, &storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    if (!map(&r, 0x1000, 8192))
    {
        return 1;
    }
    TaskGui gui;
    init_task_gui(&gui, 0, 0);
    TaskUi ui;
    zero(&ui, sizeof(ui));
    ui.runtime = &r;
    gui.ui = &ui;
    r.gui = &gui;
    TaskModules modules;
    zero(&modules, sizeof(modules));
    modules.ui = &ui;
    modules.native = 1;
    modules.references = 1;
    modules.procedures[3] = (iptr)module_probe_open;
    r.modules = &modules;
    __win16_write_dword(&r, 0x1000, 72, 523);
    UiArena arena = {&r, 0};
    u32 result = mm_mci(&modules, &arena, 0, 0x803, 0x1000, 0x10000040);
    int failure = r.error || result != 275 || modules.devices
        ? 2
        : 0;
    ui_destroy_arena(&arena);
    r.gui = 0;
    r.modules = 0;
    destroy_runtime(&r);
    return failure;
}
