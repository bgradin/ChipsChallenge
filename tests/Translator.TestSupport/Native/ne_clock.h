/* Test-only deterministic message/time driver. No game-state offsets or rules.
 * Normal mode continues using native USER timers and the same callback gate. */

static u32 clock_dispatch(WindowHost *h, void *window, u32 id, int post)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    if (post && !native_PostMessageW(window, 0x113, id, 0))
    {
        return 60;
    }
    NativeMessage message;
    if (native_GetMessageW(&message, window, 0x113, 0x113) <= 0 || message.message != 0x113 ||
        message.wparam != id || message.lparam)
    {
        return 40;
    }
    State saved = a->state;
    native_DispatchMessageW(&message);
    if (h->result || r->error)
    {
        return h->result
            ? h->result
            : 30;
    }
    if (!callback_state_equal(&saved, &a->state) || h->active || r->callbackDepth)
    {
        return 40;
    }
    NativeInput *input = input_owner(h);
    for (u32 i = 0; i < 8; i++)
    {
        if (input->leases[i].dc)
        {
            return 50;
        }
    }
    return 0;
}

static u32 clock_deliver(WindowHost *h, LevelTimer *timer)
{
    if (!timer->active)
    {
        return 0;
    }
    u32 result = clock_dispatch(h, timer->window, timer->id, 1);
    if (result)
    {
        return result;
    }
    u32 budget = 16;
    while (h->app->clockQueued)
    {
        if (!budget--)
        {
            return 40;
        }
        h->app->clockQueued--;
        result = clock_dispatch(h, timer->window, timer->id, 0);
        if (result)
        {
            return result;
        }
    }
    return 0;
}

static void clock_probe(WindowHost *h)
{
    NativeApplication *a = h->app;
    LevelFile *f = a->level;
    if (!f || !f->probe)
    {
        return;
    }
    u32 count = f->probe;
    f->probe = 0;
    LevelTimer *timer = &f->timers[0];
    State saved = a->state;
    u32 active = h->active, depth = a->runtime.callbackDepth, deferred = a->clockDeferred;
    for (u32 i = 0; i < count; i++)
    {
        if (native_SendMessageW(timer->window, 0x113, timer->id, 0) ||
            !callback_state_equal(&saved, &a->state) || h->active != active ||
            a->runtime.callbackDepth != depth)
        {
            h->result = 40;
        }
    }
    if (!active || a->clockDeferred - deferred != count || !timer->pending)
    {
        h->result = 40;
    }
}

static u32 clock_contracts(WindowHost *h)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    LevelFile *f = a->level;
    u16 window = h->windows[1].id;
    // Direct adapter failure probes are explicit test actions, never successful
    // replacements for an unsupported original call.
    u16 missing[] = {0, 0, 110, 1, 0xffff};
    if (window_call(a, 0x314, missing, 5) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    u16 callback[] = {1, 1, 110, 1, window};
    if (window_call(a, 0x314, callback, 5) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    u16 wrongPeriod[] = {0, 0, 1, 1, window};
    if (window_call(a, 0x314, wrongPeriod, 5) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    u16 badKill[] = {1, 0xffff};
    if (window_call(a, 0x320, badKill, 2) || r->error != 14)
    {
        return 40;
    }
    r->error = 0;
    // Exercise the actual Windows registration/replacement/removal path too.
    u16 second[] = {0, 0, 220, 2, window}, kill[] = {2, window};
    f->virtualClock = 0;
    if (window_call(a, 0x314, second, 5) != 2 || r->error)
    {
        return 40;
    }
    if (window_call(a, 0x314, second, 5) != 2 || r->error)
    {
        return 40;
    }
    if (window_call(a, 0x320, kill, 2) != 1 || r->error)
    {
        return 40;
    }
    if (window_call(a, 0x320, kill, 2) || r->error)
    {
        return 40;
    }
    f->virtualClock = 1;
    // KillTimer does not remove already queued WM_TIMER messages. The host must
    // discard a stale notification instead of entering the retired timer owner.
    u32 delivered = a->clockDelivered;
    u32 result = clock_dispatch(h, f->timers[1].window, 2, 1);
    if (result || a->clockDelivered != delivered)
    {
        return 40;
    }
    return f->timers[0].active && f->timers[0].interval == 110
        ? 0
        : 40;
}

static u32 clock_event(WindowHost *h, const u8 *p)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    LevelFile *f = a->level;
    if (!a->clock || !a->automation || !f || !f->virtualClock || h->active || r->callbackDepth)
    {
        return 10;
    }
    u16 operation = get16(p), window = get16(p + 2), message = get16(p + 4), key = get16(p + 6);
    u32 value = get32(p + 8);
    if (operation == 3)
    {
        if (message > 1)
        {
            return 10;
        }
        CodeBinding *b = r->code;
        while (b && (((u32)b->segment << 16) | b->offset) != value)
        {
            b = b->next;
        }
        if (!b || !b->farCall)
        {
            return 10;
        }
        State saved = a->state;
        a->state.ds = a->state.ss = h->automaticDs;
        a->state.sp = 0x7fe0;
        if (message)
        {
            push(r, &a->state, key);
        }
        __win16_call_target(r, &a->state, value, 1);
        u32 result = 0;
        if (r->error)
        {
            result = 30;
        }
        else if (a->state.sp != 0x7fe0 - message * 2 || (window != 0xffff && a->state.ax != window))
        {
            result = 40;
        }
        a->state = saved;
        return result;
    }
    if (operation == 5)
    {
        State saved = a->state;
        a->state.ss = h->automaticDs;
        a->state.sp = 0x7fe0;
        u32 result = clock_contracts(h);
        a->state = saved;
        return result;
    }
    if (window >= h->nextId || message != 0x113)
    {
        return 10;
    }
    LevelTimer *timer = level_timer(f, h->windows[window].native, key);
    if (!timer || !timer->interval)
    {
        return 10;
    }
    if (operation == 1)
    {
        if (value > 10000 || value > (0xffffffff - a->clockElapsed) / timer->interval)
        {
            return 10;
        }
        a->clockElapsed += value * timer->interval;
        for (u32 i = 0; i < value; i++)
        {
            u32 result = clock_deliver(h, timer);
            if (result)
            {
                return result;
            }
        }
        return 0;
    }
    if (operation == 2)
    {
        if (value > 600000 || value > 0xffffffff - a->clockElapsed)
        {
            return 10;
        }
        a->clockElapsed += value;
        if (!timer->active)
        {
            return 0;
        }
        timer->remainder += value;
        while (timer->remainder >= timer->interval)
        {
            timer->remainder -= timer->interval;
            u32 result = clock_deliver(h, timer);
            if (result)
            {
                return result;
            }
        }
        return 0;
    }
    if (operation == 4)
    {
        if (!timer->active || key != 1 || !value || value > 8)
        {
            return 10;
        }
        u32 deferred = a->clockDeferred, posted = a->clockPosted, delivered = a->clockDelivered;
        f->probe = value;
        a->clockElapsed += timer->interval;
        u32 result = clock_deliver(h, timer);
        if (result)
        {
            return result;
        }
        return !f->probe && a->clockDeferred - deferred == value && a->clockPosted - posted == 1 &&
                a->clockDelivered - delivered == 2 && !a->clockQueued
            ? 0
            : 40;
    }
    return 10;
}
