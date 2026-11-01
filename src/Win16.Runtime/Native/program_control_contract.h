/* Native control resolver acceptance fixture; no original game implementation. */
typedef struct
{
    int loop, corruptReturn, callback, calls;
} ProgramContractState;

static ABI u16 root_block(Runtime *r, State *s)
{
    ((ProgramContractState *)r->user)->calls++;
    if (((ProgramContractState *)r->user)->loop)
    {
        __win16_flow_transfer(r, s, 0x40000100, 0);
        return 0;
    }
    u32 pointer = __win16_read_dword(r, s->ds, 0x200);
    if ((u16)(pointer >> 16) == 0xF000)
    {
        push(r, s, 0x1234);
    }
    s->ip = 0x104;
    __win16_flow_transfer(r, s, pointer, (pointer >> 16) == s->cs
        ? 2
        : 3);
    return 0;
}

static ABI u16 return_block(Runtime *r, State *s)
{
    s->dx = 9;
    __win16_flow_transfer(r, s, 0, 5);
    return 0;
}

static ABI u16 callee(Runtime *r, State *s)
{
    s->ax = 7;
    if (((ProgramContractState *)r->user)->corruptReturn)
    {
        __win16_write_word(r, s->ss, s->sp, 0x111);
    }
    __win16_flow_transfer(r, s, 0, 4);
    return 0;
}

static ABI u16 other_callee(Runtime *r, State *s)
{
    s->ax = 8;
    __win16_flow_transfer(r, s, 0, 4);
    return 0;
}

static ABI u16 callback_block(Runtime *r, State *s)
{
    s->cx++;
    __win16_flow_transfer(r, s, 0, 5);
    return 0;
}

static ABI u16 tail_import(Runtime *r, State *s)
{
    __win16_flow_transfer(r, s, 0xF0001000, 1);
    return 0;
}

static ABI u16 service(Runtime *r, State *s)
{
    s->ax = __win16_read_word(r, s->ss, (u16)(s->sp + 4));
    if (((ProgramContractState *)r->user)->callback)
    {
        __win16_program_invoke(r, s, 0x40000120, 1);
    }
    return 2;
}

static NOINLINE int program_control_contract(FixtureStorage *storage)
{
    Runtime r;
    State s;
    ProgramContractState fixture = {0};
    ProgramBinding table[] = {{0x4000, 0x100, 1, root_block},
                              {0x4000, 0x104, 1, return_block},
                              {0x4000, 0x110, 1, callee},
                              {0x4000, 0x114, 1, other_callee},
                              {0x4000, 0x120, 1, callback_block},
                              {0x4000, 0x130, 1, tail_import},
                              {0xF000, 0x1000, 2, 0}};
    ProgramImage p = {table, 7, 100, 4, 0, 0};
    init(&r, &s, storage);
    r.program = &p;
    r.user = &fixture;
    __win16_write_dword(&r, s.ds, 0x200, 0x40000110);
    u16 sp = s.sp, flags = s.flags;
    if (!__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error || s.ax != 7 || s.dx != 9 ||
        s.sp != sp || s.flags != flags || p.depth || p.active)
    {
        return 1;
    }
    __win16_write_dword(&r, s.ds, 0x200, 0x40000114);
    if (!__win16_program_invoke(&r, &s, 0x40000100, 1) || s.ax != 8 || s.sp != sp)
    {
        return 2;
    }
    __win16_write_dword(&r, s.ds, 0x200, 0x40000111);
    if (__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error != 8 || s.sp != (u16)(sp - 4) ||
        p.depth || p.active)
    {
        return 3;
    }
    r.error = 0;
    s.sp = sp;
    s.cs = 0x4000;
    __win16_write_dword(&r, s.ds, 0x200, 0xF0001000);
    if (__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error != 14 || s.sp != (u16)(sp - 6))
    {
        return 4;
    }
    r.error = 0;
    s.sp = sp;
    s.cs = 0x4000;
    table[6].entry = service;
    if (!__win16_program_invoke(&r, &s, 0x40000100, 1) || s.ax != 0x1234 || s.sp != sp)
    {
        return 5;
    }
    fixture.callback = 1;
    s.cx = 0;
    if (!__win16_program_invoke(&r, &s, 0x40000100, 1) || s.cx != 1 || s.sp != sp || p.depth ||
        p.active)
    {
        return 6;
    }
    fixture.callback = 0;
    push(&r, &s, 0x1234);
    if (!__win16_program_invoke(&r, &s, 0x40000130, 1) || s.ax != 0x1234 || s.sp != sp)
    {
        return 5;
    }
    fixture.corruptReturn = 1;
    __win16_write_dword(&r, s.ds, 0x200, 0x40000110);
    if (__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error != 8 || s.sp != (u16)(sp - 6))
    {
        return 7;
    }
    fixture.corruptReturn = 0;
    r.error = 0;
    s.sp = sp;
    s.cs = 0x4000;
    fixture.loop = 1;
    fixture.calls = 0;
    p.maximumTransfers = 8;
    if (__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error != 8 || fixture.calls != 8 ||
        s.sp != (u16)(sp - 4) || p.depth || p.active)
    {
        return 8;
    }
    fixture.loop = 0;
    r.error = 0;
    s.sp = sp;
    table[1].offset = 0x100;
    if (__win16_program_invoke(&r, &s, 0x40000100, 1) || r.error != 8 || s.sp != sp)
    {
        return 9;
    }
    return 0;
}
