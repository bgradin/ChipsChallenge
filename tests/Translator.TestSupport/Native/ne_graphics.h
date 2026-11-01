/* Resource/GDI compatibility for original counter code. Fixture addresses and oracle hashes
 * arrive in a source-pinned descriptor; resource pixels are read only from external CHIPS.EXE. */
typedef struct
{
    void *dc;
    int erase;
    NativeRect rect;
    int restore, update;
    u8 reserved[32];
} NativePaint;
_Static_assert(sizeof(NativePaint) == 72, "Win64 PAINTSTRUCT layout");
extern __attribute__((visibility("hidden"))) ABI void *native_CreateFileW(const u16 *, u32, u32,
                                                                          void *, u32, u32, void *);
extern __attribute__((visibility("hidden"))) ABI int native_WriteFile(void *, const void *, u32,
                                                                      u32 *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_ReadFile(void *, void *, u32, u32 *,
                                                                     void *);
extern __attribute__((visibility("hidden"))) ABI int native_GetFileSizeEx(void *, sptr *);
extern __attribute__((visibility("hidden"))) ABI int native_CloseHandle(void *);
extern __attribute__((visibility("hidden"))) ABI int
native_BCryptOpenAlgorithmProvider(void **, const u16 *, const u16 *, u32);
extern __attribute__((visibility("hidden"))) ABI int
native_BCryptHash(void *, void *, u32, const void *, u32, u8 *, u32);
extern __attribute__((visibility("hidden"))) ABI int native_BCryptCloseAlgorithmProvider(void *,
                                                                                         u32);
extern __attribute__((visibility("hidden"))) ABI void *native_BeginPaint(void *, NativePaint *);
extern __attribute__((visibility("hidden"))) ABI int native_EndPaint(void *, const NativePaint *);
extern __attribute__((visibility("hidden"))) ABI int native_SetDIBitsToDevice(void *, int, int, u32,
                                                                              u32, int, int, u32,
                                                                              u32, const void *,
                                                                              const void *, u32);
extern __attribute__((visibility("hidden"))) ABI void *native_CreateCompatibleDC(void *);
extern __attribute__((visibility("hidden"))) ABI int native_DeleteDC(void *);
extern __attribute__((visibility("hidden"))) ABI void *
native_CreateDIBSection(void *, const void *, u32, void **, void *, u32);
extern __attribute__((visibility("hidden"))) ABI void *native_SelectObject(void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_DeleteObject(void *);
extern __attribute__((visibility("hidden"))) ABI int native_IntersectClipRect(void *, int, int, int,
                                                                              int);
extern __attribute__((visibility("hidden"))) ABI int native_SelectClipRgn(void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_GdiFlush(void);
typedef struct
{
    NativePaint native;
    void *hwnd;
    u32 pointer;
    int active, offscreen;
} PaintFrame;
typedef struct
{
    void *algorithm, *file, *source, *memoryDc, *bitmap, *previous, *pixels;
    u32 loaded, locks, initialized, offscreen, width, height, draws, paints;
    const u8 *plan;
#ifdef NE_SCENE
    void *scene;
#ifdef NE_INPUT
    void *input;
#endif
#endif
    PaintFrame frames[8];
} CounterGraphics;

static int paint_token(const u16 **cursor, const u16 **token, u32 *length)
{
    const u16 *p = *cursor;
    while (*p == ' ' || *p == '\t')
    {
        p++;
    }
    if (!*p)
    {
        *cursor = p;
        return 0;
    }
    int quoted = *p == '"';
    if (quoted)
    {
        p++;
    }
    *token = p;
    while (*p && (quoted
        ? *p != '"'
        : *p != ' ' && *p != '\t'))
    {
        if (!quoted && *p == '"')
        {
            return -1;
        }
        p++;
    }
    *length = (u32)(p - *token);
    if (quoted)
    {
        if (!*p)
        {
            return -1;
        }
        p++;
    }
    if (*p && *p != ' ' && *p != '\t')
    {
        return -1;
    }
    *cursor = p;
    return 1;
}

static int paint_option(const u16 *token, u32 length, const char *expected)
{
    u32 i = 0;
    while (expected[i])
    {
        if (i >= length || token[i] != (u16)expected[i])
        {
            return 0;
        }
        i++;
    }
    return i == length;
}

static int paint_arguments(NativeApplication *a)
{
    const u16 *p = native_GetCommandLineW(), *token;
    u32 length;
    if (!p || paint_token(&p, &token, &length) != 1)
    {
        return -1;
    }
    int test = 0, original = 0, diagnostics = 0, status;
#ifdef NE_SCENE
    int levels = 0;
#endif
    while ((status = paint_token(&p, &token, &length)) == 1)
    {
        if (paint_option(token, length, "--lifecycle-test"))
        {
            a->suppressDialog = 1;
            if (test)
            {
                return -1;
            }
            test = 1;
            continue;
        }
#ifdef NE_SCENE
        if (paint_option(token, length, "--level"))
        {
            if (a->requestedLevel || paint_token(&p, &token, &length) != 1 || length != 1 ||
                (token[0] != '1' && token[0] != '2'))
            {
                return -1;
            }
            a->requestedLevel = token[0] - '0';
            continue;
        }
        if (paint_option(token, length, "--levels"))
        {
            if (levels || paint_token(&p, &token, &length) != 1 || !length || length >= 1024)
            {
                return -1;
            }
            levels = 1;
            for (u32 i = 0; i < length; i++)
            {
                a->levelPathStorage[i] = token[i];
            }
            a->levelPathStorage[length] = 0;
            a->levelPath = a->levelPathStorage;
            continue;
        }
#endif
        int source = paint_option(token, length, "--original"),
            log = paint_option(token, length, "--diagnostics");
        if ((!source && !log) || (source && original) || (log && diagnostics))
        {
            return -1;
        }
        if (paint_token(&p, &token, &length) != 1 || !length || length >= 1024)
        {
            return -1;
        }
        u16 *destination = source
            ? a->originalPathStorage
            : a->diagnosticsPathStorage;
        for (u32 i = 0; i < length; i++)
        {
            destination[i] = token[i];
        }
        destination[length] = 0;
        if (source)
        {
            a->originalPath = destination;
            original = 1;
        }
        else
        {
            a->diagnosticsPath = destination;
            diagnostics = 1;
        }
    }
    return status < 0
        ? -1
        : test;
}

static int paint_hash(CounterGraphics *g, const void *data, u32 length, const u8 *expected)
{
    u8 hash[32];
    if (native_BCryptHash(g->algorithm, 0, 0, data, length, hash, 32) < 0)
    {
        return 0;
    }
    for (u32 i = 0; i < 32; i++)
    {
        if (hash[i] != expected[i])
        {
            return 0;
        }
    }
    return 1;
}

static u16 paint_unlock(Runtime *r, u16 handle)
{
    NativeApplication *a = r->user;
    CounterGraphics *g = a
        ? a->graphics
        : 0;
    if (!g || handle != 0x7101 || !g->locks)
    {
        return 0;
    }
    g->locks--;
    return 0;
}

static u32 paint_invoke(WindowHost *h, u32 target)
{
    State saved = h->app->state;
    __win16_call_target(&h->app->runtime, &h->app->state, target, 1);
    u32 result = h->app->state.ax;
    if (h->app->state.sp != saved.sp || h->app->state.ss != saved.ss)
    {
        h->app->runtime.error = 14;
    }
    h->app->state = saved;
    return result;
}

static u32 paint_open(WindowHost *h)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CounterGraphics *g = heap_alloc(&a->storage, sizeof(*g));
    if (!g)
    {
        return 20;
    }
    zero(g, sizeof(*g));
    a->graphics = g;
    g->plan = a->graphicsDescriptor;
    const u8 *d = g->plan;
    u32 size = get32(d), offset = get32(d + 36), length = get32(d + 40);
    if (!a->originalPath)
    {
        return 80;
    }
    if (!size || size > 1024 * 1024 || length < 104 || length > 65536 || offset > size ||
        length > size - offset || find_segment(r, 0xc000))
    {
        return 10;
    }
    u16 algorithm[] = {'S', 'H', 'A', '2', '5', '6', 0};
    if (native_BCryptOpenAlgorithmProvider(&g->algorithm, algorithm, 0, 0) < 0)
    {
        return 20;
    }
    g->file = native_CreateFileW(a->originalPath, 0x80000000, 1, 0, 3, 0x80, 0);
    if (g->file == (void *)-1)
    {
        g->file = 0;
        return 80;
    }
    sptr actual = 0;
    if (!native_GetFileSizeEx(g->file, &actual) || actual != size)
    {
        return 81;
    }
    g->source = heap_alloc(&a->storage, size);
    if (!g->source)
    {
        return 20;
    }
    u32 read = 0;
    if (!native_ReadFile(g->file, g->source, size, &read, 0) || read != size ||
        !paint_hash(g, g->source, size, d + 4))
    {
        return 81;
    }
    if (!native_CloseHandle(g->file))
    {
        return 50;
    }
    g->file = 0;
    if (!paint_hash(g, (u8 *)g->source + offset, length, d + 44))
    {
        return 82;
    }
    Segment *resource = map(r, 0xc000, length);
    if (!resource)
    {
        return 20;
    }
    for (u32 i = 0; i < length; i++)
    {
        resource->bytes[i] = ((u8 *)g->source)[offset + i];
    }
    resource->permissions = 1;
#ifndef NE_SCENE
    heap_release(&a->storage, g->source);
    g->source = 0;
#endif
    const u8 *b = resource->bytes;
    if (get32(b) != 40 || get32(b + 4) != 17 || get32(b + 8) != 552 || get16(b + 12) != 1 ||
        get16(b + 14) != 4 || get32(b + 16) || length < 104 + 24 * 276)
    {
        return 82;
    }
    u8 *instance = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 84), 2, 1);
    if (!instance)
    {
        return 10;
    }
    put16(instance, a->instance);
    if (!paint_invoke(h, get32(d + 76)) || r->error)
    {
        return 30;
    }
    u8 *handle = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 88), 2, 0);
    u8 *pointer = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 90), 4, 0);
    u8 *array = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 92), 48, 0);
    if (!handle || !pointer || !array || get16(handle) != 0x7101 || get32(pointer) != 0xc0000000)
    {
        return 40;
    }
    for (u32 i = 0; i < 24; i++)
    {
        if (get16(array + i * 2) != 104 + i * 276)
        {
            return 40;
        }
    }
    g->initialized = 1;
    return 0;
}

