/* Cross-check the production freestanding storage and reverse-entry contracts. */
typedef struct
{
    u32 calls, nested, separate;
    LocalHeap heap;
} MemoryContract;

static ABI u16 memory_callback_block(Runtime *r, State *s)
{
    MemoryContract *f = r->user;
    if ((!f->separate && s->ss != s->ds) || (f->separate && s->ss == s->ds))
    {
        r->error = 8;
        return 0;
    }
    u16 sp = s->sp;
    push(r, s, 0xbeef);
    if (!f->separate && __win16_read_word(r, s->ds, s->sp) != 0xbeef)
    {
        r->error = 8;
        return 0;
    }
    s->sp = sp;
    f->calls++;
    __win16_write_word(r, s->ds, 0, (u16)f->calls);
    if (f->nested && r->program->depth == 1)
    {
        State saved = *s;
        u32 value = task_callback(r, s, program_address(0x1000, 0), 1, 64, 0, 0);
        if (value != 7 || s->sp != saved.sp || s->cs != saved.cs || s->ds != saved.ds ||
            s->ss != saved.ss)
        {
            r->error = 8;
            return 0;
        }
    }
    s->ax = 7;
    s->high[0] = 0;
    __win16_flow_transfer(r, s, 0, 5);
    return 0;
}

static ABI u16 memory_alias_block(Runtime *r, State *s)
{
    s->ax = s->cs;
    __win16_flow_transfer(r, s, 0, 5);
    return 0;
}

static int program_memory_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime runtime, *r = &runtime;
    init_runtime(r, storage, fixture_alloc, fixture_release, 64, 64, 65536);
    Segment *d = map(r, 0x1008, 1024), *c = map(r, 0x1000, 16);
    if (!d || !c)
    {
        return 1;
    }
    c->permissions = 5;
    c->bytes[0] = 0xcb;
    Segment *a = alias_segment(r, 0x6500, 0x1000, 0, 0, 5);
    if (!a)
    {
        return 2;
    }
    __win16_write_word(r, a->selector, 0, 0x1234);
    if (r->error != 15 || r->codeWriteCount != 1 || r->codeWriteSelector != a->selector ||
        r->codeWriteBytes != 2 || c->bytes[0] != 0xcb)
    {
        return 3;
    }
    r->error = 0;
    if (alias_segment(r, 0x6501, 0x1000, 0, 0, 7) || r->error != 2)
    {
        return 4;
    }
    r->error = 0;
    u16 handle = allocate(r, 8, 0x42), sel = lock_memory(r, handle);
    Segment *g = alias_segment(r, 0x6600, sel, 0, 0, 3);
    if (!g)
    {
        return 5;
    }
    __win16_write_word(r, g->selector, 0, 0xbeef);
    if (reallocate(r, handle, 32, 2) != handle || g->size != 32 ||
        __win16_read_word(r, g->selector, 0) != 0xbeef)
    {
        return 6;
    }
    unlock_memory(r, handle);
    if (reallocate(r, handle, 16, 2) != handle || find_segment(r, sel) || find_segment(r, 0x6600))
    {
        return 7;
    }
    if (!free_memory(r, handle))
    {
        return 8;
    }
    r->wholeMemory = 1;
    u16 fixed = allocate(r, 12, 0x20);
    if (!fixed || lock_memory(r, fixed) != fixed)
    {
        return 9;
    }
    if (reallocate(r, fixed, 20, 0x20) != fixed)
    {
        return 10;
    }
    unlock_memory(r, fixed);
    if (!free_memory(r, fixed))
    {
        return 11;
    }
    MemoryContract fixture;
    zero(&fixture, sizeof(fixture));
    r->user = &fixture;
    r->local = &fixture.heap;
    if (!local_init(r, r->local, d->selector, 128, 512))
    {
        return 12;
    }
    u16 first = local_allocate(r, r->local, 7, 0x20), next = local_allocate(r, r->local, 8, 0);
    __win16_write_word(r, d->selector, first, 0xabcd);
    __win16_write_word(r, d->selector, next, 0x9999);
    if (local_size(r->local, first) != 8 || local_reallocate(r, r->local, first, 32, 0))
    {
        return 13;
    }
    u16 moved = local_reallocate(r, r->local, first, 32, 0x62);
    if (!moved || moved == first || __win16_read_word(r, d->selector, moved) != 0xabcd ||
        __win16_read_word(r, d->selector, (u16)(moved + 8)) ||
        __win16_read_word(r, d->selector, next) != 0x9999)
    {
        return 14;
    }
    local_free(r, r->local, moved);
    local_free(r, r->local, next);
    Segment *view = alias_segment(r, 0x6700, d->selector, 0, 0, 3);
    if (!view || !resize_data(r, d->selector, 2048) || view->size != 2048 ||
        !local_extend(r, r->local, 2048))
    {
        return 15;
    }
    ProgramBinding table[] = {{0x1000, 0, 1, memory_callback_block},
                              {0x1000, 1, 1, memory_alias_block}};
    ProgramImage program = {table, 2, 128, 8, 0, 0};
    r->program = &program;
    State state;
    zero(&state, sizeof(state));
    state.ds = d->selector;
    state.ss = d->selector;
    state.sp = 128;
    state.cs = 0x1000;
    state.flags = 0x202;
    state.high[0] = 0xabcd;
    State saved = state;
    fixture.nested = 1;
    if (task_callback(r, &state, program_address(0x1000, 0), 1, 128, 0, 0) != 7 || r->error ||
        fixture.calls != 2 || fixture.heap.count || __win16_read_word(r, d->selector, 0) != 2)
    {
        return 16;
    }
    for (u32 i = 0; i < sizeof(State); i++)
    {
        if (((u8 *)&state)[i] != ((u8 *)&saved)[i])
        {
            return 17;
        }
    }
    if (!__win16_program_invoke(r, &state, program_address(a->selector, 1), 1) ||
        state.ax != a->selector || r->error)
    {
        return 18;
    }
    Segment *stack = map(r, 0x6800, 512);
    if (!stack)
    {
        return 19;
    }
    fixture.nested = 0;
    fixture.separate = 1;
    if (task_callback(r, &state, program_address(0x1000, 0), 2, 0, stack->selector, 256) != 7 ||
        r->error)
    {
        return 20;
    }
    if (!local_allocate(r, r->local, 16, 0))
    {
        return 21;
    }
    destroy_runtime(r);
    if (r->local || fixture.heap.count)
    {
        return 21;
    }
    Block *b = (Block *)storage->bytes;
    if (!b->available || b->next || b->size != sizeof(storage->bytes) - sizeof(Block))
    {
        return 21;
    }
    return 0;
}
