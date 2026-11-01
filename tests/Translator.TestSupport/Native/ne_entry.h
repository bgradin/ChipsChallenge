/* Historical standalone fixture oracle; excluded from the production payload. */
#ifndef NE_APPLICATION

ABI NOINLINE u32 ne_entry(const u8 *data, const int *procedures, u32 length,
                          u32 availableProcedures)
{
    Runtime runtime, *r = &runtime;
    State state, *s = &state;
    HeapStorage storage, *h = &storage;
    zero(r, sizeof(*r));
    zero(s, sizeof(*s));
    zero(h, sizeof(*h));
    ImageReader d = {data, data + length, 0};
    if (number(&d, 4) != 0x34454e54 || number(&d, 4) != 1 || number(&d, 4) != 2 ||
        number(&d, 4) != sizeof(State))
    {
        return 10;
    }
    u32 segments = number(&d, 4), count = number(&d, 4), root = number(&d, 4),
        arguments = number(&d, 4);
    u32 registers = number(&d, 4), memories = number(&d, 4);
    h->limit = number(&d, 4);
    h->maximum = wide(&d);
    u32 globalLimit = number(&d, 4);
    unsigned long long globalBytes = wide(&d);
    const u8 *initial = take(&d, sizeof(*s));
    if (d.error || !segments || segments > 32768 || !count || count > 4096 ||
        count != availableProcedures || root >= count || arguments > 256 || registers > 64 ||
        memories > 4096 || !h->limit || !h->maximum || !globalLimit)
    {
        return 10;
    }
    for (u32 i = 0; i < sizeof(*s); i++)
    {
        ((u8 *)s)[i] = initial[i];
    }
    h->heap = native_HeapCreate(0, 65536, 0);
    if (!h->heap)
    {
        return 20;
    }
    init_runtime(r, h, heap_alloc, heap_release, globalLimit, 32768, globalBytes);
    u32 result = 0, rootPointer = 0, rootFar = 0;
    for (u32 i = 0; i < segments; i++)
    {
        u16 selector = number(&d, 2), permissions = number(&d, 2);
        u32 size = number(&d, 4), initialized = number(&d, 4);
        const u8 *bytes = take(&d, initialized);
        if (d.error || !selector || selector == 0xf000 || permissions > 7 || initialized > size)
        {
            result = 10;
            goto done;
        }
        Segment *segment = map(r, selector, size);
        if (!segment)
        {
            result = 20;
            goto done;
        }
        for (u32 j = 0; j < initialized; j++)
        {
            segment->bytes[j] = bytes[j];
        }
        segment->permissions = permissions;
    }
    if (!find_segment(r, s->cs) || !find_segment(r, s->ss) || (s->ds && !find_segment(r, s->ds)))
    {
        result = 10;
        goto done;
    }
    for (u32 i = 0; i < count; i++)
    {
        u16 segment = number(&d, 2), offset = number(&d, 2);
        u32 farCall = number(&d, 4);
        Segment *code = find_segment(r, segment);
        if (d.error || !code || !(code->permissions & 4) || offset >= code->size || farCall > 1)
        {
            result = 10;
            goto done;
        }
        for (CodeBinding *b = r->code; b; b = b->next)
        {
            if (b->segment == segment && b->offset == offset)
            {
                result = 10;
                goto done;
            }
        }
        CodeBinding *b = heap_alloc(h, sizeof(*b));
        if (!b)
        {
            result = 20;
            goto done;
        }
        b->next = r->code;
        b->segment = segment;
        b->offset = offset;
        b->farCall = (u16)farCall;
        b->entry = (TranslatedEntry)((const u8 *)&procedures[i] + 4 + procedures[i]);
        r->code = b;
        if (i == root)
        {
            rootPointer = ((u32)segment << 16) | offset;
            rootFar = farCall;
        }
    }
    const u8 *words = take(&d, arguments * 2);
    if (d.error)
    {
        result = 10;
        goto done;
    }
    for (u32 i = arguments; i > 0; i--)
    {
        push(r, s, (u16)(words[i * 2 - 2] | ((u16)words[i * 2 - 1] << 8)));
    }
    if (!rootFar)
    {
        s->cs = (u16)(rootPointer >> 16);
    }
    __win16_call_target(r, s, rootPointer, (u16)rootFar);
    if (r->error)
    {
        result = 30;
        goto done;
    }
    for (u32 i = 0; i < registers; i++)
    {
        u32 lo = number(&d, 2), hi = number(&d, 2), expected = number(&d, 4);
        if (d.error || lo > 42 || lo % 2 || (hi != 65535 && (hi > 42 || hi % 2)))
        {
            result = 10;
            goto done;
        }
        u32 value = ((u16 *)s)[lo / 2];
        if (hi != 65535)
        {
            value |= (u32)((u16 *)s)[hi / 2] << 16;
        }
        if (value != expected)
        {
            result = 40;
            goto done;
        }
    }
    for (u32 i = 0; i < memories; i++)
    {
        u16 selector = number(&d, 2), offset = number(&d, 2);
        u32 size = number(&d, 4);
        const u8 *expected = take(&d, size);
        Segment *segment = find_segment(r, selector);
        if (d.error || size > 65536)
        {
            result = 10;
            goto done;
        }
        if (!segment || !(segment->permissions & 1) || size > segment->size ||
            offset > segment->size - size)
        {
            result = 40;
            goto done;
        }
        for (u32 j = 0; j < size; j++)
        {
            if (segment->bytes[offset + j] != expected[j])
            {
                result = 40;
                goto done;
            }
        }
    }
    if (d.p != d.end || (!registers && !memories))
    {
        result = 10;
    }
done:
    while (r->code)
    {
        CodeBinding *b = r->code;
        r->code = b->next;
        heap_release(h, b);
    }
    destroy_runtime(r);
    if (h->count || h->bytes || h->blocks || h->error)
    {
        result = 50;
    }
    if (!native_HeapDestroy(h->heap))
    {
        result = 50;
    }
    return result;
}

#endif /* NE_APPLICATION */
