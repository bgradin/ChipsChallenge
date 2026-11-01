/* Host-runnable production native control-flow acceptance. */
#include "acceptance.c"

static ABI u16 startup_frame_probe(Runtime *r, State *s)
{
    if (s->sp != 1024 || __win16_read_dword(r, s->ss, 1020) != 0x87654321u)
    {
        r->error = 8;
        return 0;
    }
    r->terminated = 1;
    r->exitCode = 9;
    r->error = 16;
    return 0;
}

static int startup_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    if (!map(&r, 0x1000, 2048))
    {
        return 10;
    }
    State s;
    zero(&s, sizeof(s));
    s.ss = s.ds = 0x1000;
    s.cs = 0x4000;
    s.ip = 0x1000;
    s.sp = 1024;
    __win16_write_dword(&r, s.ss, 1020, 0x87654321u);
    ProgramBinding binding = {0x4000, 0x1000, 1, startup_frame_probe};
    ProgramImage program = {&binding, 1, 16, 8, 0, 0};
    r.program = &program;
    if (__win16_program_enter(&r, &s, 0x40001000) || r.error != 16 || !r.terminated ||
        r.exitCode != 9 || s.sp != 1024 || program.depth || program.active)
    {
        return 10;
    }
    r.error = 0;
    destroy_runtime(&r);
    return 0;
}
/* Accelerate the counter to its wrap boundary instead of executing billions
 * of idle GUI blocks. Approved-target and finite fixture limits still apply. */

static ABI u16 counter_wrap_probe(Runtime *r, State *s)
{
    if (!s->ax)
    {
        s->ax = 1;
        r->program->active->transfers = 0xffffffffu;
        __win16_flow_transfer(r, s, 0x40001000, 0);
    }
    else
    {
        __win16_flow_transfer(r, s, 0, 5);
    }
    return 0;
}

static int lifetime_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    if (!map(&r, 0x1000, 2048))
    {
        return 10;
    }
    State s;
    zero(&s, sizeof(s));
    s.ss = s.ds = 0x1000;
    s.cs = 0x4000;
    s.sp = 1024;
    ProgramBinding binding = {0x4000, 0x1000, 1, counter_wrap_probe};
    ProgramImage program = {&binding, 1, 0xffffffffu, 8, 0, 0};
    r.program = &program;
    if (!__win16_program_invoke(&r, &s, 0x40001000, 1) || r.error || s.sp != 1024 ||
        program.depth || program.active)
    {
        return 10;
    }
    s.ax = 0;
    program.maximumTransfers = 2;
    if (__win16_program_invoke(&r, &s, 0x40001000, 1) || r.error != 8 || program.depth ||
        program.active)
    {
        return 10;
    }
    r.error = 0;
    destroy_runtime(&r);
    return 0;
}

int main(void)
{
    static FixtureStorage storage;
    int result = program_control_contract(&storage);
    if (result)
    {
        return result;
    }
    result = startup_contract(&storage);
    return result
        ? result
        : lifetime_contract(&storage);
}
