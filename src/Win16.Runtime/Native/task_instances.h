/* MakeProcInstance uses one precompiled entry and host-owned metadata. No guest
 * instructions or AMD64 instructions are generated after static translation. */
typedef struct InstanceThunk
{
    struct InstanceThunk *next;
    ProgramBinding binding;
    u32 target;
    u16 instance;
    Segment *data;
} InstanceThunk;

static const ProgramBinding *instance_resolve(Runtime *r, u32 address)
{
    for (InstanceThunk *t = r->instances; t; t = t->next)
    {
        if (program_address(t->binding.segment, t->binding.offset) == address)
        {
            return &t->binding;
        }
    }
    return 0;
}

static ABI u16 instance_thunk_entry(Runtime *r, State *s)
{
    InstanceThunk *t = r->instances;
    u32 address = program_address(s->cs, s->ip);
    while (t && program_address(t->binding.segment, t->binding.offset) != address)
    {
        t = t->next;
    }
    Segment *data = t
        ? find_segment(r, t->instance)
        : 0;
    if (!t || !data || data != t->data)
    {
        r->error = 8;
        return 0;
    }
    s->ax = t->instance;
    if (r->observe)
    {
        r->observe(r, s, 7, t->target);
    }
    __win16_flow_transfer(r, s, t->target, 1);
    return 0;
}

ABI NOINLINE u16 __win16_instance_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const KernelApiContract *c = kernel_contract(ordinal);
    if (!c || (ordinal != 51 && ordinal != 52))
    {
        r->error = 14;
        return 0;
    }
    u32 pointer = kernel_argument(r, s, c, 0);
    if (r->error)
    {
        return 0;
    }
    if (ordinal == 51)
    {
        u32 value = kernel_argument(r, s, c, 1);
        if (r->error)
        {
            return 0;
        }
        const ProgramBinding *target = program_resolve(r, pointer);
        Segment *data = find_segment(r, (u16)value);
        u32 result = 0;
        if (target && target->kind == 1 && data && !(data->permissions & 4) &&
            (data->permissions & 1) && r->nextInstanceThunk < 4096 && r->program &&
            r->program->count + r->instanceCount < 65536)
        {
            u16 offset = (u16)(0x1000 + r->nextInstanceThunk * 8);
            u32 address = program_address(0xf200, offset);
            if (!program_lookup(r->program, address) && !find_segment(r, 0xf200))
            {
                InstanceThunk *t = r->alloc(r->storage, sizeof(*t));
                if (t)
                {
                    t->binding.segment = 0xf200;
                    t->binding.offset = offset;
                    t->binding.kind = 1;
                    t->binding.entry = instance_thunk_entry;
                    t->target = pointer;
                    t->instance = (u16)value;
                    t->data = data;
                    t->next = r->instances;
                    r->instances = t;
                    r->nextInstanceThunk++;
                    r->instanceCount++;
                    result = address;
                }
            }
        }
        s->ax = (u16)result;
        s->dx = (u16)(result >> 16);
    }
    else
    {
        InstanceThunk **link = &r->instances;
        while (*link)
        {
            InstanceThunk *t = *link;
            if (program_address(t->binding.segment, t->binding.offset) == pointer)
            {
                *link = t->next;
                r->release(r->storage, t);
                r->instanceCount--;
                break;
            }
            link = &t->next;
        }
    }
    return c->cleanup;
}

static void destroy_instance_thunks(Runtime *r)
{
    while (r->instances)
    {
        InstanceThunk *t = r->instances;
        r->instances = t->next;
        r->release(r->storage, t);
    }
    r->instanceCount = 0;
}
