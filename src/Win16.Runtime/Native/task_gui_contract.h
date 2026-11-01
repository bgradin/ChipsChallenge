typedef struct
{
    u16 module, ordinal, count;
    iptr args[11], result;
    u32 calls;
    u16 text[4];
} GuiFixture;

static iptr fixture_gui_call(void *context, u16 module, u16 ordinal, const iptr *args, u16 count)
{
    GuiFixture *f = context;
    f->module = module;
    f->ordinal = ordinal;
    f->count = count;
    f->calls++;
    for (u16 i = 0; i < count; i++)
    {
        f->args[i] = args[i];
    }
    if (module == 2 && ordinal == 37)
    {
        u16 *s = (u16 *)args[1];
        for (u16 i = 0; i < 3; i++)
        {
            f->text[i] = s[i];
        }
    }
    return f->result;
}

static int task_gui_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 2048))
    {
        return 60;
    }
    s.ds = s.ss = 0x1000;
    s.sp = 1024;
    s.dx = 0x7777;
    TaskGui gui;
    GuiFixture f;
    zero(&f, sizeof(f));
    init_task_gui(&gui, &f, fixture_gui_call);
    r.gui = &gui;
    u16 window = gui_add(&r, &gui, 0x123456789LL, 5, 0, 0),
        dc = gui_add(&r, &gui, 0x223456789LL, 6, 1, 0);
    __win16_write_word(&r, s.ss, s.sp + 4, 0xfff6);
    __win16_write_word(&r, s.ss, s.sp + 6, window);
    f.result = 1;
    if (__win16_gui_scalar(&r, &s, 2, 42) != 4 || f.args[0] != 0x123456789LL || f.args[1] != -10 ||
        s.dx != 0x7777 || s.ax != 1)
    {
        return 61;
    }
    __win16_write_dword(&r, s.ss, s.sp + 4, 0xfedcba98);
    __win16_write_word(&r, s.ss, s.sp + 8, dc);
    f.result = 0x89abcdef;
    if (__win16_gui_scalar(&r, &s, 3, 1) != 6 || f.args[0] != 0x223456789LL ||
        f.args[1] != 0xfedcba98LL || s.ax != 0xcdef || s.dx != 0x89ab)
    {
        return 62;
    }
    __win16_write_byte(&r, s.ds, 64, 0x80);
    __win16_write_byte(&r, s.ds, 65, 'A');
    __win16_write_byte(&r, s.ds, 66, 0);
    __win16_write_dword(&r, s.ss, s.sp + 4, 0x10000040);
    __win16_write_word(&r, s.ss, s.sp + 8, window);
    f.result = 1;
    if (__win16_gui_scalar(&r, &s, 2, 37) != 6 || f.text[0] != 0x20ac || f.text[1] != 'A' ||
        f.text[2])
    {
        return 63;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0);
    f.result = 0x323456789LL;
    if (__win16_gui_scalar(&r, &s, 3, 52) != 2 || !s.ax || s.ax == (u16)f.result)
    {
        return 64;
    }
    u16 created = s.ax;
    if (!gui_handle(&gui, created, 6) || !gui_handle(&gui, created, 6)->owned)
    {
        return 65;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, created);
    f.result = 0;
    __win16_gui_scalar(&r, &s, 3, 68);
    if (!gui_handle(&gui, created, 6))
    {
        return 66;
    }
    f.result = 1;
    __win16_gui_scalar(&r, &s, 3, 68);
    if (gui_handle(&gui, created, 6))
    {
        return 67;
    }
    u16 borrowed = gui_add(&r, &gui, 0x423456789LL, 6, 0, 0);
    u32 calls = f.calls;
    __win16_write_word(&r, s.ss, s.sp + 4, borrowed);
    __win16_gui_scalar(&r, &s, 3, 68);
    if (s.ax || f.calls != calls)
    {
        return 68;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 1);
    __win16_write_word(&r, s.ss, s.sp + 6, 2);
    __win16_write_word(&r, s.ss, s.sp + 8, 3);
    f.result = 0xffffffeb;
    if (__win16_gui_scalar(&r, &s, 3, 128) != 6 || s.ax != (u16)-21 || f.args[0] != 3 ||
        f.args[2] != 1)
    {
        return 69;
    }
    f.result = 32768;
    __win16_gui_scalar(&r, &s, 3, 128);
    if (s.ax != 0xffff)
    {
        return 70;
    }
    __win16_write_word(&r, s.ss, s.sp + 4, 0xffff);
    __win16_write_word(&r, s.ss, s.sp + 6, 2);
    __win16_write_word(&r, s.ss, s.sp + 8, 0xffff);
    calls = f.calls;
    __win16_gui_scalar(&r, &s, 2, 154);
    if (s.ax != 0xffff || f.calls != calls)
    {
        return 71;
    }
    gui_add(&r, &gui, 0x523456789LL, 5, 0, window);
    gui_forget(&r, &gui, window);
    if (gui.count != 2)
    {
        return 72;
    }
    calls = f.calls;
    f.result = 1;
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.error || r.gui || f.calls != calls + 1 || f.ordinal != 68 ||
            f.args[0] != 0x223456789LL || !b->available || b->next
        ? 73
        : 0;
}
