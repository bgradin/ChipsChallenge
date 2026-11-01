/* Freestanding Windows entries. Contexts and native pointers never enter Win16 argument slots. */
extern __attribute__((visibility("hidden"))) ABI void *
native_CreateWindowExW(u32, const u16 *, const u16 *, u32, int, int, int, int, void *, void *,
                       void *, void *);
extern __attribute__((visibility("hidden"))) ABI int native_DestroyWindow(void *);
extern __attribute__((visibility("hidden"))) ABI int native_EnumChildWindows(void *, void *,
                                                                             long long);
extern __attribute__((visibility("hidden"))) ABI long long
native_CallWindowProcW(void *, void *, u32, unsigned long long, long long);
extern __attribute__((visibility("hidden"))) ABI long long
native_DefWindowProcW(void *, u32, unsigned long long, long long);
extern __attribute__((visibility("hidden"))) ABI void *
native_VirtualAlloc(void *, unsigned long long, u32, u32);
extern __attribute__((visibility("hidden"))) ABI int
native_VirtualProtect(void *, unsigned long long, u32, u32 *);
extern __attribute__((visibility("hidden"))) ABI int native_VirtualFree(void *, unsigned long long,
                                                                        u32);
extern __attribute__((visibility("hidden"))) ABI int
native_FlushInstructionCache(void *, const void *, unsigned long long);
extern __attribute__((visibility("hidden"))) ABI void *native_GetCurrentProcess(void);
extern __attribute__((visibility("hidden"))) ABI u32 native_GetCurrentThreadId(void);
extern __attribute__((visibility("hidden"))) ABI u16 translated_ENUMCALLBACK(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI u16 translated_STOPCALLBACK(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI u16 translated_VALUEWNDPROC(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI u16 translated_SAMPLEWNDPROC(Runtime *, State *);
typedef struct
{
    Runtime *r;
    State *s;
    HandleTable windows;
    const u32 *messages;
    u32 messageCount, owner, active, error, enumTarget, windowTarget, user, enumCalls, windowCalls,
        forwardCalls;
} WindowsCallbacks;

static u16 window_id(WindowsCallbacks *c, void *hwnd)
{
    for (NativeHandle *h = c->windows.entries; h; h = h->next)
    {
        if (h->value == (unsigned long long)hwnd)
        {
            return h->id;
        }
    }
    c->error = 22;
    return 0;
}

static int callback_ready(WindowsCallbacks *c)
{
    if (!c)
    {
        return 0;
    }
    if (!c->active)
    {
        c->error = 23;
        return 0;
    }
    if (c->owner != native_GetCurrentThreadId())
    {
        c->error = 21;
        return 0;
    }
    return 1;
}

static ABI int enum_entry(void *hwnd, long long parameter)
{
    WindowsCallbacks *c = (WindowsCallbacks *)parameter;
    if (!callback_ready(c))
    {
        return 0;
    }
    u16 id = window_id(c, hwnd);
    if (!id)
    {
        return 0;
    }
    u16 words[] = {(u16)c->user, (u16)(c->user >> 16), id};
    CallbackFrame frame = {words, 3, 0};
    c->enumCalls++;
    u32 result = __win16_callback_invoke(c->r, c->s, c->enumTarget, &frame);
    if (c->r->error)
    {
        c->error = c->r->error;
        return 0;
    }
    return result != 0;
}

static ABI long long window_entry(void *hwnd, u32 message, unsigned long long wParam,
                                  long long lParam, WindowsCallbacks *c)
{
    if (!callback_ready(c))
    {
        return 0;
    }
    int allowed = 0;
    for (u32 i = 0; i < c->messageCount; i++)
    {
        if (c->messages[i] == message)
        {
            allowed = 1;
        }
    }
    if (!allowed || message > 65535 || wParam > 65535 || lParam < (-2147483647ll - 1) ||
        lParam > 2147483647ll)
    {
        c->error = 20;
        return 0;
    }
    u16 id = window_id(c, hwnd);
    if (!id)
    {
        return 0;
    }
    u16 words[] = {(u16)lParam, (u16)((unsigned long long)lParam >> 16), (u16)wParam, (u16)message,
                   id};
    CallbackFrame frame = {words, 5, 1};
    c->windowCalls++;
    u32 result = __win16_callback_invoke(c->r, c->s, c->windowTarget, &frame);
    if (c->r->error)
    {
        c->error = c->r->error;
        return 0;
    }
    return (long long)(int)result;
}

static void dispatch_defwindowproc(Runtime *r, State *s)
{
    WindowsCallbacks *c = r->user;
    s->ax = 0;
    s->dx = 0;
    if (!c)
    {
        r->error = 14;
        return;
    }
    u32 message = arg(r, s, ARG_DefWindowProc_message),
        value = arg(r, s, ARG_DefWindowProc_lParam) |
        ((u32)arg(r, s, ARG_DefWindowProc_lParam + 1) << 16);
    if ((message != 0 && message != 0x87) || (message == 0x87 && value))
    {
        c->error = 20;
        return;
    }
    u16 id = arg(r, s, ARG_DefWindowProc_hwnd);
    NativeHandle *h = c->windows.entries;
    while (h && h->id != id)
    {
        h = h->next;
    }
    if (!h)
    {
        c->error = 22;
        return;
    }
    c->forwardCalls++;
    long long result = native_DefWindowProcW((void *)h->value, message,
                                             arg(r, s, ARG_DefWindowProc_wParam), (int)value);
    if (result < (-2147483647ll - 1) || result > 2147483647ll)
    {
        c->error = 20;
        return;
    }
    s->ax = (u16)result;
    s->dx = (u16)((unsigned long long)result >> 16);
}

static void store_u64(u8 *p, unsigned long long value)
{
    for (u32 i = 0; i < 8; i++)
    {
        p[i] = (u8)(value >> (i * 8));
    }
}

static void *create_window_entry(WindowsCallbacks *c)
{
    /* Win64 WNDPROC -> window_entry(hwnd,msg,wParam,lParam,context). */
    u8 *p = native_VirtualAlloc(0, 36, 0x3000, 4);
    if (!p)
    {
        return 0;
    }
    const u8 code[] = {0x48, 0x83, 0xEC, 0x38, 0x48, 0xB8, 0, 0, 0, 0, 0, 0,
                       0, 0, 0x48, 0x89, 0x44, 0x24, 0x20, 0x48, 0xB8, 0, 0, 0,
                       0, 0, 0, 0, 0, 0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x38, 0xC3};
    for (u32 i = 0; i < 36; i++)
    {
        p[i] = code[i];
    }
    store_u64(p + 6, (unsigned long long)c);
    store_u64(p + 21, (unsigned long long)window_entry);
    u32 old;
    if (!native_VirtualProtect(p, 36, 0x20, &old) ||
        !native_FlushInstructionCache(native_GetCurrentProcess(), p, 36))
    {
        native_VirtualFree(p, 0, 0x8000);
        return 0;
    }
    return p;
}

static int same_state(const State *a, const State *b)
{
    for (u32 i = 0; i < sizeof(State); i++)
    {
        if (((const u8 *)a)[i] != ((const u8 *)b)[i])
        {
            return 0;
        }
    }
    return 1;
}

ABI NOINLINE int callback_acceptance(void)
{
    FixtureStorage storage;
    Runtime runtime;
    Runtime *r = &runtime;
    State state;
    State *s = &state;
    init(r, s, &storage);
    WindowsCallbacks c;
    zero(&c, sizeof(c));
    c.r = r;
    c.s = s;
    c.windows.runtime = r;
    c.windows.next = 1;
    c.owner = native_GetCurrentThreadId();
    c.active = 1;
    c.enumTarget = 0x40000100;
    c.windowTarget = 0x40000200;
    c.user = 0x12345678;
    r->user = &c;
    CodeBinding counter = {0, 0x4000, 0x0300, 1, translated_SAMPLEWNDPROC};
    CodeBinding value = {&counter, 0x4000, 0x200, 1, translated_VALUEWNDPROC};
    CodeBinding stop = {&value, 0x4000, 0x110, 1, translated_STOPCALLBACK};
    CodeBinding enumeration = {&stop, 0x4000, 0x100, 1, translated_ENUMCALLBACK};
    r->code = &enumeration;
    const u16 className[] = {'S', 'T', 'A', 'T', 'I', 'C', 0}, title[] = {0};
    void *parent = 0, *first = 0, *second = 0, *entry = 0;
    int result = 0;
    parent = native_CreateWindowExW(0, className, title, 0, 0, 0, 8, 8, 0, 0, 0, 0);
    if (!parent)
    {
        result = 140;
        goto cleanup;
    }
    first = native_CreateWindowExW(0, className, title, 0x40000000, 0, 0, 8, 8, parent, 0, 0, 0);
    second = native_CreateWindowExW(0, className, title, 0x40000000, 0, 0, 8, 8, parent, 0, 0, 0);
    if (!first || !second)
    {
        result = 141;
        goto cleanup;
    }
    u16 parentId = add_handle(&c.windows, (unsigned long long)parent),
        firstId = add_handle(&c.windows, (unsigned long long)first),
        secondId = add_handle(&c.windows, (unsigned long long)second);
    if (!parentId || !firstId || !secondId)
    {
        result = 142;
        goto cleanup;
    }
    const u32 valueMessages[] = {0x9001}, counterMessages[] = {0, 0x87};
    c.messages = valueMessages;
    c.messageCount = 1;
    State saved = *s;
    u32 mappings = r->count;
    native_EnumChildWindows(parent, (void *)enum_entry, (long long)&c);
    if (c.error || r->error || c.enumCalls != 2 || data(r, 0x106) != 2 ||
        __win16_read_dword(r, 0x1000, 0x102) != 0x12345678 ||
        (data(r, 0x100) != firstId && data(r, 0x100) != secondId))
    {
        result = 143;
        goto cleanup;
    }
    if (!same_state(s, &saved) || r->count != mappings || r->callbackDepth)
    {
        result = 144;
        goto cleanup;
    }
    c.enumTarget = 0x40000110;
    c.enumCalls = 0;
    word(r, 0x106, 0);
    native_EnumChildWindows(parent, (void *)enum_entry, (long long)&c);
    if (c.error || r->error || c.enumCalls != 1 || data(r, 0x106) != 1)
    {
        result = 145;
        goto cleanup;
    }
    entry = create_window_entry(&c);
    if (!entry)
    {
        result = 146;
        goto cleanup;
    }
    long long returned = native_CallWindowProcW(entry, parent, 0x9001, 0xBEEF, -123);
    if (c.error || r->error || returned != (long long)(int)0x81235678u ||
        data(r, 0x110) != parentId || data(r, 0x112) != 0x9001 || data(r, 0x114) != 0xBEEF ||
        __win16_read_dword(r, 0x1000, 0x116) != (u32)-123)
    {
        result = 147;
        goto cleanup;
    }
    if (!same_state(s, &saved) || r->count != mappings)
    {
        result = 148;
        goto cleanup;
    }
    c.windowTarget = 0x40000300;
    c.messages = counterMessages;
    c.messageCount = 2;
    long long expected = native_DefWindowProcW(parent, 0x87, 0, 0);
    if (native_CallWindowProcW(entry, parent, 0x87, 0, 0) != expected || c.error || r->error ||
        c.forwardCalls != 1)
    {
        result = 149;
        goto cleanup;
    }
    if (native_CallWindowProcW(entry, parent, 15, 0, 0) || c.error != 20 || !same_state(s, &saved))
    {
        result = 150;
        goto cleanup;
    }
    c.error = 0;
    u32 owner = c.owner;
    c.owner = owner + 1;
    if (native_CallWindowProcW(entry, parent, 0x87, 0, 0) || c.error != 21 ||
        !same_state(s, &saved))
    {
        result = 157;
        goto cleanup;
    }
    c.owner = owner;
    c.error = 0;
    if (window_entry(parent, 0x87, 0, 1, &c) || c.error != 20 || c.forwardCalls != 1 ||
        !same_state(s, &saved))
    {
        result = 158;
        goto cleanup;
    }
    c.error = 0;
    if (!remove_handle(&c.windows, parentId) || native_CallWindowProcW(entry, parent, 0x87, 0, 0) ||
        c.error != 22 || !same_state(s, &saved))
    {
        result = 159;
        goto cleanup;
    }
    c.error = 0;
    c.enumTarget = 0x49999999;
    native_EnumChildWindows(parent, (void *)enum_entry, (long long)&c);
    if (c.error != 11 || r->error != 11 || !same_state(s, &saved) || r->count != mappings)
    {
        result = 151;
        goto cleanup;
    }
    c.error = 0;
    r->error = 0;
    c.active = 0;
    if (native_CallWindowProcW(entry, parent, 0x87, 0, 0) || c.error != 23 ||
        !same_state(s, &saved))
    {
        result = 152;
        goto cleanup;
    }
cleanup:
    c.active = 0;
    r->user = 0;
    destroy_handles(&c.windows);
    if (entry && !native_VirtualFree(entry, 0, 0x8000) && !result)
    {
        result = 153;
    }
    if (second && !native_DestroyWindow(second) && !result)
    {
        result = 154;
    }
    if (first && !native_DestroyWindow(first) && !result)
    {
        result = 155;
    }
    if (parent && !native_DestroyWindow(parent) && !result)
    {
        result = 156;
    }
    destroy_runtime(r);
    return result;
}

ABI NOINLINE int windows_callback_acceptance(void)
{
    int result = acceptance();
    if (result)
    {
        return result;
    }
    result = graphics_acceptance();
    if (result)
    {
        return result;
    }
    return callback_acceptance();
}
