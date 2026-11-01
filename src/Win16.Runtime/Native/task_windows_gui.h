#include "task_windows_gui_calls.h"
#include "task_windows_ui_calls.h"

ABI NOINLINE int __win16_install_windows_gui(Runtime *r)
{
    if (r->gui)
    {
        return 0;
    }
    TaskGui *gui = r->alloc(r->storage, sizeof(*gui));
    if (!gui)
    {
        return 0;
    }
    init_task_gui(gui, 0, windows_gui_call);
    r->gui = gui;
    r->ownsGui = 1;
    return 1;
}

static ABI iptr windows_ui_window_procedure(iptr hwnd, u32 message, iptr wp, iptr lp)
{
    UiWindow *window = message == 0x81 && lp
        ? *(UiWindow **)(void *)lp
        : (UiWindow *)(void *)ui_native_GetWindowLongPtrW(hwnd, -21);
    if (!window)
    {
        return ui_native_DefWindowProcW(hwnd, message, wp, lp);
    }
    if (message == 0x81)
    {
        ui_native_SetWindowLongPtrW(hwnd, -21, (iptr)window);
    }
    return ui_window_callback(window, hwnd, message, wp, lp);
}

static ABI iptr windows_ui_dialog_procedure(iptr hwnd, u32 message, iptr wp, iptr lp)
{
    UiWindow *window = message == 0x110
        ? (UiWindow *)(void *)lp
        : (UiWindow *)(void *)ui_native_GetWindowLongPtrW(hwnd, 16);
    if (!window)
    {
        return 0;
    }
    if (message == 0x110)
    {
        ui_native_SetWindowLongPtrW(hwnd, 16, (iptr)window);
    }
    return ui_window_callback(window, hwnd, message, wp, message == 0x110
        ? 0
        : lp);
}

static ABI void windows_ui_timer_procedure(iptr hwnd, u32 message, iptr id, u32 time)
{
    UiWindow *window = (UiWindow *)(void *)ui_native_GetWindowLongPtrW(hwnd, -21);
    if (!window)
    {
        return;
    }
    TaskUi *ui = window->ui;
    for (UiTimer *t = ui->timers; t; t = t->next)
    {
        if (t->window == hwnd && t->id == (u16)id && t->target)
        {
            u16 args[] = {(u16)time, (u16)(time >> 16), (u16)id, (u16)message, window->id};
            ui_reverse(ui, t->target, args, 5, 1);
            return;
        }
    }
}

static ABI iptr windows_wep_about_procedure(iptr hwnd, u32 message, iptr wp, iptr lp)
{
    WepAbout *about = message == 0x110
        ? (WepAbout *)(void *)lp
        : (WepAbout *)(void *)ui_native_GetWindowLongPtrW(hwnd, 16);
    if (!about)
    {
        return 0;
    }
    if (message == 0x110)
    {
        ui_native_SetWindowLongPtrW(hwnd, 16, (iptr)about);
    }
    return wep_about_callback(about, hwnd, message, wp, lp);
}

ABI NOINLINE int __win16_install_windows_ui(Runtime *r, State *s, u16 instance)
{
    if (!r->gui && !__win16_install_windows_gui(r))
    {
        return 0;
    }
    if (r->gui->ui)
    {
        return 0;
    }
    TaskUi *ui = r->alloc(r->storage, sizeof(*ui));
    if (!ui)
    {
        return 0;
    }
    zero(ui, sizeof(*ui));
    ui->runtime = r;
    ui->state = s;
    ui->instance = instance;
    ui->call = windows_ui_call;
    ui->windowProcedure = (iptr)windows_ui_window_procedure;
    ui->dialogProcedure = (iptr)windows_ui_dialog_procedure;
    ui->timerProcedure = (iptr)windows_ui_timer_procedure;
    ui->aboutProcedure = (iptr)windows_wep_about_procedure;
    r->gui->ui = ui;
    return 1;
}
/* Genuine Windows API probe, independent of application startup. The callback is
 * an ABI fixture; application/game instructions are not supplied by this host. */

