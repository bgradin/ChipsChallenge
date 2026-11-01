/* Individual complex operations, not a body interpreter. */

ABI NOINLINE u32 __win16_read_byte(Runtime *r, u16 sel, u16 off)
{
    return r->error
        ? 0
        : read8(r, sel, off);
}

ABI NOINLINE u32 __win16_read_dword(Runtime *r, u16 sel, u16 off)
{
    if (r->error)
    {
        return 0;
    }
    u32 value = 0;
    for (u32 i = 0; i < 4; i++)
    {
        value |= (u32)read8(r, sel, (u16)(off + i)) << (i * 8);
    }
    return value;
}

static void write_value(Runtime *r, u16 sel, u16 off, u32 value, u32 width)
{
    if (r->error)
    {
        return;
    }
    Segment *s = segment(r, sel);
    if (!s)
    {
        return;
    }
    if (!writable(r, s, off, width))
    {
        return;
    }
    for (u32 i = 0; i < width; i++)
    {
        if ((u16)(off + i) >= s->size)
        {
            r->error = 4;
            return;
        }
    }
    for (u32 i = 0; i < width; i++)
    {
        s->bytes[(u16)(off + i)] = (u8)(value >> (i * 8));
    }
}

ABI NOINLINE void __win16_write_byte(Runtime *r, u16 sel, u16 off, u32 value)
{
    write_value(r, sel, off, value, 1);
}

ABI NOINLINE void __win16_write_dword(Runtime *r, u16 sel, u16 off, u32 value)
{
    write_value(r, sel, off, value, 4);
}

ABI NOINLINE void __win16_string(Runtime *r, State *s, u32 op, u16 prefix)
{
    if (op > 7 || prefix > 3)
    {
        r->error = 6;
        return;
    }
    u32 width = (op == 1 || op == 3 || op == 7)
        ? 2
        : 1;
    while (!r->error && (!prefix || s->cx))
    {
        int step = (s->flags & 0x400)
            ? -(int)width
            : (int)width;
        if (op == 0)
        {
            u8 v = read8(r, s->ds, s->si);
            if (r->error)
            {
                return;
            }
            s->ax = (s->ax & 0xFF00) | v;
        }
        else if (op == 1)
        {
            u16 v = __win16_read_word(r, s->ds, s->si);
            if (r->error)
            {
                return;
            }
            s->ax = v;
        }
        else if (op == 2)
        {
            u8 v = read8(r, s->ds, s->si);
            write_value(r, s->es, s->di, v, 1);
        }
        else if (op == 3)
        {
            u16 v = __win16_read_word(r, s->ds, s->si);
            write_value(r, s->es, s->di, v, 2);
        }
        else if (op == 4)
        {
            write_value(r, s->es, s->di, s->ax, 1);
        }
        else
        {
            u32 a = op == 5
                ? (u8)s->ax
                : width == 1
                        ? read8(r, s->ds, s->si)
                        : __win16_read_word(r, s->ds, s->si);
            if (r->error)
            {
                return;
            }
            u32 b = width == 1
                ? read8(r, s->es, s->di)
                : __win16_read_word(r, s->es, s->di);
            u32 sign = width == 1
                ? 128
                : 32768,
                v = (a - b) & (width == 1
                    ? 255
                    : 65535);
            if (r->error)
            {
                return;
            }
            u32 parity = 0;
            for (u32 x = v & 255; x; x >>= 1)
            {
                parity ^= x & 1;
            }
            u16 f = (a < b
                ? 1
                : 0) |
                (!parity
                    ? 4
                    : 0) |
                ((a ^ b ^ v) & 16) | (!v
                    ? 64
                    : 0) |
                ((v & sign)
                    ? 128
                    : 0) |
                (((a ^ b) & (a ^ v) & sign)
                    ? 2048
                    : 0);
            s->flags = (s->flags & ~0x8D5) | f;
        }
        if (r->error)
        {
            return;
        }
        if (op < 4 || op >= 6)
        {
            s->si = (u16)(s->si + step);
        }
        if (op >= 2)
        {
            s->di = (u16)(s->di + step);
        }
        if (!prefix)
        {
            return;
        }
        s->cx--;
        if (op >= 5 && ((prefix == 3) == !!(s->flags & 64)))
        {
            return;
        }
    }
}

static unsigned long long dreg(State *s, u32 index)
{
    u8 *p = (u8 *)s;
    u32 off = index * 2;
    return p[off] | ((u32)p[off + 1] << 8) | ((u32)p[28 + off] << 16) | ((u32)p[29 + off] << 24);
}

static void set_dreg(State *s, u32 index, u32 value)
{
    u8 *p = (u8 *)s;
    u32 off = index * 2;
    p[off] = (u8)value;
    p[off + 1] = (u8)(value >> 8);
    p[28 + off] = (u8)(value >> 16);
    p[29 + off] = (u8)(value >> 24);
}