static int paint_client_rect(WindowHost *h, WindowIdentity *w, NativeRect *rect)
{
    CounterGraphics *g = h->app->graphics;
    if (g && g->offscreen)
    {
        rect->left = rect->top = 0;
        rect->right = g->width;
        rect->bottom = g->height;
        return w != 0;
    }
    return w && native_GetClientRect(w->native, rect);
}

static u32 paint_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CounterGraphics *g = a->graphics;
    if (!g)
    {
        r->error = 14;
        return 0;
    }
    if (offset == WINDOW_FindResource)
    {
        *cleanup = 10;
        u32 name = arg(r, s, 2) | (u32)arg(r, s, 3) << 16, type = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
        return arg(r, s, 4) == a->instance && name == get16(g->plan + 94) && type == 2
            ? 0x7001
            : 0;
    }
    if (offset == WINDOW_LoadResource)
    {
        *cleanup = 4;
        if (arg(r, s, 1) != a->instance || arg(r, s, 0) != 0x7001)
        {
            return 0;
        }
        g->loaded = 1;
        return 0x7101;
    }
    if (offset == WINDOW_LockResource)
    {
        *cleanup = 2;
        if (!g->loaded || arg(r, s, 0) != 0x7101)
        {
            return 0;
        }
        g->locks++;
        return 0xc0000000;
    }
    if (offset == WINDOW_FreeResource)
    {
        *cleanup = 2;
        if (arg(r, s, 0) != 0x7101 || g->locks)
        {
            r->error = 14;
            return 0;
        }
        g->loaded = 0;
        return 0;
    }
    if (offset == WINDOW_BeginPaint)
    {
        *cleanup = 6;
        u32 ptr = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
        u8 *p = window_memory(r, ptr, 32, 1);
        PaintFrame *f = h->active
            ? &g->frames[h->active - 1]
            : 0;
        if (!p || !f || f->active || arg(r, s, 2) == 0 || h->current.message != 0xf)
        {
            r->error = 14;
            return 0;
        }
        WindowIdentity *w = window_id(h, arg(r, s, 2));
        if (!w || w->native != h->current.hwnd)
        {
            r->error = 14;
            return 0;
        }
        zero(f, sizeof(*f));
        f->hwnd = w->native;
        f->pointer = ptr;
        f->offscreen = g->offscreen;
        if (!f->offscreen && !native_BeginPaint(w->native, &f->native))
        {
            r->error = 14;
            return 0;
        }
        f->active = 1;
        zero(p, 32);
        put16(p, (u16)(0x200 + h->active - 1));
        NativeRect rect;
        if (f->offscreen)
        {
            if (!paint_client_rect(h, w, &rect))
            {
                r->error = 14;
                return 0;
            }
        }
        else
        {
            rect = f->native.rect;
            put16(p + 2, f->native.erase != 0);
            put16(p + 12, f->native.restore != 0);
            put16(p + 14, f->native.update != 0);
        }
        if (!word_signed(rect.left) || !word_signed(rect.top) || !word_signed(rect.right) ||
            !word_signed(rect.bottom))
        {
            r->error = 14;
            return 0;
        }
        put16(p + 4, (u16)rect.left);
        put16(p + 6, (u16)rect.top);
        put16(p + 8, (u16)rect.right);
        put16(p + 10, (u16)rect.bottom);
        g->paints++;
        return get16(p);
    }
    if (offset == WINDOW_EndPaint)
    {
        *cleanup = 6;
        u32 ptr = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
        PaintFrame *f = h->active
            ? &g->frames[h->active - 1]
            : 0;
        WindowIdentity *w = window_id(h, arg(r, s, 2));
        u8 *p = window_memory(r, ptr, 32, 0);
        if (!f || !w || w->native != f->hwnd || !p || get16(p) != (u16)(0x200 + h->active - 1) ||
            !f->active || f->pointer != ptr ||
            (!f->offscreen && !native_EndPaint(f->hwnd, &f->native)))
        {
            r->error = 14;
            return 0;
        }
        f->active = 0;
        return 1;
    }
    *cleanup = 28;
    u16 id = arg(r, s, 13);
    if (id < 0x200 || id >= 0x208)
    {
        r->error = 14;
        return 0;
    }
    PaintFrame *f = &g->frames[id - 0x200];
    u32 info = arg(r, s, 1) | (u32)arg(r, s, 2) << 16, bits = arg(r, s, 3) | (u32)arg(r, s, 4) << 16;
    Segment *resource = find_segment(r, 0xc000);
    u32 start = (u16)bits;
    if (!g->loaded || !g->locks || !f->active || info != 0xc0000000 || (bits >> 16) != 0xc000 ||
        !resource || start < 104 || (start - 104) % 276 || (start - 104) / 276 >= 24 ||
        start > resource->size - 276 || arg(r, s, 0) || arg(r, s, 5) != 23 || arg(r, s, 6) ||
        arg(r, s, 7) || arg(r, s, 8) || arg(r, s, 9) != 23 || arg(r, s, 10) != 17)
    {
        r->error = 14;
        return 0;
    }
    int rows = native_SetDIBitsToDevice(f->offscreen
        ? g->memoryDc
        : f->native.dc,
                                        (short)arg(r, s, 12), (short)arg(r, s, 11), 17, 23, 0, 0, 0,
                                        23, resource->bytes + start, resource->bytes, 0);
    if (rows != 23)
    {
        r->error = 14;
        return 0;
    }
    g->draws++;
    return (u32)rows;
}