static ABI u16 windows_ui_probe_callback(Runtime *r, State *s)
{
    __win16_write_word(r, s->ds, 64, (u16)(__win16_read_word(r, s->ds, 64) + 1));
    __win16_ui_window(r, s, 107);
    if (!r->error)
    {
        __win16_flow_transfer(r, s, 10, 5);
    }
    return 0;
}

static u16 windows_ui_probe_call(Runtime *r, State *s, u16 module, u16 ordinal, const u32 *args,
                                 int scalar)
{
    const UiContract *c = ui_contract(module, ordinal);
    if (!c)
    {
        r->error = 14;
        return 0;
    }
    for (u32 i = 0; i < c->count; i++)
    {
        u16 at = (u16)(s->sp + 4 + c->words[i] * 2);
        if (c->widths[i] == 4)
        {
            __win16_write_dword(r, s->ss, at, args[i]);
        }
        else
        {
            __win16_write_word(r, s->ss, at, (u16)args[i]);
        }
    }
    if (module == 2 && (ordinal == 93 || ordinal == 95))
    {
        return __win16_ui_structure(r, s, module, ordinal);
    }
    if (scalar)
    {
        return __win16_gui_scalar(r, s, module, ordinal);
    }
    if (module == 3)
    {
        return __win16_ui_structure(r, s, module, ordinal);
    }
    return __win16_ui_window(r, s, ordinal);
}

static u16 windows_kernel_probe_call(Runtime *r, State *s, u16 ordinal, const u32 *args)
{
    const KernelApiContract *c = kernel_contract(ordinal);
    if (!c)
    {
        r->error = 14;
        return 0;
    }
    for (u32 i = 0; i < c->count; i++)
    {
        u16 at = (u16)(s->sp + 4 + c->words[i] * 2);
        if (c->widths[i] == 4)
        {
            __win16_write_dword(r, s->ss, at, args[i]);
        }
        else
        {
            __win16_write_word(r, s->ss, at, (u16)args[i]);
        }
    }
    return __win16_import_dispatch(r, s, 1, ordinal);
}

