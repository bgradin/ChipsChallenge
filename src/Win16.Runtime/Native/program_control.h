/* Static native block address resolution. No original opcodes are read here.
 * Table addresses are original selector:offset values; native pointers live only
 * in this host-owned table, never in original 16-bit pointer fields. */
typedef struct ProgramBinding
{
    u16 segment, offset, kind; /* 1: compiled code; 2: identity-mapped import */
    TranslatedEntry entry;     /* NULL import means explicit unsupported service */
} ProgramBinding;
typedef struct ProgramContext
{
    struct ProgramContext *previous;
    const ProgramBinding *next;
    u32 sentinel, returned, transfers, nextAddress;
} ProgramContext;
typedef struct ProgramImage
{
    const ProgramBinding *table;
    u32 count, maximumTransfers, maximumDepth,
        depth; /* UINT_MAX: task lifetime; finite quotas remain available to fixtures. */
    ProgramContext *active;
} ProgramImage;

static u32 program_address(u16 segment, u16 offset)
{
    return ((u32)segment << 16) | offset;
}

static const ProgramBinding *program_lookup(ProgramImage *p, u32 address)
{
    if (!p || !p->table || !p->count || p->count > 65536)
    {
        return 0;
    }
    u32 low = 0, high = p->count;
    while (low < high)
    {
        u32 middle = low + (high - low) / 2;
        const ProgramBinding *binding = p->table + middle;
        u32 candidate = program_address(binding->segment, binding->offset);
        if (candidate == address)
        {
            return binding;
        }
        if (candidate < address)
        {
            low = middle + 1;
        }
        else
        {
            high = middle;
        }
    }
    return 0;
}
static const ProgramBinding *instance_resolve(Runtime *, u32);
static const ProgramBinding *module_resolve(Runtime *, u32);

static const ProgramBinding *program_resolve(Runtime *r, u32 address)
{
    const ProgramBinding *b = program_lookup(r->program, address);
    if (b)
    {
        return b;
    }
    b = instance_resolve(r, address);
    if (b)
    {
        return b;
    }
    b = module_resolve(r, address);
    if (b)
    {
        return b;
    }
    Segment *a = find_segment(r, (u16)(address >> 16));
    if (!a || !(a->permissions & 4) || !a->alias || (u16)address >= a->size)
    {
        return 0;
    }
    for (Segment *s = r->segments; s; s = s->next)
    {
        if (!s->alias && (s->permissions & 4) && s->backing == a->backing && s->origin == a->origin)
        {
            b = program_lookup(r->program, program_address(s->selector, (u16)address));
            if (b && b->kind == 1)
            {
                return b;
            }
        }
    }
    return 0;
}

static int program_stack(Runtime *r, u16 selector, u16 offset, u32 bytes)
{
    Segment *s = segment(r, selector);
    if (!s || (s->permissions & 3) != 3 || bytes > 65536)
    {
        r->error = 8;
        return 0;
    }
    for (u32 n = 0; n < bytes; n++)
    {
        if ((u16)(offset + n) >= s->size)
        {
            r->error = 8;
            return 0;
        }
    }
    return 1;
}

ABI NOINLINE void __win16_flow_transfer(Runtime *r, State *s, u32 value, u16 form)
{
    if (r->error)
    {
        return;
    }
    ProgramImage *p = r->program;
    ProgramContext *c = p
        ? p->active
        : 0;
    if (!c || c->next || c->returned || form > 5)
    {
        r->error = 8;
        return;
    }
    if (form >= 4)
    {
        u32 bytes = form == 5
            ? 4
            : 2;
        if (value > 65535 || !program_stack(r, s->ss, s->sp, bytes))
        {
            r->error = 8;
            return;
        }
        u16 ip = __win16_read_word(r, s->ss, s->sp);
        u16 cs = form == 5
            ? __win16_read_word(r, s->ss, (u16)(s->sp + 2))
            : s->cs;
        u32 address = program_address(cs, ip);
        const ProgramBinding *b = address == c->sentinel
            ? 0
            : program_resolve(r, address);
        if (address != c->sentinel && (!b || b->kind != 1 || !b->entry))
        {
            r->error = 8;
            return;
        }
        s->sp = (u16)(s->sp + bytes + value);
        s->cs = cs;
        s->ip = ip;
        if (address == c->sentinel)
        {
            c->returned = 1;
        }
        else
        {
            c->next = b;
            c->nextAddress = address;
        }
        return;
    }
    int call = form == 2 || form == 3, farTransfer = form == 1 || form == 3;
    if (!farTransfer && (u16)(value >> 16) != s->cs)
    {
        r->error = 8;
        return;
    }
    const ProgramBinding *b = program_resolve(r, value);
    if (!b || (b->kind != 1 && b->kind != 2) || (!call && !farTransfer && b->kind != 1))
    {
        r->error = 8;
        return;
    }
    if (!b->entry)
    {
        r->error = b->kind == 2
            ? 14
            : 8;
        return;
    }
    if (b->kind == 2 && !farTransfer)
    {
        r->error = 8;
        return;
    }
    if (call)
    {
        u32 bytes = farTransfer
            ? 4
            : 2;
        if (!program_stack(r, s->ss, (u16)(s->sp - bytes), bytes))
        {
            return;
        }
        if (farTransfer)
        {
            push(r, s, s->cs);
        }
        push(r, s, s->ip);
    }
    if (b->kind == 1)
    {
        c->next = b;
        c->nextAddress = value;
    }
    else
    {
        u16 ss = s->ss;
        s->cs = b->segment;
        s->ip = b->offset;
        if (r->observe)
        {
            r->observe(r, s, 2, value);
        }
        u16 cleanup = b->entry(r, s);
        if (r->observe)
        {
            r->observe(r, s, 6, value);
        }
        if (r->error)
        {
            return;
        }
        if (s->ss != ss)
        {
            r->error = 8;
            return;
        }
        __win16_flow_transfer(r, s, cleanup, 5);
    }
}
/* A host callback/entry invocation is the only producer of a host-return token.
 * RET reads original stack bytes and validates their identity before changing SP.
 * CALL depth does not consume the native stack; native blocks return to this
 * bounded resolver loop, including shared epilogues and PUSH CS/CALL near/RETF. */

