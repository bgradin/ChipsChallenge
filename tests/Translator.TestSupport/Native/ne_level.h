/* Descriptor-selected original initialization. This supplies OS file/string/timer
 * services only; all level decoding and gameplay fields come from translated code. */
typedef struct
{
    void *window;
    uptr nativeId;
    u32 id, interval, active, remainder, pending;
} LevelTimer;
typedef struct
{
    u8 *bytes;
    void *file;
    u32 size, position, open, virtualClock, probe;
    LevelTimer timers[2];
} LevelFile;

static LevelTimer *level_timer(LevelFile *f, void *window, u32 id)
{
    for (u32 i = 0; i < 2; i++)
    {
        if (f->timers[i].window == window && f->timers[i].id == id)
        {
            return &f->timers[i];
        }
    }
    return 0;
}

static u32 level_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    LevelFile *f = a->level;
    if (!f)
    {
        r->error = 14;
        return 0;
    }
    if (offset == 0x300)
    {
        *cleanup = 10;
        u32 name = window_arg32(r, s, 3);
        const u8 *expected = a->levelDescriptor + 72;
        for (u32 i = 0; i < 16; i++)
        {
            u8 *p = window_memory(r, name + i, 1, 0);
            if (!p)
            {
                return 0;
            }
            u8 c = *p;
            if (c >= 'a' && c <= 'z')
            {
                c -= 32;
            }
            if (c != expected[i])
            {
                return 0xffff;
            }
            if (!c)
            {
                break;
            }
        }
        if (f->open || arg(r, s, 0) != 0x20)
        {
            return 0xffff;
        }
        u8 *of = window_memory(r, window_arg32(r, s, 1), 136, 1);
        if (!of)
        {
            return 0xffff;
        }
        zero(of, 136);
        of[0] = 136;
        f->open = 1;
        f->position = 0;
        return 1;
    }
    if (offset == 0x310)
    {
        *cleanup = 8;
        u32 src = window_arg32(r, s, 0), dst = window_arg32(r, s, 2), length = 0;
        for (; length < 256; length++)
        {
            u8 *p = window_memory(r, src + length, 1, 0);
            if (!p)
            {
                return 0;
            }
            if (!*p)
            {
                break;
            }
        }
        if (length == 256)
        {
            r->error = 14;
            return 0;
        }
        u8 *from = window_memory(r, src, length + 1, 0), *to = window_memory(r, dst, length + 1, 1);
        if (!from || !to)
        {
            return 0;
        }
        for (u32 i = 0; i <= length; i++)
        {
            to[i] = from[i];
        }
        return dst;
    }
    if (offset == 0x314)
    {
        *cleanup = 10;
        WindowIdentity *w = window_id(h, arg(r, s, 4));
        u32 id = arg(r, s, 3), interval = arg(r, s, 2);
        if (!w || window_arg32(r, s, 0) || id < 1 || id > 2 || interval != (id == 1
            ? 110u
            : 220u))
        {
            r->error = 14;
            return 0;
        }
        LevelTimer *timer = &f->timers[id - 1];
        uptr nativeId = 0;
        if (!f->virtualClock)
        {
            nativeId = native_SetTimer(w->native, id, interval, 0);
            if (!nativeId)
            {
                return 0;
            }
            if (nativeId != id)
            {
                native_KillTimer(w->native, nativeId);
                r->error = 14;
                return 0;
            }
        }
        timer->window = w->native;
        timer->nativeId = nativeId;
        timer->id = id;
        timer->interval = interval;
        timer->active = 1;
        timer->remainder = timer->pending = 0;
        return id;
    }
    if (offset == 0x320)
    {
        *cleanup = 4;
        WindowIdentity *w = window_id(h, arg(r, s, 1));
        if (!w)
        {
            r->error = 14;
            return 0;
        }
        LevelTimer *timer = level_timer(f, w->native, arg(r, s, 0));
        if (!timer || !timer->active)
        {
            return 0;
        }
        if (timer->nativeId && !native_KillTimer(timer->window, timer->nativeId))
        {
            r->error = 14;
            return 0;
        }
        timer->nativeId = 0;
        timer->active = timer->pending = timer->remainder = 0;
        return 1;
    }
    *cleanup = offset == 0x304
        ? 2
        : 8;
    if (!f->open || arg(r, s, offset == 0x304
        ? 0
        : 3) != 1)
    {
        return offset == 0x30c
            ? 0xffffffff
            : 0xffff;
    }
    if (offset == 0x304)
    {
        f->open = 0;
        return 0;
    }
    if (offset == 0x30c)
    {
        long long origin = arg(r, s, 0) == 0
            ? 0
            : arg(r, s, 0) == 1
                    ? f->position
                    : arg(r, s, 0) == 2
                        ? f->size
                        : -0x100000000ll;
        long long at = origin + (int)window_arg32(r, s, 1);
        if (at < 0 || at > f->size)
        {
            return 0xffffffff;
        }
        f->position = (u32)at;
        return f->position;
    }
    u32 count = arg(r, s, 0);
    if (count > f->size - f->position)
    {
        count = f->size - f->position;
    }
    u8 *dest = window_memory(r, window_arg32(r, s, 1), count, 1);
    if (!dest)
    {
        return 0xffff;
    }
    for (u32 i = 0; i < count; i++)
    {
        dest[i] = f->bytes[f->position + i];
    }
    f->position += count;
    return count;
}

