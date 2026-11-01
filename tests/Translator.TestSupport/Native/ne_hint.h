/* Bounded Win16 USER/GDI text services. Layout, font fitting, hint lifetime and
 * content are executed by original ShowHint/HideHint/HINTWNDPROC instructions. */
extern __attribute__((visibility("hidden"))) ABI int native_DrawTextA(void *, const char *, int,
                                                                      NativeRect *, u32);
extern __attribute__((visibility("hidden"))) ABI void *native_CreateFontIndirectA(const void *);
extern __attribute__((visibility("hidden"))) ABI int native_GetDeviceCaps(void *, int);
extern __attribute__((visibility("hidden"))) ABI int native_MulDiv(int, int, int);
extern __attribute__((visibility("hidden"))) ABI u32 native_SetTextColor(void *, u32);
extern __attribute__((visibility("hidden"))) ABI u32 native_SetBkColor(void *, u32);

static u32 hint_create(WindowHost *h, State *s)
{
    Runtime *r = &h->app->runtime;
    u16 name[80];
    const char *expected = "HintClass";
    if (!h->app->tiles || !window_string(r, window_arg32(r, s, 13), name))
    {
        return 0;
    }
    for (u32 i = 0;; i++)
    {
        if (name[i] != (u16)expected[i])
        {
            r->error = 14;
            return 0;
        }
        if (!expected[i])
        {
            break;
        }
    }
    WindowIdentity *parent = window_id(h, arg(r, s, 4));
    if (!parent || parent != &h->windows[2] || window_arg32(r, s, 11) || window_arg32(r, s, 0) ||
        arg(r, s, 2) != h->app->instance || arg(r, s, 3) != 6 ||
        window_arg32(r, s, 9) != 0x54000000 || h->nextId != 7)
    {
        r->error = 14;
        return 0;
    }
    WindowIdentity *w = &h->windows[h->nextId++];
    zero(w, sizeof(*w));
    w->id = 8;
    w->target = h->app->hintTarget;
    w->live = 1;
    h->creating++;
    void *created = native_CreateWindowExW(
        0, h->name, 0, window_arg32(r, s, 9), (short)arg(r, s, 8), (short)arg(r, s, 7),
        (short)arg(r, s, 6), (short)arg(r, s, 5), parent->native, (void *)6, h->app->module, h);
    h->creating--;
    if (!created || w->native != created)
    {
        r->error = 14;
        return 0;
    }
    h->app->hintCreates++;
    return w->id;
}

static u32 hint_select(WindowHost *h, State *s, u16 *cleanup)
{
    *cleanup = 4;
    Runtime *r = &h->app->runtime;
    NativeApplication *a = h->app;
    void *dc = scene_dc(h, arg(r, s, 1)), *object = 0;
    u16 id = arg(r, s, 0);
    if (!dc)
    {
        r->error = 14;
        return 0;
    }
    if (id == 0x600)
    {
        object = a->hintFont;
    }
    else if (id == 0x601)
    {
        object = a->hintSavedFont;
    }
    else if (id == 0x602)
    {
        object = a->hintBrush;
    }
    else if (id == 0x610 || id == 0x612)
    {
        object = native_GetStockObject(id - 0x610);
    }
    if (!object)
    {
        r->error = 14;
        return 0;
    }
    void *old = native_SelectObject(dc, object);
    if (!old || old == (void *)-1)
    {
        r->error = 14;
        return 0;
    }
    if (id == 0x600)
    {
        a->hintSavedFont = old;
        a->hintDc = dc;
        return 0x601;
    }
    if (id == 0x601)
    {
        if (old != a->hintFont)
        {
            r->error = 14;
            return 0;
        }
        a->hintSavedFont = a->hintDc = 0;
        return 0x600;
    }
    if (old == native_GetStockObject(0))
    {
        return 0x610;
    }
    if (old == native_GetStockObject(2))
    {
        return 0x612;
    }
    a->hintBrush = old;
    return 0x602;
}

