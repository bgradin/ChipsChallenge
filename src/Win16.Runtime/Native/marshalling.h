/* API buffers are contiguous validated ranges. Never widen a 16:16 pointer or a Win16 handle. */
typedef struct
{
    short x, y;
} Point16;
typedef struct
{
    short left, top, right, bottom;
} Rect16;

static u8 *api_buffer(Runtime *r, u32 pointer, u32 length, int write)
{
    if (r->error)
    {
        return (u8 *)0;
    }
    if (!pointer && !length)
    {
        return (u8 *)0;
    }
    Segment *s = find_segment(r, (u16)(pointer >> 16));
    u32 off = (u16)pointer;
    if (write && s && !writable(r, s, (u16)off, length))
    {
        if (r->error != 15)
        {
            r->error = 9;
        }
        return 0;
    }
    if (!pointer || !s || (write
        ? !(s->permissions & 2)
        : !(s->permissions & 1)) ||
        length > s->size || off > s->size - length)
    {
        r->error = 9;
        return (u8 *)0;
    }
    return s->bytes + off;
}

static u8 *copy_ansi(Runtime *r, u32 pointer, u32 bound)
{
    u8 *p = api_buffer(r, pointer, 0, 0);
    if (!p)
    {
        return (u8 *)0;
    }
    Segment *s = find_segment(r, (u16)(pointer >> 16));
    u32 available = s->size - (u16)pointer;
    if (bound > available)
    {
        bound = available;
    }
    for (u32 i = 0; i < bound; i++)
    {
        if (!p[i])
        {
            u8 *copy = r->alloc(r->storage, i + 1);
            if (!copy)
            {
                r->error = 9;
                return copy;
            }
            for (u32 n = 0; n <= i; n++)
            {
                copy[n] = p[n];
            }
            return copy;
        }
    }
    r->error = 9;
    return (u8 *)0;
}

static int copy_out(Runtime *r, u32 pointer, const u8 *source, u32 count)
{
    u8 *p = api_buffer(r, pointer, count, 1);
    if (r->error)
    {
        return 0;
    }
    for (u32 i = 0; i < count; i++)
    {
        p[i] = source[i];
    }
    return 1;
}

static short signed_word(const u8 *p)
{
    return (short)(p[0] | ((u16)p[1] << 8));
}

static void store_word(u8 *p, short value)
{
    p[0] = (u8)value;
    p[1] = (u8)((u16)value >> 8);
}

static int read_rect(Runtime *r, u32 pointer, Rect16 *rect)
{
    u8 *p = api_buffer(r, pointer, 8, 0);
    if (!p)
    {
        return 0;
    }
    rect->left = signed_word(p);
    rect->top = signed_word(p + 2);
    rect->right = signed_word(p + 4);
    rect->bottom = signed_word(p + 6);
    return 1;
}

static int write_rect(Runtime *r, u32 pointer, const Rect16 *rect)
{
    u8 *p = api_buffer(r, pointer, 8, 1);
    if (!p)
    {
        return 0;
    }
    store_word(p, rect->left);
    store_word(p + 2, rect->top);
    store_word(p + 4, rect->right);
    store_word(p + 6, rect->bottom);
    return 1;
}

static int read_point(Runtime *r, u32 pointer, Point16 *point)
{
    u8 *p = api_buffer(r, pointer, 4, 0);
    if (!p)
    {
        return 0;
    }
    point->x = signed_word(p);
    point->y = signed_word(p + 2);
    return 1;
}

static int write_point(Runtime *r, u32 pointer, const Point16 *point)
{
    u8 *p = api_buffer(r, pointer, 4, 1);
    if (!p)
    {
        return 0;
    }
    store_word(p, point->x);
    store_word(p + 2, point->y);
    return 1;
}

typedef struct NativeHandle
{
    struct NativeHandle *next;
    u16 id;
    unsigned long long value;
} NativeHandle;
typedef void (*DestroyHandle)(unsigned long long);
typedef struct
{
    Runtime *runtime;
    NativeHandle *entries;
    u32 next;
    DestroyHandle destroy;
} HandleTable;

static u16 add_handle(HandleTable *t, unsigned long long value)
{
    if (!t->next)
    {
        t->next = 1;
    }
    if (t->next > 65535)
    {
        return 0;
    }
    NativeHandle *h = t->runtime->alloc(t->runtime->storage, sizeof(*h));
    if (!h)
    {
        return 0;
    }
    h->id = (u16)t->next++;
    h->value = value;
    h->next = t->entries;
    t->entries = h;
    return h->id;
}

static unsigned long long get_handle(HandleTable *t, u16 id)
{
    for (NativeHandle *h = t->entries; h; h = h->next)
    {
        if (h->id == id)
        {
            return h->value;
        }
    }
    t->runtime->error = 10;
    return 0;
}

static int remove_handle(HandleTable *t, u16 id)
{
    NativeHandle **p = &t->entries;
    while (*p)
    {
        NativeHandle *h = *p;
        if (h->id == id)
        {
            *p = h->next;
            if (t->destroy)
            {
                t->destroy(h->value);
            }
            t->runtime->release(t->runtime->storage, h);
            return 1;
        }
        p = &h->next;
    }
    return 0;
}

static void destroy_handles(HandleTable *t)
{
    while (t->entries)
    {
        remove_handle(t, t->entries->id);
    }
}
