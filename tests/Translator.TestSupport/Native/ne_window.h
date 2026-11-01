/* Native-only USER checkpoint. Structure layouts/ordinals cross-checked against Wine's
 * user.exe16.spec and winuser16.h. Original application instructions remain in linked AMD64 bodies.
 */
#include "window_api_bindings.h"
typedef struct
{
    int left, top, right, bottom;
} NativeRect;
typedef ABI sptr (*NativeWndProc)(void *, u32, uptr, sptr);
typedef struct
{
    u32 style;
    NativeWndProc procedure;
    int classExtra, windowExtra;
    void *instance, *icon, *cursor, *brush;
    const u16 *menu, *name;
} NativeClass;
typedef struct
{
    void *parameter, *instance, *menu, *parent;
    int height, width, y, x;
    int style;
    const u16 *name, *className;
    u32 extended;
} NativeCreate;
_Static_assert(sizeof(NativeClass) == 72 && __builtin_offsetof(NativeClass, procedure) == 8,
               "WNDCLASSW layout");
_Static_assert(sizeof(NativeCreate) == 80 && __builtin_offsetof(NativeCreate, name) == 56,
               "CREATESTRUCTW layout");
extern __attribute__((visibility("hidden"))) ABI u16 native_RegisterClassW(const NativeClass *);
extern __attribute__((visibility("hidden"))) ABI int native_UnregisterClassW(const u16 *, void *);
extern __attribute__((visibility("hidden"))) ABI void *
native_CreateWindowExW(u32, const u16 *, const u16 *, u32, int, int, int, int, void *, void *,
                       void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_DestroyWindow(void *);
extern __attribute__((visibility("hidden"))) ABI int native_ShowWindow(void *, int);
extern __attribute__((visibility("hidden"))) ABI int native_UpdateWindow(void *);
extern __attribute__((visibility("hidden"))) ABI sptr native_DefWindowProcW(void *, u32, uptr,
                                                                            sptr);
extern __attribute__((visibility("hidden"))) ABI sptr native_SetWindowLongPtrW(void *, int, sptr);
extern __attribute__((visibility("hidden"))) ABI sptr native_GetWindowLongPtrW(void *, int);
extern __attribute__((visibility("hidden"))) ABI int native_TranslateMessage(const NativeMessage *);
extern __attribute__((visibility("hidden"))) ABI sptr
native_DispatchMessageW(const NativeMessage *);
extern __attribute__((visibility("hidden"))) ABI int native_InvalidateRect(void *,
                                                                           const NativeRect *, int);
extern __attribute__((visibility("hidden"))) ABI int native_GetClientRect(void *, NativeRect *);
extern __attribute__((visibility("hidden"))) ABI sptr native_SendMessageW(void *, u32, uptr, sptr);
extern __attribute__((visibility("hidden"))) ABI int native_PostMessageW(void *, u32, uptr, sptr);

typedef struct
{
    void *native;
    u16 id, words[16];
    int live;
    u32 target;
} WindowIdentity;
typedef struct
{
    NativeApplication *app;
    WindowIdentity windows[16];
    u32 nextId, active, result, translated, mask, destroying, registered, extra;
    u16 name[80], title[80];
    NativeMessage current, pending;
    u16 convertedWparam;
    u32 convertedLparam;
    int pendingValid;
    u32 defaultCalled, defaultResult, creating;
    u32 bindingLive, bindingGeneration, boundTarget, maximumDepth, nestedEntries;
    u16 automaticDs, callbackStacks[8];
#ifdef NE_SCENE
    u32 callbackOffsets[8];
#endif
} WindowHost;
static ABI sptr window_procedure(void *, u32, uptr, sptr);
#ifdef NE_SCENE
static u32 movement_service(WindowHost *, State *, u16, u16 *);
static int level_timer_gate(WindowHost *, void *, u32, uptr);
static void level_flush_deferred(WindowHost *);
static u32 level_service(WindowHost *, State *, u16, u16 *);
static u32 level_stop(WindowHost *);
static u32 scene_open(WindowHost *);
static u32 scene_close(WindowHost *);
static u32 scene_run(WindowHost *);
static u32 scene_service(WindowHost *, State *, u16, u16 *);
#ifdef NE_INPUT
static u32 completion_open(WindowHost *);
static u32 completion_close(WindowHost *);
static u32 completion_service(WindowHost *, State *, u16, u16 *);
static u32 completion_format(WindowHost *, State *);
static u32 hazard_open(WindowHost *);
static u32 hazard_detach_menu(WindowHost *);
static u32 hazard_service(WindowHost *, State *, u16, u16 *);
static u32 hazard_close(WindowHost *);
static const u8 *hazard_modal_end(const u8 *);
static u32 hazard_far_checks(WindowHost *, const u8 **);
static u32 hint_verify(WindowHost *);
static u32 hint_create(WindowHost *, State *);
static u32 hint_select(WindowHost *, State *, u16 *);
static u32 hint_service(WindowHost *, State *, u16, u16 *);
static u32 hint_close(WindowHost *);
static u32 clock_event(WindowHost *, const u8 *);
static void clock_probe(WindowHost *);
static u32 input_open(WindowHost *);
static u32 input_close(WindowHost *);
static u32 input_verify(WindowHost *);
static void *input_dc(WindowHost *, u16);
static u32 input_service(WindowHost *, State *, u16, u16 *);
static int input_route(WindowHost *, void *, u32, uptr, sptr, sptr *);
static int input_callback(WindowHost *, u32);
static void input_focus(WindowHost *, void *, u32);
#endif
#endif
#ifdef NE_PAINT
static u8 *window_memory(Runtime *, u32, u32, int);
static int word_signed(int);
static WindowIdentity *window_id(WindowHost *, u16);
static u16 get16(const u8 *);
static u32 get32(const u8 *);
static void put16(u8 *, u16);
static void put32(u8 *, u32);
static sptr window_dispatch(WindowHost *, void *, u32, uptr, sptr);
#include "ne_graphics.h"
#endif

static WindowHost *window_host(Runtime *r)
{
    NativeApplication *a = r->user;
    return a
        ? a->windowHost
        : 0;
}

static u8 *window_memory(Runtime *r, u32 pointer, u32 bytes, int write)
{
    Segment *s = find_segment(r, (u16)(pointer >> 16));
    u32 offset = (u16)pointer;
    if (write && s && !writable(r, s, (u16)offset, bytes))
    {
        if (r->error != 15)
        {
            r->error = 14;
        }
        return 0;
    }
    if (!s || !(s->permissions & (write
        ? 2
        : 1)) || bytes > s->size || offset > s->size - bytes)
    {
        r->error = 14;
        return 0;
    }
    return s->bytes + offset;
}

static u16 get16(const u8 *p)
{
    return (u16)(p[0] | (u16)p[1] << 8);
}

static u32 get32(const u8 *p)
{
    return get16(p) | (u32)get16(p + 2) << 16;
}

static void put16(u8 *p, u16 v)
{
    p[0] = (u8)v;
    p[1] = (u8)(v >> 8);
}

static void put32(u8 *p, u32 v)
{
    put16(p, (u16)v);
    put16(p + 2, (u16)(v >> 16));
}

static int word_signed(int v)
{
    return v >= -32768 && v <= 32767;
}

static int long_value(sptr v)
{
    return v == (sptr)(int)(u32)v || v == (sptr)(u32)v;
}

static int window_string(Runtime *r, u32 pointer, u16 *out)
{
    for (u32 i = 0; i < 80; i++)
    {
        if ((u16)pointer > 65535 - i)
        {
            break;
        }
        u8 *p = window_memory(r, pointer + i, 1, 0);
        if (!p)
        {
            return 0;
        }
        out[i] = *p;
        if (!*p)
        {
            return i != 0;
        }
    }
    r->error = 14;
    return 0;
}

static WindowIdentity *window_id(WindowHost *h, u16 id)
{
    if (!h || !id || id > h->nextId || id > 16)
    {
        return 0;
    }
    WindowIdentity *w = &h->windows[id - 1];
    return w->live && w->id == id
        ? w
        : 0;
}

static WindowIdentity *window_native(WindowHost *h, void *native)
{
    for (u32 i = 0; h && i < h->nextId; i++)
    {
        if (h->windows[i].live && h->windows[i].native == native)
        {
            return &h->windows[i];
        }
    }
    return 0;
}

static u16 window_register(WindowHost *h, u32 pointer)
{
    Runtime *r = &h->app->runtime;
    u8 *p = window_memory(r, pointer, 26, 0);
    if (!p)
    {
        return 0;
    }
    u32 target = get32(p + 2);
    if (h->registered || target != h->app->rootPointer || get16(p + 6) || get16(p + 8) > 32 ||
        get16(p + 8) % 2 || get16(p + 10) != h->app->instance || get16(p + 12) || get16(p + 14) ||
        get16(p + 16) > 32 || get32(p + 18))
    {
        r->error = 14;
        return 0;
    }
    if (!window_string(r, get32(p + 22), h->name))
    {
        return 0;
    }
    NativeClass c;
    zero(&c, sizeof(c));
    c.style = get16(p);
    c.procedure = window_procedure;
    c.windowExtra = get16(p + 8);
    c.instance = h->app->module;
    c.name = h->name;
    c.brush = (void *)(uptr)get16(
        p + 16); /* documented system-color brush token; other brush identities are deferred */
    u16 atom = native_RegisterClassW(&c);
    if (atom)
    {
        h->registered = 1;
        h->extra = c.windowExtra;
        h->bindingLive = 1;
        h->bindingGeneration = 1;
        h->boundTarget = target;
    }
    return atom;
}

/* Message-specific conversion. Pointer-bearing messages outside this set never enter translated code. */

static int window_convert(WindowHost *h, void *hwnd, u32 message, uptr wparam, sptr lparam, u16 *wp,
                          u32 *lp)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    if (!window_native(h, hwnd) || message > 65535)
    {
        return 0;
    }
    *wp = 0;
    *lp = 0;
    switch (message)
    {
    case 0:
    case 2:
    case 0x10:
    case 0x110:
#ifdef NE_PAINT
    case 0xf:
#endif
        return !wparam && !lparam;
    case 1:
    {
        if (!h->creating || wparam || !lparam)
        {
            return 0;
        }
        const NativeCreate *c = (const NativeCreate *)lparam;
        if (c->parameter != h || c->instance != a->module ||
            (c->menu
#ifdef NE_INPUT
             && !(a->tiles && c->parent && (uptr)c->menu == 6)
#endif
                 ) ||
#ifndef NE_SCENE
            c->parent ||
#else
            (c->parent && !window_native(h, c->parent)) ||
#endif
            !word_signed(c->height) || !word_signed(c->width) || !word_signed(c->x) ||
            !word_signed(c->y))
        {
            return 0;
        }
        u8 *p = window_memory(r, ((u32)a->taskSelector << 16) | (256 + h->active * 64), 34, 1);
        if (!p)
        {
            return 0;
        }
        zero(p, 34);
#ifdef NE_SCENE
        WindowIdentity *parent = window_native(h, c->parent);
        put16(p + 8, parent
            ? parent->id
            : 0);
#endif
        put16(p + 4, a->instance);
        put16(p + 6, (u16)(uptr)c->menu);
        put16(p + 10, (u16)c->height);
        put16(p + 12, (u16)c->width);
        put16(p + 14, (u16)c->y);
        put16(p + 16, (u16)c->x);
        put32(p + 18, (u32)c->style);
        put32(p + 22, ((u32)a->taskSelector << 16) | 192);
        put32(p + 26, ((u32)a->taskSelector << 16) | 128);
        put32(p + 30, c->extended);
        *lp = ((u32)a->taskSelector << 16) | (256 + h->active * 64);
        return 1;
    }
    case 5:
    case 0x100:
    case 0x101:
    case 0x102:
        if (wparam > 65535 || !long_value(lparam))
        {
            return 0;
        }
        *wp = (u16)wparam;
        *lp = (u32)lparam;
        return 1;
    case 0x111:
    {
        if (wparam > 0xffffffffull)
        {
            return 0;
        }
        WindowIdentity *control = lparam
            ? window_native(h, (void *)lparam)
            : 0;
        if (lparam && !control)
        {
            return 0;
        }
        *wp = (u16)wparam;
        *lp = (control
            ? control->id
            : 0) |
            (u32)((wparam >> 16) & 65535) << 16;
        return 1;
    }
    case 0x113:
        if (wparam > 65535 || lparam)
        {
            return 0; /* no TIMERPROC pointer truncation */
        }
        *wp = (u16)wparam;
        return 1;
    default:
        return 0;
    }
}
/* One selector/stack per callback depth; callback-local native marshalling is saved on
 * the bounded native call stack. The application DS and owned image survive every entry. */