static u32 hint_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    if (!a->tiles)
    {
        r->error = 5;
        return 0;
    }
    if (offset == 0x324)
    {
        *cleanup = 8;
        u8 *p = window_memory(r, window_arg32(r, s, 2), 8, 1);
        if (!p)
        {
            return 0;
        }
        int dx = (short)arg(r, s, 1), dy = (short)arg(r, s, 0),
            v[4] = {(short)get16(p) - dx, (short)get16(p + 2) - dy, (short)get16(p + 4) + dx,
                    (short)get16(p + 6) + dy};
        for (u32 i = 0; i < 4; i++)
        {
            if (!word_signed(v[i]))
            {
                r->error = 14;
                return 0;
            }
            put16(p + i * 2, (u16)v[i]);
        }
        return 1;
    }
    if (offset == 0x32c)
    {
        *cleanup = 0;
        if (a->completion)
        {
            return completion_format(h, s);
        }
        u8 *format = window_memory(r, window_arg32(r, s, 2), 1, 0);
        if (!format)
        {
            return 0;
        }
        u32 at = 0, source = window_arg32(r, s, 4), destination = window_arg32(r, s, 0),
            fp = window_arg32(r, s, 2), substitutions = 0;
        for (u32 i = 0; i < 128; i++)
        {
            u8 *c = window_memory(r, fp + i, 1, 0);
            if (!c)
            {
                return 0;
            }
            if (!*c)
            {
                break;
            }
            if (*c == '%')
            {
                c = window_memory(r, fp + (++i), 1, 0);
                if (!c || *c != 's' || substitutions >= (a->hazards
                    ? 3u
                    : 1u))
                {
                    r->error = 14;
                    return 0;
                }
                source = window_arg32(r, s, 4 + substitutions * 2);
                substitutions++;
                for (u32 j = 0; j < 128; j++)
                {
                    c = window_memory(r, source + j, 1, 0);
                    if (!c)
                    {
                        return 0;
                    }
                    if (!*c)
                    {
                        break;
                    }
                    if (at >= 127)
                    {
                        r->error = 14;
                        return 0;
                    }
                    u8 *d = window_memory(r, destination + at++, 1, 1);
                    if (!d)
                    {
                        return 0;
                    }
                    *d = *c;
                }
            }
            else
            {
                if (at >= 127)
                {
                    r->error = 14;
                    return 0;
                }
                u8 *d = window_memory(r, destination + at++, 1, 1);
                if (!d)
                {
                    return 0;
                }
                *d = *c;
            }
        }
        if (!substitutions)
        {
            r->error = 14;
            return 0;
        }
        u8 *end = window_memory(r, destination + at, 1, 1);
        if (end)
        {
            *end = 0;
        }
        return at;
    }
    if (offset == 0x33c)
    {
        *cleanup = 6;
        int result = native_MulDiv((short)arg(r, s, 2), (short)arg(r, s, 1), (short)arg(r, s, 0));
        return (u16)(word_signed(result)
            ? result
            : -1);
    }
    if (offset == 0x340)
    {
        *cleanup = 4;
        u8 *p = window_memory(r, window_arg32(r, s, 0), 50, 0);
        if (!p || a->hintFont)
        {
            r->error = 14;
            return 0;
        }
        u8 font[60];
        zero(font, sizeof(font));
        for (u32 i = 0; i < 5; i++)
        {
            put32(font + i * 4, (u32)(int)(short)get16(p + i * 2));
        }
        for (u32 i = 0; i < 40; i++)
        {
            font[20 + i] = p[10 + i];
        }
        a->hintFont = native_CreateFontIndirectA(font);
        return a->hintFont
            ? 0x600
            : 0;
    }
    if (offset == 0x328)
    {
        *cleanup = 14;
        void *dc = scene_dc(h, arg(r, s, 6));
        u32 length = arg(r, s, 3), flags = arg(r, s, 0);
        u8 *text = window_memory(r, window_arg32(r, s, 4), length, 0),
           *p = window_memory(r, window_arg32(r, s, 1), 8, 1);
        if (!dc || !text || !p || !length || length > 127 ||
            (flags != 0xc11 && flags != 0x811 &&
             !(a->hazards && (flags == 0xd21 || flags == 0x821))))
        {
            r->error = 14;
            return 0;
        }
        if (a->automation && h->windows[7].native == h->current.hwnd &&
            !paint_hash(a->graphics, text, length, a->hintTextHash))
        {
            r->error = 14;
            return 0;
        }
        NativeRect rect = {(short)get16(p), (short)get16(p + 2), (short)get16(p + 4),
                           (short)get16(p + 6)};
        int result = native_DrawTextA(dc, (const char *)text, (int)length, &rect, flags);
        int v[] = {rect.left, rect.top, rect.right, rect.bottom};
        for (u32 i = 0; i < 4; i++)
        {
            if (!word_signed(v[i]))
            {
                r->error = 14;
                return 0;
            }
            put16(p + i * 2, (u16)v[i]);
        }
        if (flags == 0x811)
        {
            a->hintPaints++;
        }
        if (flags == 0x821)
        {
            a->placardDraws++;
        }
        return (u16)result;
    }
    if (offset == 0x338)
    {
        *cleanup = 4;
        void *dc = scene_dc(h, arg(r, s, 1));
        if (!dc || arg(r, s, 0) != 90)
        {
            r->error = 14;
            return 0;
        }
        return native_GetDeviceCaps(dc, 90);
    }
    *cleanup = 6;
    void *dc = scene_dc(h, arg(r, s, 2));
    if (!dc)
    {
        r->error = 14;
        return 0;
    }
    return offset == 0x330
        ? native_SetTextColor(dc, window_arg32(r, s, 0))
        : native_SetBkColor(dc, window_arg32(r, s, 0));
}