static u32 level_virtualize(WindowHost *h)
{
    LevelFile *f = h->app->level;
    if (!f)
    {
        return 0;
    }
    u32 result = 0;
    for (u32 i = 0; i < 2; i++)
    {
        LevelTimer *t = &f->timers[i];
        if (t->nativeId && !native_KillTimer(t->window, t->nativeId))
        {
            result = 50;
        }
        t->nativeId = 0;
    }
    f->virtualClock = 1;
    return result;
}

static u32 level_stop(WindowHost *h)
{
    LevelFile *f = h->app->level;
    if (!f)
    {
        return 0;
    }
    u32 result = 0;
    for (u32 i = 0; i < 2; i++)
    {
        LevelTimer *t = &f->timers[i];
        if (t->nativeId && !native_KillTimer(t->window, t->nativeId))
        {
            result = 50;
        }
        t->nativeId = 0;
        t->active = t->pending = 0;
    }
    return result;
}
/* Timer callbacks never reenter any active translated callback. Win32-style
 * coalescing retains one pending notification per timer, then queues it later. */

static int level_timer_gate(WindowHost *h, void *window, u32 message, uptr id)
{
    if (message != 0x113 || !h->app->level)
    {
        return 0;
    }
    LevelTimer *t = level_timer(h->app->level, window, (u32)id);
    if (!t)
    {
        return 0;
    }
    if (!t->active)
    {
        return 1;
    }
    if (h->active)
    {
        t->pending = 1;
        h->app->clockDeferred++;
        return 1;
    }
    h->app->clockDelivered++;
    return 0;
}

static void level_flush_deferred(WindowHost *h)
{
    LevelFile *f = h->app->level;
    if (!f || h->active)
    {
        return;
    }
    for (u32 i = 0; i < 2; i++)
    {
        LevelTimer *t = &f->timers[i];
        if (!t->pending)
        {
            continue;
        }
        t->pending = 0;
        if (t->active && !h->destroying && !h->result)
        {
            if (!native_PostMessageW(t->window, 0x113, t->id, 0))
            {
                h->result = 60;
            }
            else
            {
                h->app->clockQueued++;
                h->app->clockPosted++;
            }
        }
    }
}

static void level_clock_snapshot(WindowHost *h)
{
    NativeApplication *a = h->app;
    if (!a->clockDescriptor)
    {
        return;
    }
    Segment *s = find_segment(&a->runtime, h->automaticDs);
    if (!s)
    {
        return;
    }
    const u8 *p = a->clockDescriptor + 8;
    u32 count = get32(a->clockDescriptor + 4);
    for (u32 i = 0; i < count; i++, p += 20)
    {
        u32 offset = get16(p);
        if (get16(p + 2) != 2 || offset > s->size - 2)
        {
            return;
        }
        a->clockValues[i] = get16(s->bytes + offset);
    }
    a->clockCaptured = 1;
}

static u32 level_close(WindowHost *h)
{
    LevelFile *f = h->app->level;
    if (!f)
    {
        return 0;
    }
    level_clock_snapshot(h);
    u32 result = level_stop(h);
    if (f->file && !native_CloseHandle(f->file))
    {
        result = 50;
    }
    if (f->open)
    {
        result = 50;
    }
    if (f->bytes)
    {
        heap_release(&h->app->storage, f->bytes);
    }
    heap_release(&h->app->storage, f);
    h->app->level = 0;
    return result;
}