static int callback_stack_valid(Segment *stack)
{
    if (!stack)
    {
        return 0;
    }
    for (u32 i = 0; i < 16; i++)
    {
        if (stack->bytes[i] != 0xa5 || stack->bytes[stack->size - 16 + i] != 0x5a)
        {
            return 0;
        }
    }
    return 1;
}

static int callback_frame_valid(WindowHost *h, u32 depth)
{
#ifdef NE_SCENE
    Segment *stack = find_segment(&h->app->runtime, h->automaticDs);
    u32 base = h->callbackOffsets[depth], size = h->app->callbackStackBytes;
    if (!stack || base > stack->size || size > stack->size - base)
    {
        return 0;
    }
    for (u32 i = 0; i < 16; i++)
    {
        if (stack->bytes[base + i] != 0xa5 || stack->bytes[base + size - 16 + i] != 0x5a)
        {
            return 0;
        }
    }
    return 1;
#else
    return callback_stack_valid(find_segment(&h->app->runtime, h->callbackStacks[depth]));
#endif
}
#ifdef NE_SCENE

static int window_stack_push_valid(Runtime *r, State *s)
{
    WindowHost *h = window_host(r);
    if (!h || !h->active)
    {
        return 1;
    }
    u32 base = h->callbackOffsets[h->active - 1], end = base + h->app->callbackStackBytes;
    if (s->ss != h->automaticDs || s->sp < base + 16 || s->sp > end - 18)
    {
        r->error = 14;
        return 0;
    }
    return 1;
}
#endif

