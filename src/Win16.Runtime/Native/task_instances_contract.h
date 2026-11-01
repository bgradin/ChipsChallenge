static ABI u16 instance_contract_entry(Runtime *r, State *s)
{
    s->cx = s->ax;
    __win16_flow_transfer(r, s, 0, 5);
    return 0;
}

static int task_instances_contract(FixtureStorage *storage)
{
    Runtime r;
    State s;
    init(&r, &s, storage);
    Segment *instance = map(&r, 0x6000, 128);
    if (!instance)
    {
        return 52;
    }
    ProgramBinding table[] = {{0x4000, 0x100, 1, instance_contract_entry}};
    ProgramImage image = {table, 1, 100, 4, 0, 0};
    r.program = &image;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), instance->selector);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x40000100);
    if (__win16_instance_service(&r, &s, 51) != 6 || s.dx != 0xf200 || !s.ax ||
        r.instanceCount != 1)
    {
        return 53;
    }
    u32 thunk = program_address(s.dx, s.ax);
    u16 sp = s.sp, ds = s.ds;
    if (!resize_data(&r, instance->selector, 256))
    {
        return 53;
    }
    if (!__win16_program_invoke(&r, &s, thunk, 1) || s.cx != instance->selector || s.ds != ds ||
        s.sp != sp || r.error)
    {
        return 54;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), thunk);
    if (__win16_instance_service(&r, &s, 52) != 4 || r.instanceCount || program_resolve(&r, thunk))
    {
        return 55;
    }
    if (__win16_program_invoke(&r, &s, thunk, 1) || r.error != 8 || s.sp != sp)
    {
        return 56;
    }
    r.error = 0;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), instance->selector);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x40000100);
    if (__win16_instance_service(&r, &s, 51) != 6 || program_address(s.dx, s.ax) == thunk)
    {
        return 57;
    }
    thunk = program_address(s.dx, s.ax);
    unmap(&r, instance->selector);
    if (__win16_program_invoke(&r, &s, thunk, 1) || r.error != 8)
    {
        return 58;
    }
    r.error = 0;
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.instances || r.instanceCount || !b->available || b->next
        ? 59
        : 0;
}
