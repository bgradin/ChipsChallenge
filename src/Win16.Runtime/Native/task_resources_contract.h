static int task_resources_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 128, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 2048))
    {
        return 1;
    }
    s.ss = s.ds = 0x1000;
    s.sp = 512;
    s.dx = 0xabcd;
    const u8 bytes[] = {0x28, 0, 1, 2}, name[] = {'P', 'I', 'C', 0};
    TaskResource rows[] = {{2, 0, 0, name, bytes, sizeof(bytes), 0, 0}};
    TaskResources resources = {rows, 1, s.ds};
    r.resources = &resources;
    __win16_write_dword(&r, s.ds, 64, 0x00636970);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), 2);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 8), 0x10000040);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 12), 0);
    if (__win16_resource_service(&r, &s, 60) != 10 || !s.ax || s.dx != 0xabcd || r.error)
    {
        return 2;
    }
    u16 reference = s.ax;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), reference);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 6), 0);
    if (__win16_resource_service(&r, &s, 61) != 4 || !s.ax || s.ax == reference)
    {
        return 3;
    }
    u16 handle = s.ax;
    if (__win16_resource_service(&r, &s, 61) != 4 || s.ax != handle || rows[0].uses != 2)
    {
        return 4;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), handle);
    if (__win16_resource_service(&r, &s, 62) != 2 || s.ax || !s.dx)
    {
        return 5;
    }
    u16 selector = s.dx;
    if (__win16_read_dword(&r, selector, 0) != 0x02010028)
    {
        return 6;
    }
    if (__win16_resource_service(&r, &s, 63) != 2 || s.ax || !find_segment(&r, selector) ||
        rows[0].uses != 1)
    {
        return 7;
    }
    if (__win16_resource_service(&r, &s, 63) != 2 || s.ax || find_segment(&r, selector) ||
        rows[0].dataHandle)
    {
        return 8;
    }
    if (__win16_resource_service(&r, &s, 62) != 2 || s.ax || s.dx)
    {
        return 9;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), reference);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 6), 0);
    r.failAlloc = 1;
    s.ax = 0x7777;
    if (__win16_resource_service(&r, &s, 61) != 4 || s.ax || r.allocated)
    {
        return 10;
    }
    __win16_resource_service(&r, &s, 61);
    if (!s.ax)
    {
        return 11;
    }
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return !b->available || b->next
        ? 12
        : 0;
}
