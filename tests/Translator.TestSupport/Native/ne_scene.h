#include "ne_level.h"
/* Original UI composition. All game addresses, window positions and pixel oracles are
 * descriptor metadata. This host owns native objects; it does not draw game tiles. */
extern __attribute__((visibility("hidden"))) ABI int native_BitBlt(void *, int, int, int, int,
                                                                   void *, int, int, u32);
extern __attribute__((visibility("hidden"))) ABI int native_PatBlt(void *, int, int, int, int, u32);
extern __attribute__((visibility("hidden"))) ABI int native_StretchDIBits(void *, int, int, int,
                                                                          int, int, int, int, int,
                                                                          const void *,
                                                                          const void *, u32, u32);
extern __attribute__((visibility("hidden"))) ABI void *native_GetStockObject(int);
extern __attribute__((visibility("hidden"))) ABI int native_AdjustWindowRect(NativeRect *, u32,
                                                                             int);
extern __attribute__((visibility("hidden"))) ABI void *native_GetParent(void *);
typedef struct
{
    void *dc, *bitmap, *previous, *pixels;
} SceneBitmap;
typedef struct
{
    SceneBitmap atlas, info;
    const u8 *resources, *windows, *pixels;
    u32 pixelCount, selected, infoLive, blits;
} SceneGraphics;

static void *scene_dc(WindowHost *h, u16 token)
{
    CounterGraphics *g = h->app->graphics;
    SceneGraphics *scene = g
        ? g->scene
        : 0;
    if (!scene)
    {
        return 0;
    }
    if (token == 0x300)
    {
        return scene->atlas.dc;
    }
#ifdef NE_INPUT
    void *leased = input_dc(h, token);
    if (leased)
    {
        return leased;
    }
#endif
    if (token < 0x200 || token >= 0x208)
    {
        return 0;
    }
    PaintFrame *f = &g->frames[token - 0x200];
    return f->active
        ? (f->offscreen
                ? g->memoryDc
                : f->native.dc)
        : 0;
}

static u32 scene_bitmap(WindowHost *h, const u8 *resource, SceneBitmap *b)
{
    CounterGraphics *g = h->app->graphics;
    u32 offset = get32(resource), length = get32(resource + 4), width = get16(resource + 40),
        height = get16(resource + 42);
    u32 size = get32(g->plan);
    if (!width || width > 1024 || !height || height > 1024 || offset > size ||
        length > size - offset || length < 40 ||
        !paint_hash(g, (u8 *)g->source + offset, length, resource + 8))
    {
        return 82;
    }
    const u8 *source = (u8 *)g->source + offset;
    u32 bitCount = get16(source + 14);
    if (bitCount < 1 || bitCount > 8)
    {
        return 82;
    }
    u32 colors = get32(source + 32);
    if (!colors)
    {
        colors = 1u << bitCount;
    }
    if (get32(source) != 40 || get32(source + 4) != width || get32(source + 8) != height ||
        get16(source + 12) != 1 || get16(source + 14) > 8 || colors > 256 ||
        40 + colors * 4 > length)
    {
        return 82;
    }
    b->dc = native_CreateCompatibleDC(0);
    if (!b->dc)
    {
        return 20;
    }
    u8 info[40];
    zero(info, 40);
    put32(info, 40);
    put32(info + 4, width);
    put32(info + 8, (u32) - (int)height);
    put16(info + 12, 1);
    put16(info + 14, 32);
    b->bitmap = native_CreateDIBSection(b->dc, info, 0, &b->pixels, 0, 0);
    if (!b->bitmap || !b->pixels)
    {
        return 20;
    }
    b->previous = native_SelectObject(b->dc, b->bitmap);
    if (!b->previous || b->previous == (void *)-1)
    {
        b->previous = 0;
        return 20;
    }
    int copied = native_StretchDIBits(b->dc, 0, 0, width, height, 0, 0, width, height,
                                      source + 40 + colors * 4, source, 0, 0x00cc0020);
    return copied == 0 || copied == -1
        ? 30
        : 0;
}

static u32 scene_release(SceneBitmap *b)
{
    u32 result = 0;
    if (b->previous && b->dc)
    {
        void *old = native_SelectObject(b->dc, b->previous);
        if (!old || old == (void *)-1)
        {
            result = 50;
        }
        b->previous = 0;
    }
    if (b->bitmap && !native_DeleteObject(b->bitmap))
    {
        result = 50;
    }
    if (b->dc && !native_DeleteDC(b->dc))
    {
        result = 50;
    }
    zero(b, sizeof(*b));
    return result;
}