static u16 program_execute(Runtime *r, State *s, u32 address, u16 farCall, int pushFrame)
{
    if (r->error)
    {
        return 0;
    }
    ProgramImage *p = r->program;
    if (!p || !p->table || !p->count || p->count > 65536 || !p->maximumTransfers ||
        !p->maximumDepth || p->maximumDepth > 64 || p->depth >= p->maximumDepth || farCall > 1)
    {
        r->error = 8;
        return 0;
    }
    u32 previous = 0;
    for (u32 n = 0; n < p->count; n++)
    {
        const ProgramBinding *b = p->table + n;
        u32 key = program_address(b->segment, b->offset);
        if (!b->segment || (n && key <= previous) || (b->kind != 1 && b->kind != 2) ||
            (b->kind == 1 && !b->entry))
        {
            r->error = 8;
            return 0;
        }
        previous = key;
    }
    const ProgramBinding *entry = program_resolve(r, address);
    if (!entry || entry->kind != 1 || (!farCall && (u16)(address >> 16) != s->cs))
    {
        r->error = 8;
        return 0;
    }
    u16 token = 0xffff, sentinelSegment = farCall
        ? 0xfff8
        : (u16)(address >> 16);
    while (program_lookup(p, program_address(sentinelSegment, token)))
    {
        if (!token)
        {
            r->error = 8;
            return 0;
        }
        token--;
    }
    if (pushFrame)
    {
        if (!program_stack(r, s->ss, (u16)(s->sp - (farCall
            ? 4
            : 2)), farCall
            ? 4
            : 2))
        {
            return 0;
        }
        if (farCall)
        {
            push(r, s, sentinelSegment);
        }
        push(r, s, token);
    }
    if (r->observe)
    {
        r->observe(r, s, pushFrame
            ? 4
            : 3, address);
    }
    ProgramContext c = {p->active, entry, program_address(sentinelSegment, token), 0, 0, address};
    p->active = &c;
    p->depth++;
    u16 callerCs = s->cs, callerIp = s->ip;
    while (!r->error && !c.returned)
    {
        if ((p->maximumTransfers != 0xffffffffu && c.transfers >= p->maximumTransfers) || !c.next ||
            !c.next->entry)
        {
            r->error = 8;
            break;
        }
        c.transfers++;
        const ProgramBinding *b = c.next;
        c.next = 0;
        s->cs = (u16)(c.nextAddress >> 16);
        s->ip = (u16)c.nextAddress;
        if (r->observe)
        {
            r->observe(r, s, 1, c.nextAddress);
        }
        b->entry(r, s);
    }
    p->active = c.previous;
    p->depth--;
    if (!r->error)
    {
        s->cs = callerCs;
        s->ip = callerIp;
    }
    return r->error
        ? 0
        : 1;
}

ABI NOINLINE u16 __win16_program_invoke(Runtime *r, State *s, u32 address, u16 farCall)
{
    return program_execute(r, s, address, farCall, 1);
}
/* Loader startup is an entry transfer, not a procedure call. */

ABI NOINLINE u16 __win16_program_enter(Runtime *r, State *s, u32 address)
{
    return program_execute(r, s, address, 1, 0);
}