static sptr window_dispatch(WindowHost *h, void *hwnd, u32 message, uptr wparam, sptr lparam)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    State *s = &a->state;
    u16 wp;
    u32 lp;
    if (h->result)
    {
        return 0; /* no new translated entry after a callback failure */
    }
    /* Check thread/lifetime before touching CPU state, selector storage or callback depth. */
    if (native_GetCurrentThreadId() != a->ownerThread || !h->registered || !h->bindingLive ||
        h->bindingGeneration != 1 || (a->lifecycleState != 2 && a->lifecycleState != 3) ||
        h->active >= a->callbackLimit || r->callbackDepth != h->active)
    {
        h->result = 60;
        return 0;
    }
    CodeBinding *binding = r->code;
    while (binding && (((u32)binding->segment << 16) | binding->offset) != h->boundTarget)
    {
        binding = binding->next;
    }
    Segment *code = binding
        ? find_segment(r, binding->segment)
        : 0;
    if (!binding || !binding->entry || !binding->farCall || !code || !(code->permissions & 4) ||
        binding->offset >= code->size || !find_segment(r, h->automaticDs))
    {
        h->result = 60;
        return 0;
    }
    u32 depth = h->active;
    Segment *stack = find_segment(r, h->callbackStacks[depth]);
    if (!callback_frame_valid(h, depth))
    {
        h->result = 40;
        return 0;
    }
    u32 frameTop = stack->size;
#ifdef NE_SCENE
    frameTop = h->callbackOffsets[depth] + a->callbackStackBytes;
#endif
    if (!window_convert(h, hwnd, message, wparam, lparam, &wp, &lp))
    {
        h->result = 60;
        return 0;
    }
    WindowIdentity *w = window_native(h, hwnd);
    State saved = *s;
    NativeMessage previousCurrent = h->current, previousPending = h->pending;
    u16 previousWp = h->convertedWparam;
    u32 previousLp = h->convertedLparam;
    u32 previousCalled = h->defaultCalled, previousResult = h->defaultResult;
    int previousValid = h->pendingValid;
    h->current.hwnd = hwnd;
    h->current.message = message;
    h->current.wparam = wparam;
    h->current.lparam = lparam;
    h->convertedWparam = wp;
    h->convertedLparam = lp;
    h->defaultCalled = 0;
    h->active++;
    r->callbackDepth++;
    if (h->active > h->maximumDepth)
    {
        h->maximumDepth = h->active;
    }
    if (depth)
    {
        h->nestedEntries++;
    }
    /* Clear DF and establish a fresh Win16 frame and the module's automatic DS. */
    s->ss = stack->selector;
    s->sp = (u16)(frameTop - 32);
    s->ds = h->automaticDs;
    s->bp = 0;
    s->high[7] = 0;
    s->high[6] = 0;
    s->flags &= ~0x400;
    u16 words[] = {(u16)lp, (u16)(lp >> 16), wp, (u16)message, w->id};
    for (u32 i = 5; i; i--)
    {
        push(r, s, words[i - 1]);
    }
    __win16_call_target(r, s, h->boundTarget, 1);
    u32 value = s->ax | (u32)s->dx << 16;
#ifdef NE_SCENE
    // Win16 DLGPROC returns a 16-bit BOOL, unlike WNDPROC's DX:AX LONG.
    if (a->completion && h->boundTarget == a->completionTarget)
    {
        value = s->ax;
    }
#endif
    if (!h->result && r->error)
    {
        h->result = 30;
    }
    if (!h->result && a->automation)
    {
        if (a->callbackOracle && message == 0x113)
        {
            if (value != a->callbackExpected)
            {
                h->result = 40;
            }
        }
#ifdef NE_PAINT

        else if (message == 0xf)
        {
            if (value)
            {
                h->result = 40;
            }
        }
#endif
#ifdef NE_INPUT

        else if (a->completion && h->boundTarget == a->completionTarget)
        {
            if (value != 1)
            {
                a->oracleOffset = message;
                a->oracleActual = value;
                a->oracleExpected = 1;
                h->result = 40;
            }
        }

        else if (input_callback(h, message) || (a->movement && message == 0x113))
        {
            if (value)
            {
                a->oracleOffset = message;
                a->oracleActual = value;
                a->oracleExpected = 0;
                h->result = 40;
            }
        }
#endif
        else if (!h->defaultCalled || value != h->defaultResult)
        {
#ifdef NE_SCENE
            a->oracleOffset = 0x70000 + message;
            a->oracleActual = value;
            a->oracleExpected = h->defaultResult;
#endif
            h->result = 40;
        }
    }
    if (!h->result && !r->error &&
        (s->sp != frameTop - 32 || s->ss != stack->selector || !callback_frame_valid(h, depth)))
    {
#ifdef NE_SCENE
        a->oracleOffset = 0x71000 + message;
        a->oracleActual = s->sp;
        a->oracleExpected = frameTop - 32;
#endif
        h->result = 40;
    }
    *s = saved;
    h->current = previousCurrent;
    h->pending = previousPending;
    h->pendingValid = previousValid;
    h->convertedWparam = previousWp;
    h->convertedLparam = previousLp;
    h->defaultCalled = previousCalled;
    h->defaultResult = previousResult;
    h->active--;
    r->callbackDepth--;
    h->translated++;
