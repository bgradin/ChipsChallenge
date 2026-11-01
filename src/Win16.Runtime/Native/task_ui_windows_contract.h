typedef struct
{
    TaskUi *ui;
    UiWindow *window;
    u32 calls, ended, labels, freed, waits, failed;
} UiWindowFixture;

static ABI u16 ui_fixture_callback(Runtime *r, State *s)
{
    for (u16 i = 0; i < 5; i++)
    {
        __win16_write_word(r, s->ds, (u16)(128 + i * 2),
                           __win16_read_word(r, s->ss, (u16)(s->sp + 4 + i * 2)));
    }
    s->ax = 0x5678;
    s->dx = 0x1234;
    __win16_flow_transfer(r, s, 10, 5);
    return 0;
}

static ABI iptr fixture_mm_count(void)
{
    return 2;
}

static ABI iptr fixture_mm_sound(iptr text, iptr flags)
{
    return 1;
}

static ABI iptr fixture_mm_mci(iptr device, iptr command, iptr flags, iptr pointer)
{
    if (command == 0x803)
    {
        ui_put32((u8 *)pointer + 8, 0x12345678);
        return 0;
    }
    if (command == 0x814 && device == 0x12345678)
    {
        *(iptr *)((u8 *)pointer + 8) = 42;
        return 0;
    }
    return command == 0x804 || command == 0x806
        ? 0
        : 261;
}

static iptr fixture_ui_windows(void *context, u16 function, const iptr *a, u16 count)
{
    UiWindowFixture *f = context;
    f->calls++;
    if (function == UI_GetModuleHandleW)
    {
        return 0x4444;
    }
    if (function == UI_CreateWindowExW)
    {
        f->window = (UiWindow *)(void *)a[11];
        if (ui_window_callback(f->window, 0x123456789LL, 0x81, 0, 0) != 0x12345678)
        {
            return 0;
        }
        return 0x123456789LL;
    }
    if (function == UI_DestroyWindow)
    {
        UiWindow *window = ui_window(f->ui, a[0]);
        if (window)
        {
            ui_window_callback(window, a[0], 0x82, 0, 0);
        }
        return 1;
    }
    if (function == UI_BeginPaint)
    {
        u8 *paint = (u8 *)a[1];
        *(iptr *)paint = 0x323456789LL;
        ui_put32(paint + 8, 1);
        ui_put32(paint + 20, 40);
        ui_put32(paint + 24, 50);
        paint[40] = 0xab;
        return 0x323456789LL;
    }
    if (function == UI_EndPaint)
    {
        return ((u8 *)a[1])[40] == 0xab;
    }
    if (function == UI_GetDC)
    {
        return 0x423456789LL;
    }
    if (function == UI_CreateMenu || function == UI_CreatePopupMenu)
    {
        return 0x523456789LL + f->calls;
    }
    if (function == UI_GetWindowRect)
    {
        int *rect = (int *)a[1];
        rect[0] = rect[1] = 0;
        rect[2] = 200;
        rect[3] = 100;
        return 1;
    }
    if (function == UI_GetSystemMetrics)
    {
        return a[0]
            ? 600
            : 800;
    }
    if (function == UI_SetWindowPos)
    {
        if (a[2] != 296 || a[3] != 166 || a[6] != 0x11)
        {
            f->failed = 1;
        }
        return !f->failed;
    }
    if (function == UI_GetStockObject)
    {
        return 0x623456789LL;
    }
    if (function == UI_LoadLibraryExW)
    {
        return 0x723456789LL;
    }
    if (function == UI_GetProcAddress)
    {
        const u8 *name = (const u8 *)(void *)a[1];
        if (mm_equals(name, "mciSendCommandW", 0))
        {
            return (iptr)fixture_mm_mci;
        }
        if (mm_equals(name, "sndPlaySoundW", 0))
        {
            return (iptr)fixture_mm_sound;
        }
        return (iptr)fixture_mm_count;
    }
    if (function == UI_WaitForSingleObject)
    {
        f->waits++;
        return 0;
    }
    if (function == UI_SetDlgItemTextW)
    {
        f->labels++;
        return 1;
    }
    if (function == UI_EndDialog)
    {
        f->ended++;
        return 1;
    }
    if (function == UI_DestroyIcon)
    {
        f->freed++;
        return 1;
    }
    return 1;
}

