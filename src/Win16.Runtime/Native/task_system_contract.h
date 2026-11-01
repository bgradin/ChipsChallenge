static int task_system_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 2048))
    {
        return 44;
    }
    s.ds = s.ss = 0x1000;
    s.sp = 1024;
    s.dx = 0x7777;
    const u8 module[] = {'C', ':', '\\', 'A', 'P', 'P', '\\', 'T',
                         'E', 'S', 'T', '.', 'E', 'X', 'E', 0},
             directory[] = {'C', ':', '\\', 'W', 'I', 'N', 'D', 'O', 'W', 'S', 0};
    TaskSystem system = {s.ds, 0x0a03, 0x0500, module, directory};
    r.system = &system;
    if (__win16_system_service(&r, &s, 3) || s.ax != 0x0a03 || s.dx != 0x0500)
    {
        return 45;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 64);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000040);
    if (__win16_system_service(&r, &s, 134) != 6 || s.ax != 10 ||
        __win16_read_byte(&r, s.ds, 64) != 'C' || __win16_read_byte(&r, s.ds, 74) || s.dx != 0x0500)
    {
        return 46;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 2);
    __win16_write_word(&r, s.ds, 64, 0x7777);
    __win16_system_service(&r, &s, 134);
    if (s.ax != 11 || __win16_read_word(&r, s.ds, 64) != 0x7777)
    {
        return 47;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 64);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000080);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), s.ds);
    if (__win16_system_service(&r, &s, 49) != 8 || s.ax != 15 ||
        __win16_read_byte(&r, s.ds, 128) != 'C' || __win16_read_byte(&r, s.ds, 143))
    {
        return 48;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 4);
    if (__win16_system_service(&r, &s, 49) != 8 || s.ax != 3 || __win16_read_byte(&r, s.ds, 131))
    {
        return 49;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 64);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), 0x7777);
    __win16_system_service(&r, &s, 49);
    if (s.ax)
    {
        return 50;
    }
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.system || !b->available || b->next
        ? 51
        : 0;
}