#ifdef NE_SCENE
    level_flush_deferred(h);
#endif
    if (message == 1)
    {
        h->mask |= 1;
    }
    if (message == 5)
    {
        h->mask |= 2;
    }
    if (message == 0x100)
    {
        h->mask |= 4;
    }
    if (message == 0x101)
    {
        h->mask |= 8;
    }
    if (message == 0x102)
    {
        h->mask |= 16;
    }
    if (message == 0x111)
    {
        h->mask |= 32;
    }
    if (message == 0x113)
    {
        h->mask |= 64;
    }
    if (message == 0x10)
    {
        h->mask |= 128;
    }
    if (message == 2)
    {
        h->mask |= 256;
    }
    return h->result
        ? 0
        : (sptr)(int)value;
}

static void dispatch_window_default(Runtime *r, State *s)
{
    WindowHost *h = window_host(r);
    u16 hwnd = arg(r, s, 4), message = arg(r, s, 3), wp = arg(r, s, 2);
    u32 lp = arg(r, s, 0) | (u32)arg(r, s, 1) << 16;
    WindowIdentity *w = window_id(h, hwnd);
    if (!h || !h->active || !w || h->current.hwnd != w->native || message != h->current.message ||
        wp != h->convertedWparam || lp != h->convertedLparam)
    {
        r->error = 14;
        return;
    }
    sptr value;
    /* Native default close synchronously reenters the original WM_DESTROY handler. */
    if (message == 0x10)
    {
        h->destroying++;
    }
    value = native_DefWindowProcW(w->native, message, h->current.wparam, h->current.lparam);
    if (message == 0x10)
    {
        h->destroying--;
    }
    if (!long_value(value))
    {
        r->error = 14;
        return;
    }
    h->defaultCalled++;
    h->defaultResult = (u32)value;
    s->ax = (u16)value;
    s->dx = (u16)((u32)value >> 16);
}

static ABI sptr window_procedure(void *hwnd, u32 message, uptr wp, sptr lp)
{
    WindowHost *h = (WindowHost *)native_GetWindowLongPtrW(hwnd, -21);
    if (message ==
        0x81) /* WM_NCCREATE: native-only infrastructure; never expose this pointer to Win16. */
    {
        if (h)
        {
            return 0; /* reject a replay to an already-owned window before dereferencing LPARAM */
        }
        NativeCreate *c = (NativeCreate *)lp;
        h = c
            ? c->parameter
            : 0;
        if (!h || !h->app || !h->nextId || h->nextId > 16)
        {
            return 0;
        }
        WindowIdentity *w = &h->windows[h->nextId - 1];
        w->native = hwnd;
        native_SetWindowLongPtrW(hwnd, -21, (sptr)h);
    }
    if (!h)
    {
        return native_DefWindowProcW(hwnd, message, wp, lp);
    }
#ifdef NE_SCENE
    if (level_timer_gate(h, hwnd, message, wp))
    {
        return 0;
    }
#endif
    if (message == 0x82)
    {
        if (!h->destroying && !h->creating)
        {
            return 0;
        }
        WindowIdentity *w = window_native(h, hwnd);
        if (w)
        {
            w->live = 0;
            w->native = 0;
        }
        native_SetWindowLongPtrW(hwnd, -21, 0);
        return native_DefWindowProcW(hwnd, message, wp, lp);
    }
#ifdef NE_INPUT
    input_focus(h, hwnd, message);
    sptr inputResult = 0;
    if (input_route(h, hwnd, message, wp, lp, &inputResult))
    {
        return inputResult;
    }
#endif
    int supported = message == 0 || message == 1 || message == 2 || message == 5 ||
        message == 0x10 || message == 0x100 || message == 0x101 || message == 0x102 ||
        message == 0x111 || message == 0x113;
#ifdef NE_PAINT
    supported = supported || message == 0xf;
#endif
    if (!supported)
    {
        return native_DefWindowProcW(hwnd, message, wp, lp);
    }
#ifdef NE_SCENE
    WindowIdentity *identity = window_native(h, hwnd);
    if (!identity)
    {
        return native_DefWindowProcW(hwnd, message, wp, lp);
    }
    if (!identity->target)
    {
        if (message == 0x10 && level_stop(h))
        {
            h->result = 50;
        }
#ifdef NE_INPUT
        if (message == 0x10 && hazard_detach_menu(h))
        {
            h->result = 50;
        }
#endif
        if (message == 2)
        {
            native_PostQuitMessage(0);
        }
        if (message == 0x10)
        {
            h->destroying++;
        }
        sptr answer = native_DefWindowProcW(hwnd, message, wp, lp);
        if (message == 0x10)
        {
            h->destroying--;
        }
        return answer;
    }
    u32 previousTarget = h->boundTarget;
    h->boundTarget = identity->target;
#endif
    sptr value = window_dispatch(h, hwnd, message, wp, lp);
#ifdef NE_SCENE
    h->boundTarget = previousTarget;
#else
    if (message == 2)
    {
        native_PostQuitMessage(0);
    }
#endif
    if (h->result)
    {
        native_PostQuitMessage((int)h->result);
    }
    return value;
}

static u32 window_arg32(Runtime *r, State *s, u32 index)
{
    return arg(r, s, index) | (u32)arg(r, s, index + 1) << 16;
}

