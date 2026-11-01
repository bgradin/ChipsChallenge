/* Independent physical-stack API contract oracle; does not use generated argument offsets. */

static int task_kernel_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 128, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    Segment *data = map(&r, 0x1000, 4096);
    if (!data)
    {
        return 1;
    }
    s.ds = s.ss = data->selector;
    s.sp = 1024;
    s.dx = 0xabcd;
    LocalHeap heap;
    if (!local_init(&r, &heap, s.ds, 2048, 2048))
    {
        return 1;
    }
    r.local = &heap;
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), 65536);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 8), 0x42);
    if (__win16_kernel_service(&r, &s, 15) != 6 || !s.ax || s.dx != 0xabcd || r.error)
    {
        return 2;
    }
    u16 h = s.ax;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), h);
    if (__win16_kernel_service(&r, &s, 20) != 2 || s.ax || s.dx != 1)
    {
        return 3;
    }
    if (__win16_kernel_service(&r, &s, 18) != 2 || s.ax || !s.dx)
    {
        return 4;
    }
    u16 sel = s.dx;
    __win16_write_word(&r, sel, 0, 0xbeef);
    Segment *alias = alias_segment(&r, 0x6800, sel, 0, 0, 3);
    if (!alias)
    {
        return 5;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 2);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 32768);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), h);
    if (__win16_kernel_service(&r, &s, 16) != 8 || s.ax != h || alias->size != 32768 ||
        __win16_read_word(&r, alias->selector, 0) != 0xbeef)
    {
        return 6;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), h);
    if (__win16_kernel_service(&r, &s, 17) != 2 || s.ax || find_segment(&r, sel) ||
        find_segment(&r, 0x6800))
    {
        return 7;
    }
    __win16_kernel_service(&r, &s, 18);
    if (s.ax || s.dx)
    {
        return 8;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 7);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 6), 0x40);
    if (__win16_kernel_service(&r, &s, 5) != 4 || !s.ax)
    {
        return 9;
    }
    u16 local = s.ax;
    if (__win16_read_word(&r, s.ds, local))
    {
        return 9;
    }
    __win16_write_word(&r, s.ds, local, 0x1234);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 8);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 6), 0x20);
    __win16_kernel_service(&r, &s, 5);
    u16 neighbour = s.ax;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0x62);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 6), 4096);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 8), local);
    if (__win16_kernel_service(&r, &s, 6) != 6 || !s.ax || s.ax == local ||
        __win16_read_word(&r, s.ds, s.ax) != 0x1234 || heap.end != 65536)
    {
        return 10;
    }
    local = s.ax;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), local);
    if (__win16_kernel_service(&r, &s, 10) != 2 || s.ax < 4096)
    {
        return 11;
    }
    if (__win16_kernel_service(&r, &s, 7) != 2 || s.ax)
    {
        return 12;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), neighbour);
    __win16_kernel_service(&r, &s, 7);
    if (heap.count)
    {
        return 12;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0xffff);
    if (__win16_kernel_service(&r, &s, 23) != 2 || s.ax != s.ds)
    {
        return 13;
    }
    s.ax = 0x7777;
    if (__win16_kernel_service(&r, &s, 24) != 2 || s.ax != 0x7777)
    {
        return 13;
    }
    __win16_write_word(&r, s.ds, 100, 0x6241);
    __win16_write_word(&r, s.ds, 102, 0x0063);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), ((u32)s.ds << 16) | 100);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 8), ((u32)s.ds << 16) | 200);
    if (__win16_kernel_service(&r, &s, 88) != 8 || s.ax != 200 || s.dx != s.ds ||
        __win16_read_word(&r, s.ds, 200) != 0x6241)
    {
        return 14;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), ((u32)s.ds << 16) | 200);
    if (__win16_kernel_service(&r, &s, 90) != 4 || s.ax != 3)
    {
        return 15;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 8), ((u32)s.ds << 16) | 100);
    if (__win16_user_string_compare(&r, &s) != 8 || s.ax)
    {
        return 16;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0x1234);
    s.dx = 0xabcd;
    if (__win16_kernel_service(&r, &s, 107) != 2 || s.ax || s.dx != 0xabcd)
    {
        return 17;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0);
    __win16_kernel_service(&r, &s, 107);
    if (s.ax != 0x1234)
    {
        return 17;
    }
    if (__win16_kernel_service(&r, &s, 131) != 0 || s.ax || !s.dx)
    {
        return 18;
    }
    u16 env = s.dx;
    __win16_kernel_service(&r, &s, 131);
    if (s.dx != env || __win16_read_word(&r, env, 0) || __win16_read_word(&r, env, 2) != 1 ||
        r.error)
    {
        return 18;
    }
    if (__win16_kernel_service(&r, &s, 91) || r.error != 14)
    {
        return 19;
    }
    r.error = 0;
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    if (!b->available || b->next || b->size != sizeof(storage->bytes) - sizeof(Block))
    {
        return 20;
    }
    return 0;
}