ABI NOINLINE void __win16_divide(Runtime *r, State *s, u32 divisor, u16 form)
{
    if (r->error)
    {
        return;
    }
    u32 width = form & 255;
    if (width != 8 && width != 16 && width != 32)
    {
        r->error = 6;
        return;
    }
    unsigned long long mask = width == 32
        ? 0xFFFFFFFFull
        : ((1ull << width) - 1),
                       q, rem;
    unsigned long long dividend = width == 8
        ? s->ax
        : width == 16
                ? s->ax | ((u32)s->dx << 16)
                : dreg(s, 0) | (dreg(s, 3) << 32);
    divisor &= (u32)mask;
    if (!divisor)
    {
        r->error = 7;
        return;
    }
    if (!(form & 0x100))
    {
        q = dividend / divisor;
        rem = dividend % divisor;
        if (q > mask)
        {
            r->error = 7;
            return;
        }
    }
    else
    {
        long long a = width == 8
            ? (short)dividend
            : width == 16
                    ? (int)dividend
                    : (long long)dividend;
        long long b = width == 8
            ? (signed char)divisor
            : width == 16
                    ? (short)divisor
                    : (int)divisor;
        if (a == (-9223372036854775807ll - 1) && b == -1)
        {
            r->error = 7;
            return;
        }
        long long result = a / b, remainder = a % b;
        if (result < -(1ll << (width - 1)) || result >= (1ll << (width - 1)))
        {
            r->error = 7;
            return;
        }
        q = (unsigned long long)result;
        rem = (unsigned long long)remainder;
    }
    if (width == 8)
    {
        s->ax = (u16)(((rem & 255) << 8) | (q & 255));
    }
    else if (width == 16)
    {
        s->ax = (u16)q;
        s->dx = (u16)rem;
    }
    else
    {
        set_dreg(s, 0, (u32)q);
        set_dreg(s, 3, (u32)rem);
    }
}

ABI NOINLINE void __win16_call_target(Runtime *r, State *s, u32 pointer, u16 farCall)
{
    if (r->error)
    {
        return;
    }
    u16 seg = (u16)(pointer >> 16), off = (u16)pointer;
    if (farCall && seg == 0xF000)
    {
#ifdef NE_WINDOW
        if (off >= 0x100 && window_import_target(r, s, off))
        {
            return;
        }
#endif
        for (u32 i = 0; i < sizeof(api_bindings) / sizeof(api_bindings[0]); i++)
        {
            if (api_bindings[i].offset == off)
            {
                __win16_call_service(r, s, api_bindings[i].id);
                return;
            }
        }
    }
    CodeBinding *b = r->code;
    while (b && (b->segment != seg || b->offset != off))
    {
        b = b->next;
    }
    if (!b || !b->entry || b->farCall != farCall || (!farCall && seg != s->cs))
    {
        r->error = 8;
        return;
    }
    u16 cs = s->cs, ip = s->ip, ss = s->ss, bp = s->bp, ds = s->ds, si = s->si, di = s->di;
    if (farCall)
    {
        push(r, s, cs);
    }
    push(r, s, ip);
    s->cs = seg;
    s->ip = off;
    u16 cleanup = b->entry(r, s);
    s->ss = ss;
    s->ip = pop(r, s);
    if (farCall)
    {
        s->cs = pop(r, s);
    }
    else
    {
        s->cs = cs;
    }
    s->sp = (u16)(s->sp + cleanup);
    s->bp = bp;
    s->ds = ds;
    s->si = si;
    s->di = di;
}

/* Approved targets are checked in generated AMD64 before this compatibility boundary. */

ABI NOINLINE void __win16_dispatch_fault(Runtime *r, State *s)
{
    (void)s;
    if (!r->error)
    {
        r->error = 8;
    }
}

ABI NOINLINE u16 __win16_tail_target(Runtime *r, State *s, u32 pointer, u16 farJump)
{
    if (r->error)
    {
        return 0;
    }
    u16 seg = (u16)(pointer >> 16), off = (u16)pointer;
    CodeBinding *b = r->code;
    while (b && (b->segment != seg || b->offset != off))
    {
        b = b->next;
    }
    if (!b || !b->entry || (!farJump && seg != s->cs))
    {
        r->error = 8;
        return 0;
    }
    s->cs = seg;
    s->ip = off;
    /* JMP pushes no return address. The original caller consumes the target RET. */
    return b->entry(r, s);
}

/* CPU interrupt service boundary. No guest instruction stream is executed here.
 * A freestanding DOS/Win16 service adapter is installed in milestone 7.6.
 * Until then, reject the service explicitly; successful native emission is not
 * evidence that the interrupt service itself is implemented. */
static void task_dos(Runtime *, State *);

ABI NOINLINE u16 __win16_interrupt(Runtime *r, State *s, u32 vector, u16 form)
{
    if (r->error)
    {
        return 0;
    }
    if (vector != 0x21 || !r->lifecycle)
    {
        r->error = 13;
        return 0;
    }
    u16 oldSp = s->sp, cs = s->cs, ip = s->ip, flags = s->flags;
    for (u16 n = 2; n <= 6; n += 2)
    {
        Segment *segment = find_segment(r, s->ss);
        u16 at = (u16)(oldSp - n);
        if (!segment || (segment->permissions & 3) != 3 || at >= segment->size ||
            segment->size - at < 2)
        {
            r->error = 9;
            return 0;
        }
    }
    push(r, s, flags);
    push(r, s, cs);
    push(r, s, ip);
    s->flags = flags & ~0x300u;
    task_dos(r, s);
    if (r->error)
    {
        return 0;
    }
    __win16_write_word(r, s->ss, (u16)(s->sp + 4), flags & ~1u);
    s->ip = pop(r, s);
    s->cs = pop(r, s);
    s->flags = pop(r, s);
    return r->error
        ? 0
        : 1;
}
