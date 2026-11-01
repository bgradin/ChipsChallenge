/* Fixed local blocks: logical DS offsets. Host metadata never occupies guest bytes. */
typedef struct LocalBlock
{
    struct LocalBlock *next;
    u32 offset, size;
} LocalBlock;
typedef struct LocalHeap
{
    u16 selector;
    u32 start, end, count;
    LocalBlock *blocks;
} LocalHeap;

static int local_init(Runtime *r, LocalHeap *h, u16 selector, u32 start, u32 bytes)
{
    Segment *s = segment(r, selector);
    if (!s || (s->permissions & 3) != 3 || start < 2 || (start & 1) || bytes > s->size ||
        start > s->size - bytes)
    {
        return 0;
    }
    zero(h, sizeof(*h));
    h->selector = selector;
    h->start = start;
    h->end = start + bytes;
    return 1;
}

static u32 local_size(LocalHeap *h, u16 pointer)
{
    for (LocalBlock *b = h->blocks; b; b = b->next)
    {
        if (b->offset == pointer)
        {
            return b->size;
        }
    }
    return 0;
}

static u16 local_allocate(Runtime *r, LocalHeap *h, u32 bytes, u16 flags)
{
    if ((flags & ~0x60) || bytes > 65534)
    {
        return 0;
    }
    u32 size = ((bytes
        ? bytes
        : 1) +
                1) &
        ~1,
        offset = h->start;
    LocalBlock **p = &h->blocks;
    while (*p)
    {
        if ((*p)->offset - offset >= size)
        {
            break;
        }
        offset = (*p)->offset + (*p)->size;
        p = &(*p)->next;
    }
    if (offset > h->end || size > h->end - offset)
    {
        return 0;
    }
    Segment *s = segment(r, h->selector);
    if (!s || !writable(r, s, (u16)offset, size) || offset > s->size || size > s->size - offset)
    {
        return 0;
    }
    LocalBlock *b = r->alloc(r->storage, sizeof(*b));
    if (!b)
    {
        return 0;
    }
    b->offset = offset;
    b->size = size;
    b->next = *p;
    *p = b;
    h->count++;
    if (flags & 0x40)
    {
        zero(s->bytes + offset, size);
    }
    return (u16)offset;
}

static int local_free(Runtime *r, LocalHeap *h, u16 pointer)
{
    LocalBlock **p = &h->blocks;
    while (*p)
    {
        LocalBlock *b = *p;
        if (b->offset == pointer)
        {
            *p = b->next;
            r->release(r->storage, b);
            h->count--;
            return 1;
        }
        p = &b->next;
    }
    return 0;
}

static u16 local_reallocate(Runtime *r, LocalHeap *h, u16 pointer, u32 bytes, u16 flags)
{
    if ((flags & ~0x62) || bytes > 65534)
    {
        return 0;
    }
    LocalBlock *b = h->blocks;
    while (b && b->offset != pointer)
    {
        b = b->next;
    }
    if (!b)
    {
        return 0;
    }
    if (!bytes)
    {
        local_free(r, h, pointer);
        return 0;
    }
    u32 size = (bytes + 1) & ~1, limit = b->next
        ? b->next->offset
        : h->end;
    Segment *s = segment(r, h->selector);
    if (!s || !writable(r, s, pointer, size))
    {
        return 0;
    }
    if (size <= limit - pointer)
    {
        if (size > b->size && (flags & 0x40))
        {
            zero(s->bytes + pointer + b->size, size - b->size);
        }
        b->size = size;
        return pointer;
    }
    if (!(flags & 2))
    {
        return 0;
    }
    u16 n = local_allocate(r, h, bytes, flags & 0x40);
    if (!n)
    {
        return 0;
    }
    for (u32 i = 0; i < size && i < b->size; i++)
    {
        s->bytes[n + i] = s->bytes[pointer + i];
    }
    local_free(r, h, pointer);
    return n;
}

static int local_extend(Runtime *r, LocalHeap *h, u32 end)
{
    Segment *s = segment(r, h->selector);
    if (!s || end < h->end || end > s->size)
    {
        return 0;
    }
    h->end = end;
    return 1;
}

static void local_destroy(Runtime *r, LocalHeap *h)
{
    while (h->blocks)
    {
        local_free(r, h, (u16)h->blocks->offset);
    }
}

static void destroy_local_memory(Runtime *r)
{
    if (r->local)
    {
        local_destroy(r, r->local);
        r->local = 0;
    }
}
/* Reverse entry restores all 44 CPU bytes even on failure. Memory mutations persist. */

static u32 task_callback_arguments(Runtime *r, State *s, u32 target, u16 mode, u32 stackBytes,
                                   u16 separate, u16 top, const u16 *arguments, u16 count,
                                   u16 resultMode)
{
    State saved = *s;
    u16 lease = 0;
    u32 value = 0;
    LocalHeap *h = r->local;
    if (mode == 1)
    {
        if (!h || stackBytes < 32 || stackBytes > 65530 || (stackBytes & 1))
        {
            r->error = 8;
            return 0;
        }
        lease = local_allocate(r, h, stackBytes + 4, 0x40);
        if (!lease)
        {
            r->error = 8;
            return 0;
        }
        __win16_write_word(r, h->selector, lease, 0xa55a);
        __win16_write_word(r, h->selector, (u16)(lease + stackBytes + 2), 0x5aa5);
        s->ss = s->ds = h->selector;
        s->sp = (u16)(lease + stackBytes + 2);
    }
    else if (mode == 2)
    {
        Segment *a = segment(r, separate), *d = find_segment(r, s->ds);
        if (!a || !separate || (d && a->backing == d->backing))
        {
            r->error = 8;
            return 0;
        }
        s->ss = separate;
        s->sp = top;
    }
    else if (mode != 0)
    {
        r->error = 8;
        return 0;
    }
    u16 expectedSs = s->ss, expectedSp = s->sp;
    for (u32 i = count; i > 0; i--)
    {
        push(r, s, arguments[i - 1]);
    }
    __win16_program_invoke(r, s, target, 1);
    value = (u32)s->ax | ((u32)(resultMode == 2
        ? s->dx
        : resultMode == 1
            ? 0
            : s->high[0])
                          << 16);
    if (resultMode && (s->ss != expectedSs || s->sp != expectedSp))
    {
        r->error = 8;
    }
    if (lease)
    {
        Segment *d = segment(r, h->selector);
        if (!d || d->bytes[lease] != 0x5a || d->bytes[lease + 1] != 0xa5 ||
            d->bytes[lease + stackBytes + 2] != 0xa5 || d->bytes[lease + stackBytes + 3] != 0x5a)
        {
            r->error = 8;
        }
        local_free(r, h, lease);
    }
    *s = saved;
    return value;
}

static u32 task_callback(Runtime *r, State *s, u32 target, u16 mode, u32 stackBytes, u16 separate,
                         u16 top)
{
    return task_callback_arguments(r, s, target, mode, stackBytes, separate, top, 0, 0, 0);
}