static int window_import_target(Runtime *r, State *s, u16 offset)
{
    WindowHost *h = window_host(r);
    if (!h || native_GetCurrentThreadId() != h->app->ownerThread)
    {
        r->error = 14;
        return 1;
    }
    u16 oldCs = s->cs, oldIp = s->ip, sp = s->sp;
    push(r, s, oldCs);
    push(r, s, oldIp);
    u32 value = 0;
    u16 cleanup = 0;
    WindowIdentity *w;
    switch (offset)
    {
    case WINDOW_RegisterClass:
        cleanup = 4;
        value = window_register(h, window_arg32(r, s, 0));
        break;
    case WINDOW_CreateWindow:
    {
        cleanup = 30;
#ifdef NE_INPUT
        if (h->app->tiles)
        {
            value = hint_create(h, s);
            break;
        }
#endif
        u16 name[80];
        if (!h->registered || h->nextId == 16 || !window_string(r, window_arg32(r, s, 13), name) ||
            !window_string(r, window_arg32(r, s, 11), h->title))
        {
            r->error = 14;
            break;
        }
        int equal = 1;
        for (u32 i = 0; i < 80; i++)
        {
            if (name[i] != h->name[i])
            {
                equal = 0;
                break;
            }
            if (!name[i])
            {
                break;
            }
        }
        if (!equal || arg(r, s, 4) || arg(r, s, 3) || arg(r, s, 2) != h->app->instance ||
            window_arg32(r, s, 0))
        {
            r->error = 14;
            break;
        }
        w = &h->windows[h->nextId++];
        w->id = (u16)h->nextId;
        w->live = 1;
        h->creating = 1;
        void *native = native_CreateWindowExW(
            0, h->name, h->title, window_arg32(r, s, 9), (short)arg(r, s, 8), (short)arg(r, s, 7),
            (short)arg(r, s, 6), (short)arg(r, s, 5), 0, 0, h->app->module, h);
        h->creating = 0;
        if (!native)
        {
            w->live = 0;
            w->native = 0;
            break;
        }
        value = w->id;
        break;
    }
    case WINDOW_DestroyWindow:
        cleanup = 2;
        w = window_id(h, arg(r, s, 0));
        h->destroying++;
        value = w
            ? native_DestroyWindow(w->native) != 0
            : 0;
        h->destroying--;
#ifdef NE_INPUT
        if (h->app->tiles && w == &h->windows[7] && !w->live)
        {
            h->nextId = 7;
            h->app->hintDestroys++;
        }
#endif
        break;
    case WINDOW_ShowWindow:
        cleanup = 4;
        w = window_id(h, arg(r, s, 1));
        if (w)
        {
            value = native_ShowWindow(w->native, arg(r, s, 0)) != 0;
        }
        break;
    case WINDOW_UpdateWindow:
        cleanup = 2;
        w = window_id(h, arg(r, s, 0));
        if (w)
        {
            value = native_UpdateWindow(w->native) != 0;
        }
        break;
    case WINDOW_GetWindowWord:
    case WINDOW_SetWindowWord:
    {
        int set = offset == WINDOW_SetWindowWord;
        cleanup = set
            ? 6
            : 4;
        w = window_id(h, arg(r, s, set
            ? 2
            : 1));
        short index = (short)arg(r, s, set
            ? 1
            : 0);
        if (!w || index < 0 || index % 2 || (u32)index + 2 > h->extra)
        {
            r->error = 14;
            break;
        }
        value = w->words[index / 2];
        if (set)
        {
            w->words[index / 2] = arg(r, s, 0);
        }
        break;
    }
    case WINDOW_GetClientRect:
    {
        cleanup = 6;
        w = window_id(h, arg(r, s, 2));
        u8 *p = window_memory(r, window_arg32(r, s, 0), 8, 1);
        NativeRect rect;
#ifdef NE_PAINT
        int got = paint_client_rect(h, w, &rect);
#else
        int got = w && native_GetClientRect(w->native, &rect);
#endif
        if (w && p && got)
        {
            if (!word_signed(rect.right) || !word_signed(rect.bottom))
            {
                r->error = 14;
                break;
            }
            put16(p, (u16)rect.left);
            put16(p + 2, (u16)rect.top);
            put16(p + 4, (u16)rect.right);
            put16(p + 6, (u16)rect.bottom);
            value = 1;
        }
        break;
    }
    case WINDOW_InvalidateRect:
    {
        cleanup = 8;
        w = window_id(h, arg(r, s, 3));
        u32 ptr = window_arg32(r, s, 1);
        NativeRect rect;
        NativeRect *rp = 0;
        if (ptr)
        {
            u8 *p = window_memory(r, ptr, 8, 0);
            if (!p)
            {
                break;
            }
            rect.left = (short)get16(p);
            rect.top = (short)get16(p + 2);
            rect.right = (short)get16(p + 4);
            rect.bottom = (short)get16(p + 6);
            rp = &rect;
        }
        if (w)
        {
            value = native_InvalidateRect(w->native, rp, arg(r, s, 0) != 0) != 0;
        }
        break;
    }
    case WINDOW_PostQuitMessage:
        cleanup = 2;
        native_PostQuitMessage((short)arg(r, s, 0));
        break;
    case WINDOW_GetMessage:
    {
        cleanup = 10;
        u8 *p = window_memory(r, window_arg32(r, s, 3), 18, 1);
        u16 filter = arg(r, s, 2), first = arg(r, s, 1), last = arg(r, s, 0);
        WindowIdentity *filtered = filter
            ? window_id(h, filter)
            : 0;
        if (!p || (filter && !filtered) || (last && first > last))
        {
            r->error = 14;
            break;
        }
        int status = native_GetMessageW(&h->pending, filtered
            ? filtered->native
            : 0, first, last);
        u16 wp = 0;
        u32 lp = 0;
        if (status < 0 ||
            (status && !window_convert(h, h->pending.hwnd, h->pending.message, h->pending.wparam, h->pending.lparam, &wp, &lp)))
        {
            r->error = 14;
            break;
        }
        WindowIdentity *id = window_native(h, h->pending.hwnd);
        zero(p, 18);
        put16(p, id
            ? id->id
            : 0);
        put16(p + 2, (u16)h->pending.message);
        put16(p + 4, status
            ? wp
            : (u16)h->pending.wparam);
        put32(p + 6, lp);
        if (!word_signed(h->pending.x) || !word_signed(h->pending.y))
        {
            r->error = 14;
            break;
        }
        put32(p + 10, h->pending.time);
        put16(p + 14, (u16)h->pending.x);
        put16(p + 16, (u16)h->pending.y);
        h->pendingValid = status != 0;
        value = status != 0;
        break;
    }
    case WINDOW_TranslateMessage:
    case WINDOW_DispatchMessage:
    {
        cleanup = 4;
        u8 *p = window_memory(r, window_arg32(r, s, 0), 18, 0);
        WindowIdentity *id = window_native(h, h->pending.hwnd);
        u16 wp;
        u32 lp;
        if (!p || !id || !h->pendingValid ||
            !window_convert(h, h->pending.hwnd, h->pending.message, h->pending.wparam,
                            h->pending.lparam, &wp, &lp) ||
            get16(p) != id->id || get16(p + 2) != h->pending.message || get16(p + 4) != wp ||
            get32(p + 6) != lp)
        {
            r->error = 14;
            break;
        }
        if (offset == WINDOW_TranslateMessage)
        {
            value = native_TranslateMessage(&h->pending) != 0;
        }
        else
        {
            value = (u32)native_DispatchMessageW(&h->pending);
            h->pendingValid = 0;
        }
        break;
    }
    case WINDOW_SendMessage:
    {
        cleanup = 10;
        w = window_id(h, arg(r, s, 4));
        u16 message = arg(r, s, 3), wp = arg(r, s, 2);
        u32 lp = window_arg32(r, s, 0);
        uptr nativeWp = wp;
        sptr nativeLp = (sptr)(int)lp;
        if (!w)
        {
            r->error = 14;
            break;
        }
        if (message == 0 || message == 2 || message == 0x10)
        {
            if (wp || lp || message == 2)
            {
                r->error = 14;
                break;
            }
        }
        else if (message == 0x113)
        {
            if (lp)
            {
                r->error = 14;
                break;
            }
        }
        else if (message == 0x111)
        {
            WindowIdentity *control = (u16)lp
                ? window_id(h, (u16)lp)
                : 0;
            if ((u16)lp && !control)
            {
                r->error = 14;
                break;
            }
            nativeWp = wp | (lp & 0xffff0000u);
            nativeLp = control
                ? (sptr)control->native
                : 0;
        }
        else if (message != 5 && message != 0x100 && message != 0x101 && message != 0x102)
        {
            r->error = 14;
            break;
        }
        sptr answer = native_SendMessageW(w->native, message, nativeWp, nativeLp);
        if (!long_value(answer))
        {
            r->error = 14;
            break;
        }
        value = (u32)answer;
        break;
    }
#ifdef NE_SCENE
    case 0x318:
    case 0x31c:
        value = movement_service(h, s, offset, &cleanup);
        break;
    case 0x300:
    case 0x304:
    case 0x308:
    case 0x30c:
    case 0x310:
    case 0x314:
    case 0x320:
        value = level_service(h, s, offset, &cleanup);
        break;
    case WINDOW_BitBlt:
    case WINDOW_LoadBitmap:
    case WINDOW_SelectObject:
    case WINDOW_DeleteObject:
    case WINDOW_GetStockObject:
    case WINDOW_PatBlt:
        value = scene_service(h, s, offset, &cleanup);
        break;
#endif
#ifdef NE_INPUT
    case 0x36c:
    case 0x370:
    case 0x374:
    case 0x378:
    case 0x37c:
    case 0x380:
    case 0x384:
    case 0x388:
    case 0x38c:
        value = completion_service(h, s, offset, &cleanup);
        break;
    case 0x344:
    case 0x348:
    case 0x34c:
    case 0x350:
    case 0x354:
    case 0x358:
    case 0x35c:
    case 0x360:
    case 0x364:
    case 0x368:
        value = hazard_service(h, s, offset, &cleanup);
        break;
    case 0x324:
    case 0x328:
    case 0x32c:
    case 0x330:
    case 0x334:
    case 0x338:
    case 0x33c:
    case 0x340:
        value = hint_service(h, s, offset, &cleanup);
        break;
    case WINDOW_GetDC:
    case WINDOW_ReleaseDC:
        value = input_service(h, s, offset, &cleanup);
        break;
#endif
    /* Deferred painting endpoints are deliberate failures, not successful stand-ins. */
    case WINDOW_BeginPaint:
    case WINDOW_EndPaint:
    case WINDOW_SetDIBitsToDevice:
#ifdef NE_PAINT
    case WINDOW_FindResource:
    case WINDOW_LoadResource:
    case WINDOW_LockResource:
    case WINDOW_FreeResource:
        value = paint_service(h, s, offset, &cleanup);
        break;
#else
        r->error = 5;
        break;
#endif
    default:
        r->error = 5;
        break;
    }
    s->ip = pop(r, s);
    s->cs = pop(r, s);
    s->sp = (u16)(s->sp + cleanup);
    s->ax = (u16)value;
    s->dx = (u16)(value >> 16);
    if (!r->error && (s->cs != oldCs || s->ip != oldIp || s->sp != (u16)(sp + cleanup)))
    {
        r->error = 14;
#ifdef NE_SCENE
        h->app->oracleOffset = offset;
        h->app->oracleActual = s->sp;
        h->app->oracleExpected = (u16)(sp + cleanup);
#endif
    }
#ifdef NE_SCENE
    if (r->error && !h->app->lastFailedService)
    {
        h->app->lastFailedService = offset;
    }
#endif
    return 1;
}