static int windows_io_profile_probe(Runtime *r, State *s)
{
    if (!__win16_install_windows_system(r, s->ds) || !__win16_install_windows_files(r) ||
        !__win16_install_windows_profiles(r))
    {
        return 193;
    }
    u32 args[12] = {0};
    windows_kernel_probe_call(r, s, 3, args);
    if (r->error || s->ax != 0x0a03 || s->dx != 0x0500)
    {
        return 194;
    }
    args[0] = s->ds;
    args[1] = 0x10000900;
    args[2] = 260;
    windows_kernel_probe_call(r, s, 49, args);
    if (r->error || !s->ax)
    {
        return 195;
    }
    args[0] = 0x10000a20;
    args[1] = 260;
    windows_kernel_probe_call(r, s, 134, args);
    if (r->error || !s->ax)
    {
        return 196;
    }
    args[0] = 0x10000900;
    args[1] = 0x10000b40;
    args[2] = 0x20;
    windows_kernel_probe_call(r, s, 74, args);
    if (r->error || s->ax == 0xffff)
    {
        return 197;
    }
    u16 file = s->ax;
    args[0] = file;
    args[1] = 0x10000c00;
    args[2] = 2;
    windows_kernel_probe_call(r, s, 82, args);
    if (r->error || s->ax != 2 || __win16_read_word(r, s->ds, 0xc00) != 0x5a4d)
    {
        return 198;
    }
    args[0] = file;
    args[1] = 0;
    args[2] = 0;
    windows_kernel_probe_call(r, s, 84, args);
    if (r->error || s->ax || s->dx)
    {
        return 199;
    }
    args[0] = file;
    windows_kernel_probe_call(r, s, 81, args);
    if (r->error || s->ax)
    {
        return 200;
    }
    const u8 section[] = "Ne", key[] = "Value", value[] = "17", filename[] = "service-probe.ini";
    u8 *dest = ui_buffer(r, 0x10000c20, sizeof(section), 1);
    for (u32 i = 0; i < sizeof(section); i++)
    {
        dest[i] = section[i];
    }
    dest = ui_buffer(r, 0x10000c40, sizeof(key), 1);
    for (u32 i = 0; i < sizeof(key); i++)
    {
        dest[i] = key[i];
    }
    dest = ui_buffer(r, 0x10000c60, sizeof(value), 1);
    for (u32 i = 0; i < sizeof(value); i++)
    {
        dest[i] = value[i];
    }
    const u8 *module = r->system->modulePath;
    u32 length = system_path_length(r, module), prefix = 0;
    for (u32 i = 0; i < length; i++)
    {
        if (module[i] == '\\' || module[i] == '/')
        {
            prefix = i + 1;
        }
    }
    if (prefix + sizeof(filename) > 260)
    {
        return 201;
    }
    dest = ui_buffer(r, 0x10000c80, 260, 1);
    for (u32 i = 0; i < prefix; i++)
    {
        dest[i] = module[i];
    }
    for (u32 i = 0; i < sizeof(filename); i++)
    {
        dest[prefix + i] = filename[i];
    }
    args[0] = 0x10000c20;
    args[1] = 0x10000c40;
    args[2] = 0x10000c60;
    args[3] = 0x10000c80;
    windows_kernel_probe_call(r, s, 129, args);
    if (r->error || !s->ax)
    {
        return 202;
    }
    args[2] = 0xffff;
    windows_kernel_probe_call(r, s, 127, args);
    if (r->error || s->ax != 17)
    {
        return 203;
    }
    args[2] = 0;
    args[3] = 0x10000c00;
    args[4] = 16;
    args[5] = 0x10000c80;
    windows_kernel_probe_call(r, s, 128, args);
    if (r->error || s->ax != 2 || __win16_read_word(r, s->ds, 0xc00) != 0x3731)
    {
        return 204;
    }
    args[0] = args[1] = args[2] = 0;
    args[3] = 0x10000c80;
    windows_kernel_probe_call(r, s, 129, args);
    /* Windows documents zero for cache flush. The parent also checks the INI
       on disk after process exit, independently of cached integer/string reads. */
    if (r->error || s->ax)
    {
        return 205;
    }
    const u8 relative[] = "service-relative.ini";
    dest = ui_buffer(r, 0x10000d90, sizeof(relative), 1);
    for (u32 i = 0; i < sizeof(relative); i++)
    {
        dest[i] = relative[i];
    }
    args[0] = 0x10000c20;
    args[1] = 0x10000c40;
    args[2] = 0x10000c60;
    args[3] = 0x10000d90;
    windows_kernel_probe_call(r, s, 129, args);
    return r->error || !s->ax
        ? 206
        : 0;
}

