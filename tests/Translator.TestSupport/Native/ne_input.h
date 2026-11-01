/* Generic input routing, DC leases and descriptor-authored state assertions.
 * No game coordinates, directions or movement rules are computed here. */
extern __attribute__((visibility("hidden"))) ABI void *native_GetDC(void *);
extern __attribute__((visibility("hidden"))) ABI int native_ReleaseDC(void *, void *);
extern __attribute__((visibility("hidden"))) ABI void *native_SetFocus(void *);
extern __attribute__((visibility("hidden"))) ABI void *native_GetFocus(void);
typedef struct
{
    void *dc, *hwnd;
    u16 token;
} InputLease;
typedef struct
{
    u32 target, keyCount, caseCount, delivered, downs, ups, nextToken;
    u16 window, focus;
    const u8 *keys, *initial, *cases;
    u8 *snapshot;
    InputLease leases[8];
} NativeInput;

static NativeInput *input_owner(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    return g
        ? g->input
        : 0;
}

static const u8 *input_writes(WindowHost *h, const u8 *p)
{
    u32 count = get32(p);
    p += 4;
    for (u32 i = 0; i < count; i++)
    {
        u32 offset = get16(p), length = get16(p + 2);
        p += 4;
        // Test/setup writes may never overlap private callback stacks or executable storage.
        if (offset >= 0x5000 || length > 0x5000 - offset)
        {
            h->app->runtime.error = 14;
            return p;
        }
        u8 *dest = window_memory(&h->app->runtime, ((u32)h->automaticDs << 16) | offset, length, 1);
        if (!dest)
        {
            return p;
        }
        for (u32 j = 0; j < length; j++)
        {
            dest[j] = p[j];
        }
        p += length;
    }
    return p;
}

static u32 input_open(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    NativeInput *input = heap_alloc(&h->app->storage, sizeof(*input));
    if (!input)
    {
        return 20;
    }
    zero(input, sizeof(*input));
    g->input = input;
    const u8 *p = h->app->inputDescriptor;
    input->target = get32(p);
    input->window = get16(p + 4);
    input->focus = get16(p + 6);
    p += 8;
    if (input->window >= h->nextId || input->focus >= h->nextId)
    {
        return 10;
    }
    CodeBinding *b = h->app->runtime.code;
    while (b && (((u32)b->segment << 16) | b->offset) != input->target)
    {
        b = b->next;
    }
    if (!b || !b->entry || !b->farCall)
    {
        return 10;
    }
    u32 count = get32(p);
    p += 4;
    for (u32 i = 0; i < count; i++, p += 4)
    {
        u16 node = get16(p + 2);
        if (node >= h->nextId)
        {
            return 10;
        }
        u8 *dest = window_memory(&h->app->runtime, ((u32)h->automaticDs << 16) | get16(p), 2, 1);
        if (!dest)
        {
            return 10;
        }
        put16(dest, h->windows[node].id);
    }
    input->keyCount = get32(p);
    input->keys = p + 4;
    p += 4 + input->keyCount * 2;
    for (u32 i = 0; i < input->keyCount; i++)
    {
        for (u32 j = 0; j < i; j++)
        {
            if (get16(input->keys + i * 2) == get16(input->keys + j * 2))
            {
                return 10;
            }
        }
    }
    input->initial = p;
    p = input_writes(h, p);
    if (h->app->runtime.error)
    {
        return 10;
    }
    input->caseCount = get32(p);
    input->cases = p + 4;
    if (h->app->automation)
    {
        input->snapshot = heap_alloc(&h->app->storage, 0x5000);
        if (!input->snapshot)
        {
            return 20;
        }
    }
    return 0;
}

static int input_callback(WindowHost *h, u32 message)
{
    NativeInput *input = input_owner(h);
    return input && h->boundTarget == input->target &&
        (message == 0x100 || (h->app->hazards && message == 0x111));
}

