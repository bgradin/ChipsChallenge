/* Windows-only native forwarding and offscreen acceptance, compiled into the graphics helper
 * variant. */
extern __attribute__((visibility("hidden"))) ABI void *native_CreateCompatibleDC(void *);
typedef struct
{
    u32 size;
    int width, height;
    u16 planes, bitCount;
    u32 compression, sizeImage;
    int xPels, yPels;
    u32 used, important, color;
} BitmapInfo;
extern __attribute__((visibility("hidden"))) ABI void *
native_CreateDIBSection(void *, BitmapInfo *, u32, void **, void *, u32);
extern __attribute__((visibility("hidden"))) ABI void *native_SelectObject(void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_DeleteObject(void *);
extern __attribute__((visibility("hidden"))) ABI int native_DeleteDC(void *);
extern __attribute__((visibility("hidden"))) ABI int native_GdiFlush(void);
extern __attribute__((visibility("hidden"))) ABI int native_SetViewportOrgEx(void *, int, int,
                                                                             void *);
extern __attribute__((visibility("hidden"))) ABI int native_PatBlt(void *, int, int, int, int, u32);
extern __attribute__((visibility("hidden"))) ABI u16 translated_IsPointVisible(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI u16 translated_InvertViewportTile(Runtime *,
                                                                                   State *);
typedef struct
{
    HandleTable dcs;
    u32 calls;
} GdiContext;

static void dispatch_patblt(Runtime *r, State *s)
{
    s->ax = 0;
    GdiContext *g = r->gdi;
    if (!g)
    {
        return;
    }
    u32 rop = arg(r, s, ARG_PatBlt_rop) | ((u32)arg(r, s, ARG_PatBlt_rop + 1) << 16);
    if (rop != 0x00550009)
    {
        return;
    }
    u16 id = arg(r, s, ARG_PatBlt_hdc);
    NativeHandle *h = g->dcs.entries;
    while (h && h->id != id)
    {
        h = h->next;
    }
    if (!h)
    {
        return;
    }
    g->calls++;
    s->ax = native_PatBlt((void *)h->value, (short)arg(r, s, ARG_PatBlt_x),
                          (short)arg(r, s, ARG_PatBlt_y), (short)arg(r, s, ARG_PatBlt_width),
                          (short)arg(r, s, ARG_PatBlt_height), rop)
        ? 1
        : 0;
}

static u32 pixel_pattern(u32 x, u32 y)
{
    return (0x123456u + x * 0x010203u + y * 0x030107u) & 0xFFFFFFu;
}

ABI NOINLINE int graphics_acceptance(void)
{
    FixtureStorage storage;
    Runtime runtime;
    Runtime *r = &runtime;
    State state;
    State *s = &state;
    init(r, s, &storage);
    void *dc = 0, *bitmap = 0, *old = 0, *bits = 0;
    int result = 0;
    GdiContext context;
    zero(&context, sizeof(context));
    context.dcs.runtime = r;
    context.dcs.next = 1;
    r->gdi = &context;
    CodeBinding onscreen = {0, 0x4000, 0x100, 1, translated_IsPointVisible};
    CodeBinding invert = {&onscreen, 0x4000, 0x200, 1, translated_InvertViewportTile};
    r->code = &invert;
    dc = native_CreateCompatibleDC(0);
    if (!dc)
    {
        result = 110;
        goto cleanup;
    }
    BitmapInfo info;
    zero(&info, sizeof(info));
    info.size = 40;
    info.width = 64;
    info.height = -64;
    info.planes = 1;
    info.bitCount = 32;
    bitmap = native_CreateDIBSection(dc, &info, 0, &bits, 0, 0);
    if (!bitmap || !bits)
    {
        result = 111;
        goto cleanup;
    }
    old = native_SelectObject(dc, bitmap);
    if (!old || old == (void *)-1)
    {
        result = 112;
        goto cleanup;
    }
    u16 hdc = add_handle(&context.dcs, (unsigned long long)dc);
    if (!hdc)
    {
        result = 113;
        goto cleanup;
    }
    word(r, 0x0040, 0x3000);
    word(r, 0x3000, 3);
    word(r, 0x3002, 4);
    word(r, 0x3004, 4);
    word(r, 0x3006, 4);
    for (u32 test = 0; test < 5; test++)
    {
        int origin = test == 1
            ? 16
            : 0;
        if (!native_SetViewportOrgEx(dc, origin, origin, 0) || !native_GdiFlush())
        {
            result = 114;
            goto cleanup;
        }
        volatile u32 *p = bits;
        for (u32 y = 0; y < 64; y++)
        {
            for (u32 x = 0; x < 64; x++)
            {
                p[y * 64 + x] = pixel_pattern(x, y);
            }
        }
        word(r, 0x3008, test == 1
            ? 1
            : 0);
        word(r, 0x300A, 0);
        context.calls = 0;
        u16 x = test == 1
            ? 3
            : test == 2
                    ? 2
                    : 4,
            y = test == 1
                ? 4
                : 5;
        u32 repetitions = test == 3
            ? 2
            : 1;
        for (u32 n = 0; n < repetitions; n++)
        {
            push(r, s, y);
            push(r, s, x);
            push(r, s, test == 4
                ? 0xFFFF
                : hdc);
            __win16_call_target(r, s, 0x40000200, 1);
            s->sp = (u16)(s->sp + 6);
            if (r->error || !balanced(s) || s->ax != (test == 2 || test == 4
                ? 0
                : 1))
            {
                result = 115;
                goto cleanup;
            }
        }
        if (context.calls != (test == 2 || test == 4
            ? 0
            : repetitions) || !native_GdiFlush())
        {
            result = 116;
            goto cleanup;
        }
        for (int py = 0; py < 64; py++)
        {
            for (int px = 0; px < 64; px++)
            {
                int changed = test == 0
                    ? (px >= 32 && py >= 32)
                    : test == 1
                            ? (px < 16 && py >= 16 && py < 48)
                            : 0;
                u32 expected = pixel_pattern((u32)px, (u32)py);
                if (changed)
                {
                    expected ^= 0xFFFFFF;
                }
                if ((p[py * 64 + px] & 0xFFFFFF) != expected)
                {
                    result = 117;
                    goto cleanup;
                }
            }
        }
    }
    /* Unsupported raster policies fail without reaching GDI. */
    push(r, s, hdc);
    push(r, s, 0);
    push(r, s, 0);
    push(r, s, 32);
    push(r, s, 32);
    push(r, s, 0xF0);
    push(r, s, 0x21);
    __win16_call_service(r, s, SERVICE_PatBlt);
    if (s->ax || s->sp != 0xF000 || context.calls || r->error)
    {
        result = 121;
        goto cleanup;
    }
cleanup:
    destroy_handles(&context.dcs);
    r->gdi = 0;
    if (dc && old && old != (void *)-1)
    {
        void *selected = native_SelectObject(dc, old);
        if ((!selected || selected == (void *)-1) && !result)
        {
            result = 118;
        }
    }
    if (dc && !native_DeleteDC(dc) && !result)
    {
        result = 119;
    }
    if (bitmap && !native_DeleteObject(bitmap) && !result)
    {
        result = 120;
    }
    destroy_runtime(r);
    return result;
}