static u32 level_open(WindowHost *h)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CounterGraphics *g = a->graphics;
    const u8 *p = a->levelDescriptor;
    if (!p)
    {
        return 0;
    }
    LevelFile *f = heap_alloc(&a->storage, sizeof(*f));
    if (!f)
    {
        return 20;
    }
    zero(f, sizeof(*f));
    a->level = f;
    f->size = get32(p);
    if (!a->levelPath && a->gameplay && a->originalPath)
    {
        // Path resolution only. Level parsing and selection stay in original code.
        u32 prefix = 0, length = 0;
        while (a->originalPath[length])
        {
            if (a->originalPath[length] == '\\' || a->originalPath[length] == '/')
            {
                prefix = length + 1;
            }
            length++;
        }
        const u8 *name = p + 72;
        u32 n = 0;
        while (n < 16 && name[n])
        {
            n++;
        }
        if (!n || n == 16 || prefix + n >= 1024)
        {
            return 84;
        }
        for (u32 i = 0; i < prefix; i++)
        {
            a->levelPathStorage[i] = a->originalPath[i];
        }
        for (u32 i = 0; i < n; i++)
        {
            a->levelPathStorage[prefix + i] = name[i];
        }
        a->levelPathStorage[prefix + n] = 0;
        a->levelPath = a->levelPathStorage;
    }
    if (!a->levelPath)
    {
        return 84;
    }
    f->file = native_CreateFileW(a->levelPath, 0x80000000, 1, 0, 3, 0x80, 0);
    if (f->file == (void *)-1)
    {
        f->file = 0;
        return 84;
    }
    sptr size = 0;
    if (!native_GetFileSizeEx(f->file, &size) || size != f->size)
    {
        return 85;
    }
    f->bytes = heap_alloc(&a->storage, f->size);
    if (!f->bytes)
    {
        return 20;
    }
    u32 got = 0;
    if (!native_ReadFile(f->file, f->bytes, f->size, &got, 0) || got != f->size ||
        !paint_hash(g, f->bytes, f->size, p + 4))
    {
        return 85;
    }
    if (!native_CloseHandle(f->file))
    {
        return 50;
    }
    f->file = 0;
    p += 88;
    u32 calls = get32(p);
    p += 4;
    const u8 *callList = p;
    for (u32 i = 0; i < calls; i++)
    {
        u32 args = get32(p + 4);
        p += 8 + args * 2;
    }
    u32 bindings = get32(p);
    p += 4;
    for (u32 i = 0; i < bindings; i++)
    {
        u32 address = get16(p), window = get16(p + 2);
        p += 4;
        if (window >= h->nextId)
        {
            return 10;
        }
        u8 *dest = window_memory(r, ((u32)h->automaticDs << 16) | address, 2, 1);
        if (!dest)
        {
            return 10;
        }
        put16(dest, h->windows[window].id);
    }
    const u8 *post = p;
    for (u32 i = 0; i < calls; i++)
    {
        u32 target = get32(callList), args = get32(callList + 4);
        callList += 8;
        State saved = a->state;
        a->state.ds = a->state.ss = h->automaticDs;
        a->state.sp = 0x7fe0;
        for (u32 j = args; j > 0; j--)
        {
            push(r, &a->state, get16(callList + (j - 1) * 2));
        }
        callList += args * 2;
        __win16_call_target(r, &a->state, target, 1);
        if (r->error)
        {
            return 30;
        }
        if (a->state.sp != 0x7fe0 - args * 2)
        {
            return 40;
        }
        a->state = saved;
    }
    u32 count = get32(post);
    post += 4;
    for (u32 i = 0; i < count; i++)
    {
        u32 offset = get16(post), length = get16(post + 2);
        post += 4;
        u8 *dest = window_memory(r, ((u32)h->automaticDs << 16) | offset, length, 1);
        if (!dest)
        {
            return 10;
        }
        for (u32 j = 0; j < length; j++)
        {
            dest[j] = post[j];
        }
        post += length;
    }
    u32 boardOffset = get16(a->levelDescriptor + 68), boardLength = get16(a->levelDescriptor + 70);
    u8 *board = window_memory(r, ((u32)h->automaticDs << 16) | boardOffset, boardLength, 0);
    if (!board || f->open || !f->timers[0].active ||
        !paint_hash(g, board, boardLength, a->levelDescriptor + 36))
    {
        return 40;
    }
    count = get32(post);
    post += 4;
    for (u32 i = 0; i < count; i++)
    {
        u32 offset = get16(post), length = get16(post + 2);
        post += 4;
        u8 *actual = window_memory(r, ((u32)h->automaticDs << 16) | offset, length, 0);
        if (!actual)
        {
            return 10;
        }
        for (u32 j = 0; j < length; j++)
        {
            if (actual[j] != post[j])
            {
                return 40;
            }
        }
        post += length;
    }
    return 0;
}

extern __attribute__((visibility("hidden"))) ABI int
native_ScrollWindow(void *, int, int, const NativeRect *, const NativeRect *);
extern __attribute__((visibility("hidden"))) ABI int native_ValidateRect(void *,
                                                                         const NativeRect *);

static u32 movement_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    Runtime *r = &h->app->runtime;
    int scroll = offset == 0x318;
    *cleanup = scroll
        ? 14
        : 6;
    WindowIdentity *w = window_id(h, arg(r, s, scroll
        ? 6
        : 2));
    if (!w)
    {
        r->error = 14;
        return 0;
    }
    u32 ptr = window_arg32(r, s, scroll
        ? 2
        : 0);
    NativeRect rect, *rectangle = 0;
    if (ptr)
    {
        u8 *p = window_memory(r, ptr, 8, 0);
        if (!p)
        {
            return 0;
        }
        rect.left = (short)get16(p);
        rect.top = (short)get16(p + 2);
        rect.right = (short)get16(p + 4);
        rect.bottom = (short)get16(p + 6);
        rectangle = &rect;
    }
    if (!scroll)
    {
        return native_ValidateRect(w->native, rectangle) != 0;
    }
    if (window_arg32(r, s, 0))
    {
        r->error = 14;
        return 0;
    } /* observed original path has NULL clip */
    return native_ScrollWindow(w->native, (short)arg(r, s, 5), (short)arg(r, s, 4), rectangle, 0) !=
        0;
}
