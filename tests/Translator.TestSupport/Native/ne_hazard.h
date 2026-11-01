/* OS compatibility and descriptor-only test oracles. Original translated code
 * owns monster behavior, death reasons, list destruction and level restart. */
extern __attribute__((visibility("hidden"))) ABI void *native_LoadCursorW(void *, const u16 *);
extern __attribute__((visibility("hidden"))) ABI void *native_SetCursor(void *);
extern __attribute__((visibility("hidden"))) ABI void *native_SetCapture(void *);
extern __attribute__((visibility("hidden"))) ABI int native_ReleaseCapture(void);
extern __attribute__((visibility("hidden"))) ABI int native_SetWindowTextW(void *, const u16 *);
extern __attribute__((visibility("hidden"))) ABI u32 native_EnableMenuItem(void *, u32, u32);
extern __attribute__((visibility("hidden"))) ABI int native_SetBkMode(void *, int);
extern __attribute__((visibility("hidden"))) ABI int native_GetTextMetricsA(void *, void *);
extern __attribute__((visibility("hidden"))) ABI void *native_CreateMenu(void);
extern __attribute__((visibility("hidden"))) ABI int native_AppendMenuW(void *, u32, uptr,
                                                                        const u16 *);
extern __attribute__((visibility("hidden"))) ABI int native_SetMenu(void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_DestroyMenu(void *);

static u32 hazard_open(WindowHost *h)
{
    NativeApplication *a = h->app;
    if (!a->hazards)
    {
        return 0;
    }
    u16 label[] = {'R', 'e', 's', 't', 'a', 'r', 't', 0};
    a->restartMenu = native_CreateMenu();
    if (!a->restartMenu || !native_AppendMenuW(a->restartMenu, 0, a->restartCommand, label) ||
        !native_SetMenu(h->windows[0].native, a->restartMenu))
    {
        return 60;
    }
    u8 *p = window_memory(&a->runtime, ((u32)h->automaticDs << 16) | a->menuOffset, 2, 1);
    if (!p)
    {
        return 10;
    }
    put16(p, 0x7300);
    return 0;
}

static u32 hazard_detach_menu(WindowHost *h)
{
    NativeApplication *a = h->app;
    if (!a->restartMenu)
    {
        return 0;
    }
    u32 result = 0;
    if (h->windows[0].live && !native_SetMenu(h->windows[0].native, 0))
    {
        result = 50;
    }
    if (!native_DestroyMenu(a->restartMenu))
    {
        result = 50;
    }
    a->restartMenu = 0;
    return result;
}

static const u8 *hazard_modal_end(const u8 *p)
{
    p += 4 + get32(p);
    for (u32 group = 0; group < 2; group++)
    {
        u32 count = get32(p);
        p += 4;
        for (u32 i = 0; i < count; i++)
        {
            u32 length = get16(p + 2);
            p += 4 + length;
        }
    }
    return p + 4 + get32(p) * 46;
}

static u32 hazard_far_checks(WindowHost *h, const u8 **cursor)
{
    const u8 *p = *cursor;
    u32 count = get32(p);
    p += 4;
    for (u32 i = 0; i < count; i++)
    {
        u32 offset = get16(p), length = get16(p + 2);
        p += 4;
        Runtime *r = &h->app->runtime;
        u8 *pointer = window_memory(r, ((u32)h->automaticDs << 16) | offset, 4, 0);
        if (!pointer)
        {
            return 10;
        }
        u8 *actual = window_memory(r, get32(pointer), length, 0);
        if (!actual)
        {
            return 30;
        }
        for (u32 j = 0; j < length; j++)
        {
            if (actual[j] != p[j])
            {
                return 40;
            }
        }
        p += length;
    }
    *cursor = p;
    return 0;
}

static u32 hazard_cursor(NativeApplication *a, void *native)
{
    if (!native)
    {
        return 0;
    }
    for (u32 i = 0; i < a->cursorCount; i++)
    {
        if (a->cursors[i] == native)
        {
            return i + 1;
        }
    }
    if (a->cursorCount == 8)
    {
        a->runtime.error = 14;
        return 0;
    }
    a->cursors[a->cursorCount++] = native;
    return a->cursorCount;
}

static u32 hazard_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    if (!a->hazards)
    {
        r->error = 5;
        return 0;
    }
    if (offset == 0x368)
    {
        *cleanup = 2;
        u16 handle = arg(r, s, 0);
        return free_memory(r, handle)
            ? 0
            : handle;
    }
    if (offset == 0x344)
    {
        *cleanup = 12;
        WindowIdentity *w = window_id(h, arg(r, s, 5));
        u16 text[80], caption[80];
        if (!w || !window_string(r, window_arg32(r, s, 3), text) ||
            !window_string(r, window_arg32(r, s, 1), caption) || arg(r, s, 0))
        {
            r->error = 14;
            return 0;
        }
        if (a->automation)
        {
            const u8 *p = a->modalPlan;
            if (!p)
            {
                r->error = 14;
                return 0;
            }
            u32 length = get32(p);
            p += 4;
            if (length >= 80)
            {
                r->error = 14;
                return 0;
            }
            for (u32 i = 0; i < length; i++)
            {
                if (text[i] != p[i])
                {
                    h->result = 40;
                    return 0;
                }
            }
            if (text[length])
            {
                h->result = 40;
                return 0;
            }
            p += length;
            u32 checks = get32(p);
            p += 4;
            for (u32 i = 0; i < checks; i++)
            {
                u32 at = get16(p), size = get16(p + 2);
                p += 4;
                u8 *actual = window_memory(r, ((u32)h->automaticDs << 16) | at, size, 0);
                if (!actual)
                {
                    return 0;
                }
                for (u32 j = 0; j < size; j++)
                {
                    if (actual[j] != p[j])
                    {
                        h->result = 40;
                        return 0;
                    }
                }
                p += size;
            }
            u32 result = hazard_far_checks(h, &p);
            if (result)
            {
                h->result = result;
                return 0;
            }
            SceneGraphics *scene = ((CounterGraphics *)a->graphics)->scene;
            const u8 *previous = scene->pixels;
            u32 count = scene->pixelCount;
            scene->pixelCount = get32(p);
            scene->pixels = p + 4;
            result = scene_verify(h);
            scene->pixels = previous;
            scene->pixelCount = count;
            if (result)
            {
                h->result = result;
                return 0;
            }
            // Exercise a real synchronous timer notification during the modal
            // callback. It must defer; it cannot reenter the original CPU frame.
            State saved = *s;
            u32 active = h->active, depth = r->callbackDepth;
            if (native_SendMessageW(h->windows[1].native, 0x113, 1, 0) ||
                !callback_state_equal(&saved, s) || h->active != active ||
                r->callbackDepth != depth)
            {
                h->result = 40;
                return 0;
            }
            a->deathDialogs++;
            return 1; // explicitly scripted OK, automation only
        }
        a->deathDialogs++;
        return native_MessageBoxW(w->native, text, caption, 0);
    }
    if (offset == 0x348)
    {
        *cleanup = 6;
        if (arg(r, s, 2) || window_arg32(r, s, 0) != 32514)
        {
            r->error = 14;
            return 0;
        }
        return hazard_cursor(a, native_LoadCursorW(0, (const u16 *)32514));
    }
    if (offset == 0x34c)
    {
        *cleanup = 2;
        u32 token = arg(r, s, 0);
        if (token > a->cursorCount)
        {
            r->error = 14;
            return 0;
        }
        return hazard_cursor(a, native_SetCursor(token
            ? a->cursors[token - 1]
            : 0));
    }
    if (offset == 0x350)
    {
        *cleanup = 2;
        u16 token = arg(r, s, 0);
        WindowIdentity *w = window_id(h, token);
        if (!w)
        {
            a->oracleOffset = token;
            a->oracleActual = h->nextId;
            a->oracleExpected = s->sp;
            r->error = 14;
            return 0;
        }
        WindowIdentity *old = window_native(h, native_SetCapture(w->native));
        return old
            ? old->id
            : 0;
    }
    if (offset == 0x354)
    {
        *cleanup = 0;
        return native_ReleaseCapture() != 0;
    }
    if (offset == 0x358)
    {
        *cleanup = 6;
        WindowIdentity *w = window_id(h, arg(r, s, 2));
        u16 text[80];
        if (!w || !window_string(r, window_arg32(r, s, 0), text))
        {
            r->error = 14;
            return 0;
        }
        return native_SetWindowTextW(w->native, text) != 0;
    }
    if (offset == 0x35c)
    {
        *cleanup = 6;
        if (arg(r, s, 2) != 0x7300 || !a->restartMenu || arg(r, s, 0) > 1)
        {
            r->error = 14;
            return 0;
        }
        return (u16)native_EnableMenuItem(a->restartMenu, arg(r, s, 1), arg(r, s, 0));
    } // absent items return native failure; no fake success
    if (offset == 0x360)
    {
        *cleanup = 4;
        void *dc = scene_dc(h, arg(r, s, 1));
        int mode = arg(r, s, 0);
        if (!dc || (mode != 1 && mode != 2))
        {
            r->error = 14;
            return 0;
        }
        return native_SetBkMode(dc, mode);
    }
    *cleanup = 6;
    void *dc = scene_dc(h, arg(r, s, 2));
    u8 *dest = window_memory(r, window_arg32(r, s, 0), 31, 1);
    u8 metrics[56];
    if (!dc || !dest)
    {
        r->error = 14;
        return 0;
    }
    if (!native_GetTextMetricsA(dc, metrics))
    {
        return 0;
    }
    for (u32 i = 0; i < 11; i++)
    {
        int value = (int)get32(metrics + i * 4);
        if (!word_signed(value))
        {
            r->error = 14;
            return 0;
        }
        put16(dest + i * 2, (u16)value);
    }
    for (u32 i = 0; i < 9; i++)
    {
        dest[22 + i] = metrics[44 + i];
    }
    return 1;
}

static u32 hazard_close(WindowHost *h)
{
    NativeApplication *a = h->app;
    if (!a->hazards || !a->level)
    {
        return 0;
    }
    Runtime *r = &a->runtime;
    State saved = a->state;
    u32 previous = r->error;
    r->error = 0;
    a->state.ds = a->state.ss = h->automaticDs;
    a->state.sp = 0x7fe0;
    __win16_call_target(r, &a->state, a->listFinalizer, 1);
    a->listCleanupError = r->error
        ? r->error
        : a->state.sp != 0x7fe0
                ? 1000 + a->state.sp
                : r->allocated
                    ? 2000 + r->allocated
                    : 0;
    u32 result = a->listCleanupError
        ? 50
        : 0;
    if (hazard_detach_menu(h))
    {
        result = 50;
    }
    a->state = saved;
    if (previous)
    {
        r->error = previous;
    }
    a->listCleanup = result
        ? 0
        : 1;
    return result;
}