static u32 scene_open(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    SceneGraphics *scene = heap_alloc(&h->app->storage, sizeof(*scene));
    if (!scene)
    {
        return 20;
    }
    zero(scene, sizeof(*scene));
    g->scene = scene;
    const u8 *p = h->app->sceneDescriptor;
    scene->resources = p + 4;
    p += 4 + 2 * 60;
    u32 count = get32(p);
    p += 4;
    for (u32 i = 0; i < count; i++)
    {
        u32 offset = get16(p), length = get16(p + 2);
        p += 4;
        u8 *dest = window_memory(&h->app->runtime, ((u32)h->automaticDs << 16) | offset, length, 1);
        if (!dest)
        {
            return 10;
        }
        for (u32 j = 0; j < length; j++)
        {
            dest[j] = p[j];
        }
        p += length;
    }
    p += 4;
    scene->windows = p;
    p += 7 * 24;
    scene->pixelCount = get32(p);
    scene->pixels = p + 4;
    u32 result = scene_bitmap(h, scene->resources, &scene->atlas);
    if (result)
    {
        return result;
    }
    scene->selected = 0x400;
    return 0;
}

static u32 scene_close(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    SceneGraphics *scene = g
        ? g->scene
        : 0;
    if (!scene)
    {
        return 0;
    }
    u32 result = 0;
    if (scene->selected == 0x401 && scene->atlas.dc && scene->atlas.bitmap)
    {
        void *old = native_SelectObject(scene->atlas.dc, scene->atlas.bitmap);
        if (!old || old == (void *)-1)
        {
            result = 50;
        }
        scene->selected = 0x400;
    }
    if (scene_release(&scene->info))
    {
        result = 50;
    }
    if (scene_release(&scene->atlas))
    {
        result = 50;
    }
    if (level_close(h))
    {
        result = 50;
    }
    heap_release(&h->app->storage, scene);
    g->scene = 0;
    return result;
}

static u32 scene_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    Runtime *r = &h->app->runtime;
    CounterGraphics *g = h->app->graphics;
    SceneGraphics *scene = g
        ? g->scene
        : 0;
    if (!scene)
    {
        r->error = 14;
        return 0;
    }
    if (offset == WINDOW_LoadBitmap)
    {
        *cleanup = 6;
        u16 name[80];
        zero(name, sizeof(name));
        u32 pointer = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
        if (arg(r, s, 2) != h->app->instance || !window_string(r, pointer, name) || scene->infoLive)
        {
            r->error = 14;
            return 0;
        }
        const u8 *expected = scene->resources + 60 + 44;
        for (u32 i = 0; i < 16; i++)
        {
            if ((name[i] >= 'a' && name[i] <= 'z'
                ? name[i] - 32
                : name[i]) != expected[i])
            {
                r->error = 14;
                return 0;
            }
        }
        if (scene_bitmap(h, scene->resources + 60, &scene->info))
        {
            r->error = 14;
            return 0;
        }
        void *restored = native_SelectObject(scene->info.dc, scene->info.previous);
        if (!restored || restored == (void *)-1)
        {
            r->error = 14;
            return 0;
        }
        scene->info.previous = 0;
        scene->infoLive = 1;
        return 0x401;
    }
    if (offset == WINDOW_SelectObject)
    {
        *cleanup = 4;
        u16 dc = arg(r, s, 1), object = arg(r, s, 0);
#ifdef NE_INPUT
        if (h->app->tiles && dc != 0x300)
        {
            return hint_select(h, s, cleanup);
        }
#endif
        if (dc != 0x300 || (object != 0x400 && (object != 0x401 || !scene->infoLive)))
        {
            r->error = 14;
            return 0;
        }
        void *selected = native_SelectObject(scene->atlas.dc, object == 0x400
            ? scene->atlas.bitmap
            : scene->info.bitmap);
        if (!selected || selected == (void *)-1)
        {
            r->error = 14;
            return 0;
        }
        u32 old = scene->selected;
        scene->selected = object;
        return old;
    }
    if (offset == WINDOW_DeleteObject)
    {
        *cleanup = 2;
#ifdef NE_INPUT
        if (h->app->tiles && arg(r, s, 0) == 0x600)
        {
            if (!h->app->hintFont || h->app->hintSavedFont ||
                !native_DeleteObject(h->app->hintFont))
            {
                r->error = 14;
                return 0;
            }
            h->app->hintFont = 0;
            return 1;
        }
#endif
        if (arg(r, s, 0) != 0x401 || !scene->infoLive || scene->selected == 0x401)
        {
            r->error = 14;
            return 0;
        }
        if (scene_release(&scene->info))
        {
            r->error = 14;
            return 0;
        }
        scene->infoLive = 0;
        return 1;
    }
    if (offset == WINDOW_GetStockObject)
    {
        *cleanup = 2;
#ifdef NE_INPUT
        if (h->app->tiles && (arg(r, s, 0) == 0 || arg(r, s, 0) == 2))
        {
            return 0x610 + arg(r, s, 0);
        }
#endif
        r->error = 5;
        return 0;
    } // fallback brush path is outside the successful resource fixture
    if (offset == WINDOW_PatBlt)
    {
        *cleanup = 14;
        void *dc = scene_dc(h, arg(r, s, 6));
        u32 rop = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
        if (!dc || (rop != 0x00000042
#ifdef NE_INPUT
                    && !(h->app->tiles && rop == 0x00f00021)
#endif
                        ))
        {
            r->error = 14;
            return 0;
        }
        return native_PatBlt(dc, (short)arg(r, s, 5), (short)arg(r, s, 4), (short)arg(r, s, 3),
                             (short)arg(r, s, 2), rop) != 0;
    }
    *cleanup = 20;
    void *dest = scene_dc(h, arg(r, s, 9)), *source = scene_dc(h, arg(r, s, 4));
    u32 rop = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
    int width = (short)arg(r, s, 6), height = (short)arg(r, s, 5), sx = (short)arg(r, s, 3),
        sy = (short)arg(r, s, 2);
    u32 sw =
            scene->selected == 0x400
                ? get16(scene->resources + 40)
                : get16(scene->resources + 100),
        sh =
            scene->selected == 0x400
                ? get16(scene->resources + 42)
                : get16(scene->resources + 102);
    if (!dest || !source || arg(r, s, 4) != 0x300 || width <= 0 || height <= 0 || sx < 0 ||
        sy < 0 || sx + width > (int)sw || sy + height > (int)sh ||
        (rop != 0x00cc0020 && rop != 0x00ee0086 && rop != 0x008800c6))
    {
        r->error = 14;
        return 0;
    }
    int ok = native_BitBlt(dest, (short)arg(r, s, 8), (short)arg(r, s, 7), width, height, source,
                           sx, sy, rop);
    if (!ok)
    {
        r->error = 14;
    }
    else
    {
        scene->blits++;
    }
    return ok != 0;
}