static u32 window_call(NativeApplication *a, u16 offset, const u16 *words, u32 count)
{
    State saved = a->state;
    for (u32 i = count; i; i--)
    {
        push(&a->runtime, &a->state, words[i - 1]);
    }
    window_import_target(&a->runtime, &a->state, offset);
    u32 value = a->state.ax | (u32)a->state.dx << 16;
    if (a->state.sp != saved.sp)
    {
        a->runtime.error = 14;
    }
    a->state = saved;
    return value;
}

static u32 window_close(NativeApplication *a)
{
    WindowHost *h = a->windowHost;
    if (!h)
    {
        return 0;
    }
    u32 result = 0;
    if (h->active || a->runtime.callbackDepth)
    {
        return 50;
    }
#ifdef NE_SCENE
    if (level_stop(h))
    {
        result = 50;
    }
#endif
#ifdef NE_INPUT
    if (completion_close(h))
    {
        result = 50;
    }
    if (hazard_close(h))
    {
        result = 50;
    }
    if (hint_close(h))
    {
        result = 50;
    }
#endif
#ifdef NE_PAINT
    if (paint_abort_frames(h))
    {
        result = 50;
    }
#endif
#ifdef NE_INPUT
    if (input_close(h))
    {
        result = 50;
    }
#endif
    h->destroying++;
    for (u32 i = 0; i < h->nextId; i++)
    {
        if (h->windows[i].live && !native_DestroyWindow(h->windows[i].native))
        {
            result = 50;
        }
    }
    h->destroying--;
    /* Failed destruction must not leave a callback pointing into freed owner storage. */
    for (u32 i = 0; i < h->nextId; i++)
    {
        if (h->windows[i].live)
        {
            native_SetWindowLongPtrW(h->windows[i].native, -21, 0);
        }
    }
    if (h->registered && !native_UnregisterClassW(h->name, a->module))
    {
        result = 50;
    }
    for (u32 i = 0; i < h->nextId; i++)
    {
        if (h->windows[i].live)
        {
            result = 50;
        }
    }
#ifdef NE_PAINT
#ifdef NE_SCENE
    if (scene_close(h))
    {
        result = 50;
    }
#endif
    if (paint_close(h))
    {
        result = 50;
    }
#endif
    h->bindingLive = 0;
    h->bindingGeneration++;
    for (u32 i = 0; i < a->callbackLimit; i++)
    {
        if (h->callbackStacks[i])
        {
            Segment *stack = find_segment(&a->runtime, h->callbackStacks[i]);
            if (!callback_frame_valid(h, i))
            {
                result = 50;
            }
#ifndef NE_SCENE
            unmap(&a->runtime, h->callbackStacks[i]);
#endif
            h->callbackStacks[i] = 0;
        }
    }
    a->runtime.user = 0;
    heap_release(&a->storage, h);
    a->windowHost = 0;
    return result;
}