static u32 hint_close(WindowHost *h)
{
    NativeApplication *a = h->app;
    u32 result = 0;
    if (a->hintSavedFont && a->hintDc)
    {
        if (!native_SelectObject(a->hintDc, a->hintSavedFont))
        {
            result = 50;
        }
        a->hintSavedFont = a->hintDc = 0;
    }
    if (a->hintFont)
    {
        if (!native_DeleteObject(a->hintFont))
        {
            result = 50;
        }
        a->hintFont = 0;
    }
    return result;
}
/* Independent UI oracle, automation only: fixed 12-point Helv italic/bold text,
 * three inset border rings and explicit descriptor-supplied expected text.
 * It does not inspect or copy the original procedure's output/layout decisions. */

static u32 hint_verify(WindowHost *h)
{
    NativeApplication *a = h->app;
    if (!a->tiles || h->nextId != 8 || !h->windows[7].live)
    {
        return 0;
    }
    CounterGraphics *g = a->graphics;
    WindowIdentity *w = &h->windows[7];
    if (!native_UpdateWindow(w->native) || h->result || a->runtime.error)
    {
        return 30;
    }
    void *actualDc = 0, *expectedDc = 0, *actualBitmap = 0, *expectedBitmap = 0, *actualOld = 0,
         *expectedOld = 0, *actualPixels = 0, *expectedPixels = 0, *font = 0, *oldFont = 0;
    u32 result = 20;
    u8 bmi[40];
    zero(bmi, 40);
    put32(bmi, 40);
    put32(bmi + 4, 128);
    put32(bmi + 8, (u32)-146);
    put16(bmi + 12, 1);
    put16(bmi + 14, 32);
    actualDc = native_CreateCompatibleDC(0);
    expectedDc = native_CreateCompatibleDC(0);
    if (!actualDc || !expectedDc)
    {
        goto done;
    }
    actualBitmap = native_CreateDIBSection(actualDc, bmi, 0, &actualPixels, 0, 0);
    expectedBitmap = native_CreateDIBSection(expectedDc, bmi, 0, &expectedPixels, 0, 0);
    if (!actualBitmap || !expectedBitmap)
    {
        goto done;
    }
    actualOld = native_SelectObject(actualDc, actualBitmap);
    expectedOld = native_SelectObject(expectedDc, expectedBitmap);
    if (!actualOld || !expectedOld)
    {
        goto done;
    }
    zero(actualPixels, 128 * 146 * 4);
    zero(expectedPixels, 128 * 146 * 4);
    for (int y = 0; y < 146; y++)
    {
        for (int x = 0; x < 128; x++)
        {
            u32 color = 0;
            int ring = x < y
                ? x
                : y;
            if (127 - x < ring)
            {
                ring = 127 - x;
            }
            if (145 - y < ring)
            {
                ring = 145 - y;
            }
            if (ring < 3)
            {
                color = (x == 127 - ring || y == 145 - ring)
                    ? 0xffffff
                    : 0x808080;
            }
            ((u32 *)expectedPixels)[y * 128 + x] = color;
        }
    }
    // A separate reference font/layout, rather than borrowing selected objects
    // or computed rectangles from translated HINTWNDPROC.
    u8 lf[60];
    zero(lf, 60);
    put32(lf, (u32)native_MulDiv(-12, native_GetDeviceCaps(expectedDc, 90), 72));
    put32(lf + 16, 700);
    lf[20] = 1;
    const char *face = "Helv";
    for (u32 i = 0; i < 5; i++)
    {
        lf[28 + i] = face[i];
    }
    font = native_CreateFontIndirectA(lf);
    if (!font)
    {
        goto done;
    }
    oldFont = native_SelectObject(expectedDc, font);
    if (!oldFont)
    {
        goto done;
    }
    native_SetTextColor(expectedDc, 0x00ffff00);
    native_SetBkColor(expectedDc, 0);
    NativeRect rect = {4, 4, 124, 142};
    u32 length = 0;
    while (length < 128 && a->hintText[length])
    {
        length++;
    }
    if (length == 128)
    {
        goto done;
    }
    if (!native_DrawTextA(expectedDc, (const char *)a->hintText, length, &rect, 0x811))
    {
        goto done;
    }
    g->memoryDc = actualDc;
    g->width = 128;
    g->height = 146;
    g->offscreen = 1;
    u32 previous = h->boundTarget;
    h->boundTarget = w->target;
    window_dispatch(h, w->native, 0xf, 0, 0);
    h->boundTarget = previous;
    g->offscreen = 0;
    g->memoryDc = 0;
    if (h->result || a->runtime.error)
    {
        result = 30;
        goto done;
    }
    result = 40;
    if (!native_GdiFlush())
    {
        goto done;
    }
    for (u32 i = 0; i < 128 * 146; i++)
    {
        if ((((u32 *)actualPixels)[i] & 0xffffff) != (((u32 *)expectedPixels)[i] & 0xffffff))
        {
            goto done;
        }
    }
    result = 0;
done:
    if (hint_close(h))
    {
        result = 50;
    }
    if (oldFont && expectedDc)
    {
        native_SelectObject(expectedDc, oldFont);
    }
    if (font && !native_DeleteObject(font))
    {
        result = 50;
    }
    if (actualOld && actualDc)
    {
        native_SelectObject(actualDc, actualOld);
    }
    if (expectedOld && expectedDc)
    {
        native_SelectObject(expectedDc, expectedOld);
    }
    if (actualBitmap && !native_DeleteObject(actualBitmap))
    {
        result = 50;
    }
    if (expectedBitmap && !native_DeleteObject(expectedBitmap))
    {
        result = 50;
    }
    if (actualDc && !native_DeleteDC(actualDc))
    {
        result = 50;
    }
    if (expectedDc && !native_DeleteDC(expectedDc))
    {
        result = 50;
    }
    return result;
}