static void input_focus(WindowHost *h, void *hwnd, u32 message)
{
    NativeInput *input = input_owner(h);
    if (!input || !window_native(h, hwnd))
    {
        return;
    }
    if (message == 0x201 || message == 7)
    {
        void *desired = h->windows[input->focus].native;
        if (native_GetFocus() != desired)
        {
            native_SetFocus(desired);
        }
    }
}

static int input_route(WindowHost *h, void *hwnd, u32 message, uptr wp, sptr lp, sptr *result)
{
    NativeInput *input = input_owner(h);
    if (!input || !window_native(h, hwnd) ||
        (message != 0x100 && message != 0x101 && !(h->app->hazards && message == 0x111)))
    {
        return 0;
    }
    // Route the declared key set unchanged. Original code decides its meaning.
    int command = message == 0x111;
    int allowed = command && hwnd == h->windows[0].native && wp == h->app->restartCommand && !lp;
    if (!command)
    {
        for (u32 i = 0; i < input->keyCount; i++)
        {
            if (wp == get16(input->keys + i * 2))
            {
                allowed = 1;
            }
        }
    }
    if (!allowed)
    {
        return 0;
    }
    // A modal original callback disables its owner. Posted/synchronous input
    // must not enter another gameplay frame or steal the dialog focus.
    if (h->app->hazards && h->active)
    {
        *result = 0;
        return 1;
    }
    if (!command && native_GetFocus() != h->windows[input->focus].native)
    {
        h->result = 60;
        native_PostQuitMessage(60);
        *result = 0;
        return 1;
    }
    u32 previous = h->boundTarget;
    h->boundTarget = input->target;
    *result = window_dispatch(h, h->windows[input->window].native, message, wp, lp);
    h->boundTarget = previous;
    if (!h->result)
    {
        input->delivered++;
        if (message == 0x100)
        {
            input->downs++;
        }
        else if (message == 0x101)
        {
            input->ups++;
        }
    }
    if (h->result)
    {
        native_PostQuitMessage((int)h->result);
    }
    return 1;
}

static void *input_dc(WindowHost *h, u16 token)
{
    NativeInput *input = input_owner(h);
    if (!input)
    {
        return 0;
    }
    for (u32 i = 0; i < 8; i++)
    {
        if (input->leases[i].dc && input->leases[i].token == token)
        {
            return input->leases[i].dc;
        }
    }
    return 0;
}

static u32 input_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    Runtime *r = &h->app->runtime;
    NativeInput *input = input_owner(h);
    if (!input)
    {
        r->error = 14;
        return 0;
    }
    if (offset == WINDOW_GetDC)
    {
        if (h->app->clock)
        {
            clock_probe(h);
        }
        *cleanup = 2;
        WindowIdentity *w = window_id(h, arg(r, s, 0));
        if (!w)
        {
            r->error = 14;
            return 0;
        }
        for (u32 i = 0; i < 8; i++)
        {
            if (!input->leases[i].dc)
            {
                if (input->nextToken >= 0x6fff)
                {
                    r->error = 14;
                    return 0;
                }
                void *dc = native_GetDC(w->native);
                if (!dc)
                {
                    return 0;
                }
                input->leases[i].dc = dc;
                input->leases[i].hwnd = w->native;
                input->leases[i].token = (u16)(0x1000 + input->nextToken++);
                return input->leases[i].token;
            }
        }
        r->error = 14;
        return 0;
    }
    *cleanup = 4;
    WindowIdentity *w = window_id(h, arg(r, s, 1));
    u16 token = arg(r, s, 0);
    for (u32 i = 0; i < 8; i++)
    {
        if (input->leases[i].dc && input->leases[i].token == token)
        {
            InputLease *lease = &input->leases[i];
            if (!w || w->native != lease->hwnd || !native_ReleaseDC(lease->hwnd, lease->dc))
            {
                r->error = 14;
                return 0;
            }
            zero(lease, sizeof(*lease));
            return 1;
        }
    }
    r->error = 14;
    return 0;
}