static int callback_state_equal(const State *a, const State *b)
{
    for (u32 i = 0; i < sizeof(*a); i++)
    {
        if (((const u8 *)a)[i] != ((const u8 *)b)[i])
        {
            return 0;
        }
    }
    return 1;
}

static u32 window_callback_probes(WindowHost *h, WindowIdentity *w)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    State saved = a->state;
    u32 delivered = h->translated;
    /* Deliberately stale bindings and a foreign owner must be rejected before CPU/stack mutation. */
    for (u32 fault = 0; fault < 4; fault++)
    {
        u32 owner = a->ownerThread, target = h->boundTarget, generation = h->bindingGeneration;
        if (fault == 0)
        {
            h->bindingLive = 0;
        }
        if (fault == 1)
        {
            a->ownerThread ^= 0x80000000u;
        }
        if (fault == 2)
        {
            h->boundTarget ^= 1;
        }
        if (fault == 3)
        {
            h->bindingGeneration++;
        }
        window_dispatch(h, w->native, 0, 0, 0);
        int rejected = h->result == 60 && !h->active && !r->callbackDepth &&
            h->translated == delivered && callback_state_equal(&saved, &a->state);
        h->bindingLive = 1;
        a->ownerThread = owner;
        h->boundTarget = target;
        h->bindingGeneration = generation;
        h->result = 0;
        if (!rejected)
        {
            return 40;
        }
    }
    /* Every native SendMessage must restore all 44 caller CPU bytes, including high halves
     * and DF, while leaving original DS-memory/window-word changes intact. */
    for (u32 iteration = 0; iteration < 32; iteration++)
    {
        a->state.ax = 0x1234;
        a->state.bx = 0x5678;
        a->state.cx = 0x9abc;
        a->state.dx = 0xdef0;
        a->state.si = 0x1357;
        a->state.di = 0x2468;
        a->state.bp = 0x4321;
        a->state.flags |= 0x400;
        for (u32 i = 0; i < 8; i++)
        {
            a->state.high[i] = (u16)(0x8100 + i + iteration);
        }
        State before = a->state;
        sptr value = native_SendMessageW(w->native, 0x113, 7, 0);
        if (h->result || r->error)
        {
            a->state = saved;
            return h->result
                ? h->result
                : 30;
        }
        if (value != (sptr)(int)(a->callbackOracle
            ? a->callbackExpected
            : 0) ||
            !callback_state_equal(&before, &a->state) || h->active || r->callbackDepth)
        {
            a->state = saved;
            return 40;
        }
        for (u32 i = 0; i < a->callbackLimit; i++)
        {
            if (!callback_stack_valid(find_segment(r, h->callbackStacks[i])))
            {
                a->state = saved;
                return 40;
            }
        }
    }
    a->state = saved;
    return 0;
}

