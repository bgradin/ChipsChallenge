typedef struct
{
    u32 calls;
    iptr dc, object;
    u16 last;
    int deleteCount;
} UiStructuresFixture;

static iptr fixture_ui_structures(void *context, u16 function, const iptr *a, u16 count)
{
    UiStructuresFixture *f = context;
    f->calls++;
    f->last = function;
    if (function == UI_CreateFontIndirectW)
    {
        u8 *font = (u8 *)a[0];
        return (int)ui_get32(font) == -32 && ui_get32(font + 16) == 700 &&
                ui_get16(font + 28) == 0x20ac
            ? 0x123456789LL
            : 0;
    }
    if (function == UI_GetTextMetricsA)
    {
        u8 *metrics = (u8 *)a[1];
        for (u32 i = 0; i < 11; i++)
        {
            ui_put32(metrics + i * 4, 100 + i);
        }
        for (u32 i = 0; i < 9; i++)
        {
            metrics[44 + i] = (u8)(20 + i);
        }
        return a[0] == f->dc
            ? 1
            : 0;
    }
    if (function == UI_GetClientRect)
    {
        int *rect = (int *)a[1];
        for (int i = 0; i < 4; i++)
        {
            rect[i] = i * 10 - 5;
        }
        return 1;
    }
    if (function == UI_GetDlgItemTextW)
    {
        u16 *text = (u16 *)a[2];
        text[0] = 0x20ac;
        text[1] = 0;
        return 1;
    }
    if (function == UI_SystemParametersInfoW)
    {
        *(u32 *)a[2] = 3;
        return 1;
    }
    if (function == UI_GetObjectType)
    {
        return 7;
    }
    if (function == UI_GetObjectW)
    {
        u8 *bitmap = (u8 *)a[2];
        ui_put32(bitmap + 4, 48);
        ui_put32(bitmap + 8, 64);
        ui_put32(bitmap + 12, 24);
        ui_put16(bitmap + 16, 1);
        ui_put16(bitmap + 18, 4);
        *(iptr *)(bitmap + 24) = 0x765432109LL;
        return 32;
    }
    return 1;
}

static int task_ui_structures_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 8192))
    {
        return 80;
    }
    s.ds = s.ss = 0x1000;
    s.sp = 4096;
    s.dx = 0x7777;
    TaskGui gui;
    init_task_gui(&gui, 0, 0);
    r.gui = &gui;
    TaskUi *ui = r.alloc(r.storage, sizeof(*ui));
    if (!ui)
    {
        return 81;
    }
    zero(ui, sizeof(*ui));
    gui.ui = ui;
    ui->runtime = &r;
    ui->instance = s.ds;
    UiStructuresFixture fixture = {0, 0x223456789LL, 0, 0, 0};
    ui->context = &fixture;
    ui->call = fixture_ui_structures;
    __win16_write_word(&r, s.ds, 64, (u16)-32);
    __win16_write_word(&r, s.ds, 72, 700);
    __win16_write_byte(&r, s.ds, 82, 0x80);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000040);
    if (__win16_ui_structure(&r, &s, 3, 57) != 4 || !s.ax || !gui_handle(&gui, s.ax, 7) ||
        gui_handle(&gui, s.ax, 7)->native != 0x123456789LL || !gui_handle(&gui, s.ax, 7)->owned)
    {
        return 82;
    }
    gui_handle(&gui, s.ax, 7)->owned = 0;
    u16 dc = ui_handle_add(ui, fixture.dc, 6, 0, 0);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000100);
    __win16_write_word(&r, s.ss, s.sp + 8, dc);
    if (__win16_ui_structure(&r, &s, 3, 93) != 6 || s.ax != 1 ||
        __win16_read_byte(&r, s.ds, 272) != 24 || __win16_read_byte(&r, s.ds, 275) != 20 ||
        __win16_read_word(&r, s.ds, 281) != 108 || __win16_read_word(&r, s.ds, 285) != 110 ||
        s.dx != 0x7777)
    {
        return 83;
    }
    u16 window = ui_handle_add(ui, 0x323456789LL, 5, 0, 0);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000040);
    __win16_write_word(&r, s.ss, s.sp + 8, window);
    if (__win16_ui_structure(&r, &s, 2, 33) != 6 || __win16_read_word(&r, s.ds, 64) != (u16)-5 ||
        __win16_read_word(&r, s.ds, 70) != 25)
    {
        return 84;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 3);
    __win16_write_word(&r, s.ss, s.sp + 6, 2);
    __win16_write_dword(&r, s.ss, s.sp + 8, 0x10000040);
    if (__win16_ui_structure(&r, &s, 2, 78) != 8 || __win16_read_word(&r, s.ds, 64) != (u16)-7 ||
        __win16_read_word(&r, s.ds, 70) != 28)
    {
        return 85;
    }
    const u8 format[] = "%05d %lu %s %%", text[] = "ok";
    for (u32 i = 0; i < sizeof(format); i++)
    {
        __win16_write_byte(&r, s.ds, (u16)(512 + i), format[i]);
    }
    for (u32 i = 0; i < sizeof(text); i++)
    {
        __win16_write_byte(&r, s.ds, (u16)(544 + i), text[i]);
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000100);
    __win16_write_dword(&r, s.ss, s.sp + 8, 0x10000200);
    __win16_write_word(&r, s.ss, s.sp + 12, (u16)-7);
    __win16_write_dword(&r, s.ss, s.sp + 14, 0xfedcba98);
    __win16_write_dword(&r, s.ss, s.sp + 18, 0x10000220);
    if (__win16_ui_structure(&r, &s, 2, 420) || s.ax != 21 ||
        __win16_read_byte(&r, s.ds, 256) != '-' || __win16_read_byte(&r, s.ds, 260) != '7' ||
        __win16_read_byte(&r, s.ds, 277))
    {
        return 86;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0);
    __win16_write_dword(&r, s.ss, s.sp + 6, 0x100000a0);
    __win16_write_word(&r, s.ss, s.sp + 10, 0);
    __win16_write_word(&r, s.ss, s.sp + 12, 0x16);
    if (__win16_ui_structure(&r, &s, 2, 483) != 10 || s.ax != 1 ||
        __win16_read_word(&r, s.ds, 160) != 3)
    {
        return 87;
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10001fff);
    __win16_write_word(&r, s.ss, s.sp + 8, dc);
    u32 calls = fixture.calls;
    __win16_ui_structure(&r, &s, 3, 93);
    if (r.error != 9 || fixture.calls != calls)
    {
        return 88;
    }
    r.error = 0;
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.gui || !b->available || b->next
        ? 89
        : 0;
}