static int task_ui_windows_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 65536))
    {
        return 90;
    }
    s.ds = s.ss = 0x1000;
    s.cs = 0x4000;
    s.sp = 4096;
    s.flags = 0x201;
    s.high[0] = 0xabcd;
    s.high[3] = 0xeeee;
    LocalHeap heap;
    if (!local_init(&r, &heap, s.ds, 8192, 49152))
    {
        return 91;
    }
    r.local = &heap;
    ProgramBinding bindings[] = {{0x4000, 0x1000, 1, ui_fixture_callback}};
    ProgramImage program = {bindings, 1, 100, 16, 0, 0};
    r.program = &program;
    TaskGui gui;
    init_task_gui(&gui, 0, 0);
    r.gui = &gui;
    TaskUi *ui = r.alloc(r.storage, sizeof(*ui));
    if (!ui)
    {
        return 92;
    }
    zero(ui, sizeof(*ui));
    gui.ui = ui;
    ui->runtime = &r;
    ui->state = &s;
    ui->instance = s.ds;
    UiWindowFixture fixture = {0};
    fixture.ui = ui;
    ui->context = &fixture;
    ui->call = fixture_ui_windows;
    const u8 name[] = "TaskClass";
    for (u32 i = 0; i < sizeof(name); i++)
    {
        __win16_write_byte(&r, s.ds, (u16)(512 + i), name[i]);
    }
    u8 *cls = ui_buffer(&r, 0x10000100, 26, 1);
    zero(cls, 26);
    ui_put32(cls + 2, 0x40001000);
    ui_put16(cls + 8, 4);
    ui_put16(cls + 10, s.ds);
    ui_put32(cls + 22, 0x10000200);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000100);
    if (__win16_ui_window(&r, &s, 57) != 4 || s.ax != 1 || !ui->classes)
    {
        return 93;
    }
    const UiContract *create = ui_contract(2, 41);
    u32 args[] = {0x10000200, 0, 0, 1, 2, 40, 50, 0, 0, 0x1000, 0xdeadbeef};
    for (u32 i = 0; i < create->count; i++)
    {
        u16 at = (u16)(s.sp + 4 + create->words[i] * 2);
        if (create->widths[i] == 4)
        {
            __win16_write_dword(&r, s.ss, at, args[i]);
        }
        else
        {
            __win16_write_word(&r, s.ss, at, (u16)args[i]);
        }
    }
    if (__win16_ui_window(&r, &s, 41) != 30 || !s.ax || !fixture.window || r.error)
    {
        return 94;
    }
    u16 window = s.ax, child = ui_handle_add(ui, 0x223456789LL, 5, 0, window);
    State saved = s;
    if (ui_window_callback(fixture.window, fixture.window->native, 0x111, 100 | (7u << 16),
                           0x223456789LL) != 0x12345678 ||
        r.error || s.sp != saved.sp || s.ss != saved.ss || s.ax != saved.ax ||
        s.high[0] != saved.high[0] || heap.count)
    {
        return 95;
    }
    if (__win16_read_word(&r, s.ds, 128) != child || __win16_read_word(&r, s.ds, 130) != 7 ||
        __win16_read_word(&r, s.ds, 132) != 100 || __win16_read_word(&r, s.ds, 134) != 0x111 ||
        __win16_read_word(&r, s.ds, 136) != window)
    {
        return 96;
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000300);
    __win16_write_word(&r, s.ss, s.sp + 8, window);
    if (__win16_ui_window(&r, &s, 39) != 6 || !s.ax || __win16_read_word(&r, s.ds, 0x302) != 1 ||
        __win16_read_word(&r, s.ds, 0x308) != 40)
    {
        return 97;
    }
    if (__win16_ui_window(&r, &s, 40) != 6 || s.ax != 1 || ui->paints)
    {
        return 98;
    }
    if (!__win16_install_modules(&r))
    {
        return 99;
    }
    const u8 dll[] = "MMSYSTEM.DLL";
    for (u32 i = 0; i < sizeof(dll); i++)
    {
        __win16_write_byte(&r, s.ds, (u16)(544 + i), dll[i]);
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000220);
    if (__win16_module_service(&r, &s, 95) != 4 || s.ax != 0x600)
    {
        return 100;
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 201);
    __win16_write_word(&r, s.ss, s.sp + 8, 0x600);
    if (__win16_module_service(&r, &s, 50) != 6 || s.ax != 201 || s.dx != 0xf100 ||
        !module_resolve(&r, 0xf10000c9))
    {
        return 101;
    }
    s.ip = 201;
    if (__win16_mm_service(&r, &s) || s.ax != 2)
    {
        return 102;
    }
    u8 *open = ui_buffer(&r, 0x10000340, 20, 1);
    zero(open, 20);
    ui_put32(open + 8, 0x10000200);
    ui_put32(open + 12, 0x10000220);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000340);
    __win16_write_dword(&r, s.ss, s.sp + 8, 0x2200);
    __win16_write_word(&r, s.ss, s.sp + 12, 0x803);
    __win16_write_word(&r, s.ss, s.sp + 14, 0);
    s.ip = 701;
    if (__win16_mm_service(&r, &s) != 12 || s.ax || s.dx || !ui_get16(open + 4) ||
        ui_get16(open + 4) == 0x5678)
    {
        return 103;
    }
    u16 device = ui_get16(open + 4);
    if (mm_guest_device(&r, 0x12345678) != device)
    {
        return 104;
    }
    u8 *status = ui_buffer(&r, 0x10000360, 16, 1);
    zero(status, 16);
    ui_put32(status + 8, 0x4003);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000360);
    __win16_write_dword(&r, s.ss, s.sp + 8, 0x100);
    __win16_write_word(&r, s.ss, s.sp + 12, 0x814);
    __win16_write_word(&r, s.ss, s.sp + 14, device);
    if (__win16_mm_service(&r, &s) != 12 || s.ax || ui_get32(status + 4) != 42)
    {
        return 105;
    }
    if (!__win16_install_task(&r, ui->instance, 3, 256, 0))
    {
        return 106;
    }
    s.sp = 4096;
    __win16_write_dword(&r, s.ss, s.sp, 0x12345678);
    s.bx = 256;
    u16 old = s.sp;
    if (__win16_task_service(&r, &s, 91) || s.ax != 1 || s.dx != 3 || s.di != ui->instance ||
        s.sp != old - 2 || __win16_read_dword(&r, s.ss, s.sp) != 0x12345678 || !s.es ||
        __win16_read_word(&r, s.es, 0x2c) != r.environmentSelector)
    {
        return 107;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0);
    if (__win16_task_service(&r, &s, 30) != 2 || s.ax)
    {
        return 108;
    }
    if (__win16_task_service(&r, &s, 30) != 2 || s.ax != 1 || fixture.waits != 1)
    {
        return 109;
    }
    s.ax = 0x3000;
    if (!__win16_interrupt(&r, &s, 0x21, 0) || s.ax != 5 || (s.flags & 1))
    {
        return 110;
    }
    TaskResources utility = {0};
    if (!__win16_install_wep(&r, &utility))
    {
        return 111;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0x218);
    if (__win16_wep_service(&r, &s, 2) != 2 || s.ax != 1)
    {
        return 112;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, window);
    if (__win16_wep_service(&r, &s, 103) != 2 || fixture.calls == 0 || fixture.failed)
    {
        return 113;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 9);
    __win16_task_service(&r, &s, 1);
    if (r.error != 16 || !r.terminated || r.exitCode != 9)
    {
        return 115;
    }
    r.error = 0;
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.gui || r.modules || r.lifecycle || r.wep || heap.count || !b->available || b->next
        ? 114
        : 0;
}