static u32 window_run(NativeApplication *a)
{
    Runtime *r = &a->runtime;
    WindowHost *h = heap_alloc(&a->storage, sizeof(WindowHost));
    if (!h)
    {
        return 20;
    }
    zero(h, sizeof(*h));
    h->app = a;
    a->windowHost = h;
    r->user = a;
    h->automaticDs = a->state.ds;
    for (u32 i = 0; i < a->callbackLimit; i++)
    {
#ifdef NE_SCENE
        Segment *stack = find_segment(r, h->automaticDs);
        u32 base = 0x5000 + i * a->callbackStackBytes;
        if (!stack || base > stack->size || a->callbackStackBytes > stack->size - base)
        {
            return 10;
        }
        h->callbackStacks[i] = h->automaticDs;
        h->callbackOffsets[i] = base;
        for (u32 j = 0; j < 16; j++)
        {
            stack->bytes[base + j] = 0xa5;
            stack->bytes[base + a->callbackStackBytes - 16 + j] = 0x5a;
        }
#else
        u32 selector = 0xd000;
        while (selector < 0xe000 && find_segment(r, (u16)selector))
        {
            selector++;
        }
        if (selector == 0xe000)
        {
            return 20;
        }
        Segment *stack = map(r, (u16)selector, a->callbackStackBytes);
        if (!stack)
        {
            return 20;
        }
        h->callbackStacks[i] = (u16)selector;
        for (u32 j = 0; j < 16; j++)
        {
            stack->bytes[j] = 0xa5;
            stack->bytes[stack->size - 16 + j] = 0x5a;
        }
#endif
    }
#ifdef NE_PAINT
    u32 graphicsResult = paint_open(h);
    if (graphicsResult)
    {
        return graphicsResult;
    }
#endif
    u8 *task = window_memory(r, (u32)a->taskSelector << 16, 4096, 1);
    if (!task)
    {
        return 10;
    }
#ifdef NE_SCENE
    u32 sceneResult = scene_open(h);
    if (sceneResult)
    {
        return sceneResult;
    }
    return scene_run(h);
#endif
    const char *name = "Win16TranslatedCounter",
               *title = "Chip's Challenge - original message dispatch";
    for (u32 i = 0; name[i]; i++)
    {
        task[128 + i] = (u8)name[i];
    }
    for (u32 i = 0; title[i]; i++)
    {
        task[192 + i] = (u8)title[i];
    }
    u8 *cls = task + 32;
    put16(cls, 3);
    put32(cls + 2, a->rootPointer);
    put16(cls + 8, 4);
    put16(cls + 10, a->instance);
    put16(cls + 16, 6);
    put32(cls + 22, ((u32)a->taskSelector << 16) | 128);
    u16 registerArgs[] = {32, a->taskSelector};
    if (a->automation)
    {
        put32(cls + 2, a->rootPointer + 1);
        if (window_call(a, WINDOW_RegisterClass, registerArgs, 2) || r->error != 14)
        {
            return 40;
        }
        r->error = 0;
        put32(cls + 2, a->rootPointer);
    }
    if (!window_call(a, WINDOW_RegisterClass, registerArgs, 2) || r->error)
    {
        return 60;
    }
    u16 createArgs[] = {
        0, 0, 1, 0, 0, 180, 420, 100, 100, 0, 0xcf, 192, a->taskSelector, 128, a->taskSelector};
    u16 id = (u16)window_call(a, WINDOW_CreateWindow, createArgs, 15);
    WindowIdentity *w = window_id(h, id);
    if (h->result)
    {
        return h->result;
    }
    if (r->error)
    {
        return 30;
    }
    if (!w)
    {
        return 60;
    }
    u16 setWords[] = {123, 0, id};
    window_call(a, WINDOW_SetWindowWord, setWords, 3);
    u16 getWords[] = {0, id};
    if (window_call(a, WINDOW_GetWindowWord, getWords, 2) != 123)
    {
        return 40;
    }
    u16 rectArgs[] = {544, a->taskSelector, id};
    if (!window_call(a, WINDOW_GetClientRect, rectArgs, 3))
    {
        return 40;
    }
#ifdef NE_PAINT
    if (a->automation)
    {
        u32 verification = paint_verify(h, w);
        if (verification)
        {
            return verification;
        }
    }
#endif
    u16 showArgs[] = {a->showState, id};
    window_call(a, WINDOW_ShowWindow, showArgs, 2);
    u16 updateArgs[] = {id};
    window_call(a, WINDOW_UpdateWindow, updateArgs, 1);
    u16 invalidateArgs[] = {1, 0, 0, id};
    window_call(a, WINDOW_InvalidateRect, invalidateArgs, 4);
    if (r->error || h->result)
    {
        return 30;
    }
#ifdef NE_PAINT
    if (a->automation)
    {
        CounterGraphics *g = a->graphics;
        u32 before = g->paints;
        window_call(a, WINDOW_UpdateWindow, updateArgs, 1);
        if (g->paints <= before || r->error)
        {
            return 40;
        }
    }
#endif
    if (a->automation)
    {
        u32 probes = window_callback_probes(h, w);
        if (probes)
        {
            return probes;
        }
        u16 convertedWp;
        u32 convertedLp;
        if (!window_convert(h, w->native, 0x111, 7 | (42u << 16), (sptr)w->native, &convertedWp,
                            &convertedLp) ||
            convertedWp != 7 || convertedLp != (id | (42u << 16)) ||
            window_convert(h, w->native, 0x111, 7, (sptr)0x123400001234ull, &convertedWp,
                           &convertedLp) ||
            window_convert(h, w->native, 0x113, 0x10000, 0, &convertedWp, &convertedLp) ||
            window_convert(h, w->native, 0x113, 1, (sptr)0x123400001234ull, &convertedWp,
                           &convertedLp) ||
            window_convert(h, w->native, 0x100, 0x10000, 0, &convertedWp, &convertedLp) ||
            window_convert(h, w->native, 5, 0, (sptr)0x123400001234ull, &convertedWp,
                           &convertedLp) ||
            window_convert(h, w->native, 0x4e, 0, (sptr)0x123400001234ull, &convertedWp,
                           &convertedLp))
        {
            return 40;
        }
        u16 invalidWord[] = {1, id};
        window_call(a, WINDOW_GetWindowWord, invalidWord, 2);
        if (r->error != 14)
        {
            return 40;
        }
        r->error = 0;
        if (!native_PostMessageW(w->native, 0, 0, 0) ||
            !native_PostMessageW(w->native, 0x113, 1, 0))
        {
            return 60;
        }
        u16 messageArgs[] = {0x113, 0x113, id, 512, a->taskSelector};
        if (!window_call(a, WINDOW_GetMessage, messageArgs, 5) || r->error)
        {
            return 30;
        }
        u16 dispatchArgs[] = {512, a->taskSelector};
        window_call(a, WINDOW_TranslateMessage, dispatchArgs, 2);
        window_call(a, WINDOW_DispatchMessage, dispatchArgs, 2);
        if (r->error || h->result)
        {
            return 30;
        }
        /* Deterministic messages exercise packing through the real queue and original procedure. */
        if (!native_PostMessageW(w->native, 0x100, 0x25, 0x004b0001) ||
            !native_PostMessageW(w->native, 0x101, 0x25, (sptr)(int)0xc04b0001) ||
            !native_PostMessageW(w->native, 0x102, 'A', 1) ||
            !native_PostMessageW(w->native, 0x111, 7 | (2u << 16), (sptr)w->native) ||
            !native_PostMessageW(w->native, 0x113, 1, 0) ||
            !native_PostMessageW(w->native, 0x10, 0, 0))
        {
            return 60;
        }
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
    if (r->error || h->result)
    {
        return h->result
            ? h->result
            : 30;
    }
    if (h->active || r->callbackDepth ||
        (a->automation && (h->maximumDepth < 2 || !h->nestedEntries)))
    {
        return 40;
    }
    /* CREATE, SIZE, CLOSE, DESTROY must traverse original instructions; automation adds keyboard/command/timer. */
    u32 required = a->automation
        ? 511
        : 385;
    return (h->mask & required) == required && !w->live
        ? 0
        : 40;
}

#ifdef NE_SCENE
#include "ne_scene.h"
#endif

#ifdef NE_INPUT
#include "ne_input.h"
#include "ne_clock.h"
#include "ne_hint.h"
#include "ne_hazard.h"
#include "ne_completion.h"
#endif