static u32 input_close(WindowHost *h)
{
    CounterGraphics *g = h->app->graphics;
    NativeInput *input = g
        ? g->input
        : 0;
    if (!input)
    {
        return 0;
    }
    u32 result = 0;
    for (u32 i = 0; i < 8; i++)
    {
        if (input->leases[i].dc && !native_ReleaseDC(input->leases[i].hwnd, input->leases[i].dc))
        {
            result = 50;
        }
    }
    if (input->snapshot)
    {
        heap_release(&h->app->storage, input->snapshot);
    }
    heap_release(&h->app->storage, input);
    g->input = 0;
    return result;
}

static u32 input_verify(WindowHost *h)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    NativeInput *input = input_owner(h);
    const u8 *p = input->cases;
    if (native_GetFocus() != h->windows[input->focus].native)
    {
        return 60;
    }
    // A stale token and a different HWND must never release a live DC lease.
    u16 get[] = {h->windows[input->window].id};
    u16 invalid[] = {0xffff};
    if (window_call(a, WINDOW_GetDC, invalid, 1) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    u16 lease = (u16)window_call(a, WINDOW_GetDC, get, 1);
    if (!lease || r->error)
    {
        return 40;
    }
    u16 wrong[] = {lease, h->windows[(input->window + 1) % h->nextId].id};
    if (window_call(a, WINDOW_ReleaseDC, wrong, 2) || r->error != 14 || !input_dc(h, lease))
    {
        return 40;
    }
    r->error = 0;
    u16 release[] = {lease, get[0]};
    if (window_call(a, WINDOW_ReleaseDC, release, 2) != 1 || r->error || input_dc(h, lease))
    {
        return 40;
    }
    if (window_call(a, WINDOW_ReleaseDC, release, 2) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    for (u32 c = 0; c < input->caseCount; c++)
    {
        if (a->clock)
        {
            a->clockCase = c + 1;
        }
        p = input_writes(h, p);
        if (r->error)
        {
            return 10;
        }
        u32 completions = a->completionDialogs;
        a->completionPlan = 0;
        u32 dialogs = a->deathDialogs;
        a->modalPlan = 0;
        if (a->hazards)
        {
            u32 modal = get32(p);
            p += 4;
            if (modal)
            {
                a->modalPlan = p;
                p = hazard_modal_end(p);
            }
        }
        if (a->completion)
        {
            u32 count = get32(p);
            if (count)
            {
                a->completionPlan = p;
            }
            p += 4;
            for (u32 i = 0; i < count; i++)
            {
                u32 length = get32(p);
                p += 4 + length;
            }
        }
        Segment *data = find_segment(r, h->automaticDs);
        if (!data || data->size < 0x5000 || !input->snapshot)
        {
            return 10;
        }
        for (u32 i = 0; i < 0x5000; i++)
        {
            input->snapshot[i] = data->bytes[i];
        }
        u32 events = get32(p);
        p += 4;
        u32 keyboardEvents = 0;
        u32 delivered = input->delivered, downs = input->downs, ups = input->ups;
        for (u32 i = 0; i < events; i++)
        {
            if (a->clock)
            {
                u16 operation = get16(p);
                p += 2;
                if (operation)
                {
                    u32 result = clock_event(h, p - 2);
                    if (result)
                    {
                        return result;
                    }
                    p += 10;
                    continue;
                }
            }
            u16 window = input->focus;
            if (a->movement)
            {
                window = get16(p);
                p += 2;
            }
            if (window >= h->nextId)
            {
                return 10;
            }
            u16 message = get16(p), key = get16(p + 2);
            if (message != 0x100 && message != 0x101 && !(a->movement && message == 0x113) &&
                !(a->hazards && message == 0x111))
            {
                return 10;
            }
            if (message != 0x113)
            {
                keyboardEvents++;
            }
            if (!native_PostMessageW(h->windows[window].native, message, key,
                                     (sptr)(int)get32(p + 4)))
            {
                return 60;
            }
            NativeMessage msg;
            int status = native_GetMessageW(&msg, h->windows[window].native, message, message);
            if (status <= 0 || msg.message != message || msg.wparam != key ||
                (u32)msg.lparam != get32(p + 4))
            {
                return 40;
            }
            State saved = a->state;
            native_TranslateMessage(&msg);
            native_DispatchMessageW(&msg);
            if (h->result || r->error)
            {
                if (!a->oracleOffset)
                {
                    a->oracleOffset = 0x6400 + i;
                    a->oracleActual = message;
                }
                return h->result
                    ? h->result
                    : 30;
            }
            if (!callback_state_equal(&saved, &a->state) || h->active || r->callbackDepth)
            {
                return 40;
            }
            for (u32 j = 0; j < 8; j++)
            {
                if (input->leases[j].dc)
                {
                    return 50;
                }
            }
            p += 8;
        }
        if (input->delivered - delivered != keyboardEvents ||
            (!a->movement && (input->downs == downs || input->ups == ups)))
        {
            return 40;
        }
        if (a->hazards && a->deathDialogs - dialogs != (a->modalPlan
            ? 1u
            : 0u))
        {
            return 40;
        }
        a->modalPlan = 0;
        if (a->completion && a->completionDialogs - completions != (a->completionPlan
            ? 1u
            : 0u))
        {
            return 40;
        }
        a->completionPlan = 0;
        if (a->tiles)
        {
            u32 result = hint_verify(h);
            if (result)
            {
                return result;
            }
        }
        u32 checks = get32(p);
        p += 4;
        for (u32 i = 0; i < checks; i++)
        {
            u32 offset = get16(p), length = get16(p + 2);
            p += 4;
            u8 *actual = window_memory(r, ((u32)h->automaticDs << 16) | offset, length, 0);
            if (!actual)
            {
                return 10;
            }
            if (offset >= 0x5000 || length > 0x5000 - offset)
            {
                return 10;
            }
            for (u32 j = 0; j < length; j++)
            {
                if (actual[j] != p[j])
                {
                    a->oracleOffset = offset + j;
                    a->oracleActual = actual[j];
                    a->oracleExpected = p[j];
                    return 40;
                }
                input->snapshot[offset + j] = p[j];
            }
            p += length;
        }
        for (u32 i = 0; i < 0x5000; i++)
        {
            if (input->snapshot[i] != data->bytes[i])
            {
                a->oracleOffset = i;
                a->oracleActual = data->bytes[i];
                a->oracleExpected = input->snapshot[i];
                return 40;
            }
        }
        if (a->hazards)
        {
            u32 result = hazard_far_checks(h, &p);
            if (result)
            {
                return result;
            }
        }
        if (a->tiles)
        {
            u32 count = get32(p);
            p += 4;
            for (u32 i = 0; i < count; i++, p += 6)
            {
                u16 node = get16(p), index = get16(p + 2);
                if (node >= h->nextId || index >= 16 ||
                    h->windows[node].words[index] != get16(p + 4))
                {
                    return 40;
                }
            }
        }
        // Repaint all original components after each key sequence. State/pixels must agree.
        SceneGraphics *scene = a->graphics
            ? ((CounterGraphics *)a->graphics)->scene
            : 0;
        if (!scene)
        {
            return 10;
        }
        const u8 *savedPixels = scene->pixels;
        u32 savedCount = scene->pixelCount;
        if (a->movement)
        {
            scene->pixelCount = get32(p);
            scene->pixels = p + 4;
            p += 4 + scene->pixelCount * 46;
        }
        u32 repaint = scene_verify(h);
        scene->pixels = savedPixels;
        scene->pixelCount = savedCount;
        if (repaint)
        {
            return repaint;
        }
    }
    // Automation restores the documented launch fixture only after all four original-code cases.
    input_writes(h, input->initial);
    return r->error
        ? 10
        : 0;
}