static u32 paint_verify(WindowHost *h, WindowIdentity *w)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CounterGraphics *g = a->graphics;
    const u8 *d = g->plan;
    u32 count = get32(d + 96);
    for (u32 c = 0; c < count; c++)
    {
        const u8 *p = d + 100 + c * 54;
        g->width = get16(p + 6);
        g->height = get16(p + 8);
        if (!g->width || g->width > 512 || !g->height || g->height > 512)
        {
            return 10;
        }
        g->memoryDc = native_CreateCompatibleDC(0);
        if (!g->memoryDc)
        {
            return 20;
        }
        u8 bmi[40];
        zero(bmi, 40);
        put32(bmi, 40);
        put32(bmi + 4, g->width);
        put32(bmi + 8, (u32) - (int)g->height);
        put16(bmi + 12, 1);
        put16(bmi + 14, 32);
        g->bitmap = native_CreateDIBSection(g->memoryDc, bmi, 0, &g->pixels, 0, 0);
        if (!g->bitmap || !g->pixels)
        {
            return 20;
        }
        g->previous = native_SelectObject(g->memoryDc, g->bitmap);
        if (!g->previous || g->previous == (void *)-1)
        {
            g->previous = 0;
            return 20;
        }
        w->words[0] = get16(p);
        w->words[1] = get16(p + 2);
        u8 *color = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 86), 2, 1);
        if (!color)
        {
            return 10;
        }
        put16(color, get16(p + 4));
        for (u32 repeat = 0; repeat < 2; repeat++)
        {
            for (u32 i = 0; i < g->width * g->height; i++)
            {
                ((u32 *)g->pixels)[i] = get32(p + 18) & 0xffffff;
            }
            if (native_SelectClipRgn(g->memoryDc, 0) == 0 ||
                native_IntersectClipRect(g->memoryDc, (short)get16(p + 10), (short)get16(p + 12),
                                         (short)get16(p + 14), (short)get16(p + 16)) == 0)
            {
                return 30;
            }
            g->offscreen = 1;
            window_dispatch(h, w->native, 0xf, 0, 0);
            g->offscreen = 0;
            if (h->result || r->error)
            {
                return h->result
                    ? h->result
                    : 30;
            }
            if (!native_GdiFlush())
            {
                return 30;
            }
            for (u32 i = 0; i < g->width * g->height; i++)
            {
                ((u32 *)g->pixels)[i] &= 0xffffff;
            }
            if (!paint_hash(g, g->pixels, g->width * g->height * 4, p + 22))
            {
                return 40;
            }
        }
        if (native_SelectObject(g->memoryDc, g->previous) == (void *)-1)
        {
            return 50;
        }
        g->previous = 0;
        if (!native_DeleteObject(g->bitmap) || !native_DeleteDC(g->memoryDc))
        {
            return 50;
        }
        g->bitmap = 0;
        g->memoryDc = 0;
        g->pixels = 0;
    }
    w->words[0] = 123;
    w->words[1] = 0;
    u8 *color = window_memory(r, ((u32)h->automaticDs << 16) | get16(d + 86), 2, 1);
    if (!color)
    {
        return 10;
    }
    put16(color, 4);
    return g->draws == count * 6
        ? 0
        : 40;
}