ABI NOINLINE int __win16_windows_ui_probe(void)
{
    HeapStorage heap;
    zero(&heap, sizeof(heap));
    heap.heap = native_HeapCreate(0, 0, 0);
    if (!heap.heap)
    {
        return 170;
    }
    heap.maximum = 16 * 1024 * 1024;
    heap.limit = 4096;
    Runtime r;
    init_runtime(&r, &heap, heap_alloc, heap_release, 4096, 65535, 16 * 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    s.ds = s.ss = 0x1000;
    s.cs = 0x4000;
    s.sp = 4096;
    int result = 0;
    LocalHeap local;
    zero(&local, sizeof(local));
    ProgramBinding bindings[] = {{0x4000, 0, 1, windows_ui_probe_callback}};
    ProgramImage program = {bindings, 1, 10000, 32, 0, 0};
    r.program = &program;
    if (!map(&r, s.ds, 65536) || !local_init(&r, &local, s.ds, 8192, 57344))
    {
        result = 171;
        goto finish;
    }
    r.local = &local;
    if (!__win16_install_windows_ui(&r, &s, s.ds))
    {
        result = 172;
        goto finish;
    }
    const u8 name[] = "Win16Ne6ServiceProbe";
    u8 *data = ui_buffer(&r, 0x10000200, sizeof(name), 1);
    for (u32 i = 0; i < sizeof(name); i++)
    {
        data[i] = name[i];
    }
    u8 *cls = ui_buffer(&r, 0x10000100, 26, 1);
    zero(cls, 26);
    ui_put32(cls + 2, 0x40000000);
    ui_put16(cls + 8, 4);
    ui_put16(cls + 10, s.ds);
    ui_put32(cls + 22, 0x10000200);
    u32 a[12] = {0};
    a[0] = 0x10000100;
    windows_ui_probe_call(&r, &s, 2, 57, a, 0);
    if (r.error || !s.ax)
    {
        result = 173;
        goto finish;
    }
    zero(a, sizeof(a));
    a[0] = 0x10000200;
    a[2] = 0x00cf0000;
    a[3] = 10;
    a[4] = 10;
    a[5] = 160;
    a[6] = 120;
    a[9] = s.ds;
    windows_ui_probe_call(&r, &s, 2, 41, a, 0);
    u16 window = s.ax;
    if (r.error || !window || !__win16_read_word(&r, s.ds, 64))
    {
        result = 174;
        goto finish;
    }
    /* Positive native edit-control getters: real Windows text, BOOL32->BOOL16,
       bounded UTF-16->ANSI output and actual typed service routing. */
    TaskUi *probeUi = r.gui->ui;
    const u16 editClass[] = {'E', 'd', 'i', 't', 0}, editText[] = {'2', 0};
    iptr edit =
        UI_CALL(probeUi, CreateWindowExW, 0, (iptr)editClass, (iptr)editText, 0x40000000, 0, 0, 40,
                20, ui_handle(probeUi, window, 5), 100, UI_CALL(probeUi, GetModuleHandleW, 0), 0);
    if (!edit)
    {
        result = 207;
        goto finish;
    }
    a[0] = window;
    a[1] = 100;
    a[2] = 0x10001100;
    a[3] = 0;
    windows_ui_probe_call(&r, &s, 2, 95, a, 0);
    if (r.error || s.ax != 2 || __win16_read_word(&r, s.ds, 0x1100) != 1)
    {
        result = 208;
        goto finish;
    }
    a[2] = 0x10001110;
    a[3] = 4;
    windows_ui_probe_call(&r, &s, 2, 93, a, 0);
    if (r.error || s.ax != 1 || __win16_read_word(&r, s.ds, 0x1110) != 0x32)
    {
        result = 209;
        goto finish;
    }
    UI_CALL(probeUi, DestroyWindow, edit);
    a[0] = window;
    windows_ui_probe_call(&r, &s, 2, 66, a, 0);
    u16 dc = s.ax;
    if (r.error || !dc)
    {
        result = 175;
        goto finish;
    }
    a[0] = dc;
    windows_ui_probe_call(&r, &s, 3, 52, a, 1);
    u16 compatible = s.ax;
    if (r.error || !compatible)
    {
        result = 176;
        goto finish;
    }
    u8 *font = ui_buffer(&r, 0x10000300, 50, 1);
    zero(font, 50);
    ui_put16(font, (u16)-12);
    a[0] = 0x10000300;
    windows_ui_probe_call(&r, &s, 3, 57, a, 0);
    u16 object = s.ax;
    if (r.error || !object)
    {
        result = 177;
        goto finish;
    }
    a[0] = compatible;
    a[1] = object;
    windows_ui_probe_call(&r, &s, 3, 45, a, 1);
    u16 previous = s.ax;
    if (r.error || !previous)
    {
        result = 178;
        goto finish;
    }
    a[0] = compatible;
    a[1] = 0x10000400;
    windows_ui_probe_call(&r, &s, 3, 93, a, 0);
    if (r.error || !s.ax || !__win16_read_word(&r, s.ds, 0x400))
    {
        result = 179;
        goto finish;
    }
    a[0] = compatible;
    a[1] = previous;
    windows_ui_probe_call(&r, &s, 3, 45, a, 1);
    a[0] = object;
    windows_ui_probe_call(&r, &s, 3, 69, a, 1);
    if (r.error || !s.ax)
    {
        result = 180;
        goto finish;
    }
    a[0] = compatible;
    windows_ui_probe_call(&r, &s, 3, 68, a, 1);
    a[0] = window;
    a[1] = dc;
    windows_ui_probe_call(&r, &s, 2, 68, a, 0);
    a[0] = window;
    windows_ui_probe_call(&r, &s, 2, 53, a, 1);
    if (r.error)
    {
        result = 181;
        goto finish;
    }
    if (!__win16_install_modules(&r) || !__win16_install_task(&r, s.ds, 1, 256, 0))
    {
        result = 182;
        goto finish;
    }
    s.ax = 0x3000;
    __win16_task_service(&r, &s, 102);
    if (r.error || s.ax != 5)
    {
        result = 183;
        goto finish;
    }
    const u8 dll[] = "MMSYSTEM.DLL";
    data = ui_buffer(&r, 0x10000500, sizeof(dll), 1);
    for (u32 i = 0; i < sizeof(dll); i++)
    {
        data[i] = dll[i];
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000500);
    __win16_module_service(&r, &s, 95);
    if (r.error || s.ax != 0x600)
    {
        result = 184;
        goto finish;
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 201);
    __win16_write_word(&r, s.ss, s.sp + 8, 0x600);
    __win16_module_service(&r, &s, 50);
    if (r.error || s.ax != 201 || s.dx != 0xf100)
    {
        result = 185;
        goto finish;
    }
    s.ip = 201;
    __win16_mm_service(&r, &s);
    if (r.error)
    {
        result = 186;
        goto finish;
    }
    /* An absent MIDI asset must return a WinMM error, never crash from a
       naturally aligned MCI_OPEN_PARMSW. mmsystem.h uses byte packing. */
    const u8 typeName[] = "sequencer", missingFile[] = "missing-native-probe.mid";
    data = ui_buffer(&r, 0x10000600, sizeof(typeName), 1);
    for (u32 i = 0; i < sizeof(typeName); i++)
    {
        data[i] = typeName[i];
    }
    data = ui_buffer(&r, 0x10000640, sizeof(missingFile), 1);
    for (u32 i = 0; i < sizeof(missingFile); i++)
    {
        data[i] = missingFile[i];
    }
    data = ui_buffer(&r, 0x10000700, 20, 1);
    zero(data, 20);
    ui_put32(data + 8, 0x10000600);
    ui_put32(data + 12, 0x10000640);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000700);
    __win16_write_dword(&r, s.ss, s.sp + 8, 0x2200);
    __win16_write_word(&r, s.ss, s.sp + 12, 0x803);
    __win16_write_word(&r, s.ss, s.sp + 14, 0);
    s.ip = 701;
    if (__win16_mm_service(&r, &s) != 12 || r.error || (!s.ax && !s.dx))
    {
        result = 189;
        goto finish;
    }
    u32 missingError = (u32)s.dx << 16 | s.ax;
    __win16_write_word(&r, s.ss, s.sp + 4, 128);
    __win16_write_dword(&r, s.ss, s.sp + 6, 0x10000800);
    __win16_write_dword(&r, s.ss, s.sp + 10, missingError);
    s.ip = 706;
    if (__win16_mm_service(&r, &s) != 10 || r.error || !s.ax || !__win16_read_byte(&r, s.ds, 0x800))
    {
        result = 190;
        goto finish;
    }
    s.ip = 401;
    if (__win16_mm_service(&r, &s) || r.error)
    {
        result = 191;
        goto finish;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0);
    __win16_write_dword(&r, s.ss, s.sp + 6, 0);
    s.ip = 2;
    if (__win16_mm_service(&r, &s) != 6 || r.error)
    {
        result = 192;
        goto finish;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0x600);
    __win16_module_service(&r, &s, 96);
    if (r.error || r.modules->references)
    {
        result = 187;
        goto finish;
    }
    result = windows_io_profile_probe(&r, &s);
finish:
    if (!result && r.error)
    {
        result = 188;
    }
    destroy_runtime(&r);
    if (!result && (heap.count || heap.bytes || heap.error))
    {
        result = 189;
    }
    native_HeapDestroy(heap.heap);
    return result;
}