static u32 scene_verify(WindowHost *h)
{
    NativeApplication *a = h->app;
    CounterGraphics *g = a->graphics;
    SceneGraphics *scene = g->scene;
    for (u32 c = 0; c < scene->pixelCount; c++)
    {
        const u8 *p = scene->pixels + c * 46;
        u32 node = get16(p);
        g->width = get16(p + 2);
        g->height = get16(p + 4);
        if (node >= 7 || !g->width || g->width > 512 || !g->height || g->height > 512)
        {
            return 10;
        }
        WindowIdentity *w = &h->windows[node];
        if (!w->live || !w->target)
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
        if (!g->bitmap)
        {
            return 20;
        }
        g->previous = native_SelectObject(g->memoryDc, g->bitmap);
        if (!g->previous || g->previous == (void *)-1)
        {
            g->previous = 0;
            return 20;
        }
        for (u32 repeat = 0; repeat < 2; repeat++)
        {
            for (u32 i = 0; i < g->width * g->height; i++)
            {
                ((u32 *)g->pixels)[i] = 0x224466;
            }
            if (!native_SelectClipRgn(g->memoryDc, 0) ||
                !native_IntersectClipRect(g->memoryDc, (short)get16(p + 6), (short)get16(p + 8),
                                          (short)get16(p + 10), (short)get16(p + 12)))
            {
                return 30;
            }
            g->offscreen = 1;
            u32 target = h->boundTarget;
            h->boundTarget = w->target;
            window_dispatch(h, w->native, 0xf, 0, 0);
            h->boundTarget = target;
            g->offscreen = 0;
            if (h->result || a->runtime.error)
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
            if (!paint_hash(g, g->pixels, g->width * g->height * 4, p + 14))
            {
                return 40;
            }
        }
        void *old = native_SelectObject(g->memoryDc, g->previous);
        if (!old || old == (void *)-1)
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
    return scene->blits
        ? 0
        : 40;
}

static u32 scene_run(WindowHost *h)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CounterGraphics *g = a->graphics;
    SceneGraphics *scene = g->scene;
    const char *name = "Win16OriginalScene", *title = a->levelDescriptor
        ? "Chip's Challenge - original level"
        : "Chip's Challenge - original scene";
    for (u32 i = 0; name[i]; i++)
    {
        h->name[i] = (u16)name[i];
    }
    for (u32 i = 0; title[i]; i++)
    {
        h->title[i] = (u16)title[i];
    }
    NativeClass c;
    zero(&c, sizeof(c));
    c.style = 3;
    c.procedure = window_procedure;
    c.instance = a->module;
    c.name = h->name;
    c.brush = (void *)6;
    if (!native_RegisterClassW(&c))
    {
        return 60;
    }
    h->extra = 4;
    h->registered = h->bindingLive = h->bindingGeneration = 1;
    h->boundTarget = a->rootPointer;
    for (u32 i = 0; i < 7; i++)
    {
        const u8 *p = scene->windows + i * 24;
        int parent = (short)get16(p);
        u32 target = get32(p + 4), style = get32(p + 16);
        if ((i == 0 && (parent != -1 || target)) ||
            (i && (parent < 0 || (u32)parent >= i || !target)))
        {
            return 10;
        }
        if (target)
        {
            CodeBinding *b = r->code;
            while (b && (((u32)b->segment << 16) | b->offset) != target)
            {
                b = b->next;
            }
            if (!b || !b->entry || !b->farCall)
            {
                return 10;
            }
        }
        WindowIdentity *w = &h->windows[h->nextId++];
        w->id = (u16)h->nextId;
        w->live = 1;
        w->target = target;
        w->words[0] = get16(p + 20);
        w->words[1] = get16(p + 22);
        int width = (short)get16(p + 12), height = (short)get16(p + 14), x = (short)get16(p + 8),
            y = (short)get16(p + 10);
        if (width <= 0 || height <= 0)
        {
            return 10;
        }
        if (i == 0)
        {
            NativeRect rect = {0, 0, width, height};
            if (style & 0x40000 || style & 0x10000 || !native_AdjustWindowRect(&rect, style, 0))
            {
                return 10;
            }
            width = rect.right - rect.left;
            height = rect.bottom - rect.top;
            x = y = 100;
        }
        h->creating = 1;
        void *window =
            native_CreateWindowExW(0, h->name, h->title, style, x, y, width, height,
                                   parent < 0
                                       ? 0
                                       : h->windows[parent].native,
                                   0, a->module, h);
        h->creating = 0;
        if (!window || !w->live || w->native != window)
        {
            return 60;
        }
        NativeRect rect;
        if (!native_GetClientRect(window, &rect) || rect.right != (short)get16(p + 12) ||
            rect.bottom != (short)get16(p + 14) ||
            native_GetParent(window) != (parent < 0
                ? 0
                : h->windows[parent].native))
        {
            return 40;
        }
        if (h->result || r->error)
        {
            return 30;
        }
    }
    u32 levelResult = level_open(h);
    if (levelResult)
    {
        return levelResult;
    }
#ifdef NE_INPUT
    if (a->movement && a->automation)
    {
        u32 stopped = level_virtualize(h);
        if (stopped)
        {
            return stopped;
        }
    }
    u32 completionResult = completion_open(h);
    if (completionResult)
    {
        return completionResult;
    }
    u32 hazardResult = hazard_open(h);
    if (hazardResult)
    {
        return hazardResult;
    }
    u32 inputResult = input_open(h);
    if (inputResult)
    {
        return inputResult;
    }
#endif
    if (a->automation)
    {
        u32 result = scene_verify(h);
        if (result)
        {
            return result;
        }
    }
    native_ShowWindow(h->windows[0].native, a->showState);
    for (u32 repeat = 0; repeat < 3; repeat++)
    {
        for (u32 i = 1; i < 7; i++)
        {
            u32 before = g->paints;
            if (!native_InvalidateRect(h->windows[i].native, 0, 1) ||
                !native_UpdateWindow(h->windows[i].native) || g->paints <= before || r->error ||
                h->result)
            {
                return 40;
            }
        }
    }
#ifdef NE_INPUT
    input_focus(h, h->windows[0].native, 0x201);
    if (a->automation)
    {
        u32 result = input_verify(h);
        if (result)
        {
            return result;
        }
    }
#endif
    if (a->automation && !native_PostMessageW(h->windows[0].native, 0x10, 0, 0))
    {
        return 60;
    }
    NativeMessage msg;
    a->lifecycleState = 3;
    for (;;)
    {
        int status = native_GetMessageW(&msg, 0, 0, 0);
        if (status < 0)
        {
            return 60;
        }
        if (!status)
        {
            break;
        }
        native_TranslateMessage(&msg);
        native_DispatchMessageW(&msg);
        if (h->result)
        {
            return h->result;
        }
    }
    for (u32 i = 0; i < 7; i++)
    {
        if (h->windows[i].live)
        {
            return 40;
        }
    }
    return r->error
        ? 30
        : 0;
}