static u32 paint_abort_frames(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    if (!g)
    {
        return 0;
    }
    u32 result = 0;
    for (u32 i = 0; i < 8; i++)
    {
        if (g->frames[i].active)
        {
            if (!g->frames[i].offscreen &&
                !native_EndPaint(g->frames[i].hwnd, &g->frames[i].native))
            {
                result = 50;
            }
            g->frames[i].active = 0;
        }
    }
    return result;
}

static u32 paint_close(WindowHost *h)
{
    NativeApplication *a = h->app;
    CounterGraphics *g = a->graphics;
    if (!g)
    {
        return 0;
    }
    u32 result = 0;
    if (paint_abort_frames(h))
    {
        result = 50;
    }
    if (g->initialized)
    {
        u32 previousError = a->runtime.error;
        a->runtime.error = 0;
        paint_invoke(h, get32(g->plan + 80));
        if (a->runtime.error || g->locks || g->loaded)
        {
            result = 50;
        }
        if (previousError)
        {
            a->runtime.error = previousError;
        }
    }
    if (g->previous && g->memoryDc)
    {
        native_SelectObject(g->memoryDc, g->previous);
    }
    if (g->bitmap && !native_DeleteObject(g->bitmap))
    {
        result = 50;
    }
    if (g->memoryDc && !native_DeleteDC(g->memoryDc))
    {
        result = 50;
    }
    if (g->file && !native_CloseHandle(g->file))
    {
        result = 50;
    }
    if (g->algorithm && native_BCryptCloseAlgorithmProvider(g->algorithm, 0) < 0)
    {
        result = 50;
    }
    if (g->source)
    {
        heap_release(&a->storage, g->source);
    }
    if (find_segment(&a->runtime, 0xc000))
    {
        unmap(&a->runtime, 0xc000);
    }
    heap_release(&a->storage, g);
    a->graphics = 0;
    return result;
}
