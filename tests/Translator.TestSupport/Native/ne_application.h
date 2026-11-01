/* Persistent application owner. The game-specific startup selection is descriptor metadata.
 * Milestone 5.2 uses a bounded thread-message lifecycle, without windows or callback thunks.
 */
typedef unsigned long long uptr;
typedef long long sptr;
typedef struct
{
    void *hwnd;
    u32 message;
    uptr wparam;
    sptr lparam;
    u32 time;
    int x, y;
    u32 private_;
} NativeMessage;
_Static_assert(sizeof(NativeMessage) == 48 && __builtin_offsetof(NativeMessage, wparam) == 16 &&
                   __builtin_offsetof(NativeMessage, lparam) == 24,
               "Win64 MSG layout mismatch");
extern __attribute__((visibility("hidden"))) ABI void *native_GetModuleHandleW(const u16 *);
extern __attribute__((visibility("hidden"))) ABI u32 native_GetCurrentThreadId(void);
typedef struct
{
    u32 size;
    void *reserved, *desktop, *title;
    u32 x, y, width, height, columns, rows, fill, flags;
    u16 show, reservedBytes;
    u8 *reservedData;
    void *input, *output, *error;
} NativeStartupInfo;
_Static_assert(sizeof(NativeStartupInfo) == 104 &&
                   __builtin_offsetof(NativeStartupInfo, show) == 64,
               "Win64 STARTUPINFOW layout mismatch");
extern __attribute__((visibility("hidden"))) ABI void native_GetStartupInfoW(NativeStartupInfo *);
extern __attribute__((visibility("hidden"))) ABI const u16 *native_GetCommandLineW(void);
extern __attribute__((visibility("hidden"))) ABI void native_OutputDebugStringW(const u16 *);
extern __attribute__((visibility("hidden"))) ABI int native_PeekMessageW(NativeMessage *, void *,
                                                                         u32, u32, u32);
extern __attribute__((visibility("hidden"))) ABI int native_GetMessageW(NativeMessage *, void *,
                                                                        u32, u32);
extern __attribute__((visibility("hidden"))) ABI uptr native_SetTimer(void *, uptr, u32, void *);
extern __attribute__((visibility("hidden"))) ABI int native_KillTimer(void *, uptr);
extern __attribute__((visibility("hidden"))) ABI void native_PostQuitMessage(int);
extern __attribute__((visibility("hidden"))) ABI int native_MessageBoxW(void *, const u16 *,
                                                                        const u16 *, u32);

typedef struct
{
    Runtime runtime;
    State state;
    HeapStorage storage;
    void *module; /* Native module identity never enters a Win16 word. */
    u32 ownerThread, lifecycleState, completed, invocations, interval;
    u32 rootPointer, rootFar, expected, delta, arguments, callerCleanup;
    u32 stackBottom, stackTop, heapOffset, heapSize;
    u16 taskSelector, instance, previousInstance, showState;
    const u8 *words;
    uptr timer;
#ifdef NE_WINDOW
    void *windowHost;
    int automation;
    u32 callbackLimit, callbackStackBytes, callbackOracle, callbackExpected;
#ifdef NE_PAINT
    void *graphics;
    const u8 *graphicsDescriptor;
#ifdef NE_SCENE
    const u8 *sceneDescriptor, *levelDescriptor;
    u32 requestedLevel;
    int gameplay;
    void *level;
    int movement, clock, tiles, hazards, completion;
    void *completionState;
    u32 completionTarget, completionDialogs;
    const u8 *completionPlan;
    u32 listFinalizer, deathDialogs, listCleanup, primaryResult, listCleanupError, oracleOffset,
        oracleActual, oracleExpected, lastFailedService;
    const u8 *modalPlan;
    void *cursors[8], *restartMenu;
    u32 cursorCount, restartCommand, menuOffset;
    u32 hintTarget;
    void *hintFont, *hintSavedFont, *hintBrush, *hintDc;
    const u8 *hintTextHash, *hintText;
    u32 hintPaints, hintCreates, hintDestroys, placardDraws;
    const u8 *clockDescriptor;
    u32 clockDelivered, clockDeferred, clockQueued, clockPosted, clockElapsed, clockCaptured,
        clockCase;
    u16 clockValues[32];
    const u16 *levelPath;
    u16 levelPathStorage[1024];
#ifdef NE_INPUT
    const u8 *inputDescriptor;
#endif
#endif
    const u16 *diagnosticsPath;
    u16 diagnosticsPathStorage[1024];
    int suppressDialog;
    const u16 *originalPath;
    u16 originalPathStorage[1024];
#endif
#endif
} NativeApplication;
#ifdef NE_WINDOW
#include "ne_window.h"
#endif

/* Lifecycle mode has no callbacks; window mode detaches its live native owner before storage teardown. */

static u32 application_close(NativeApplication *a)
{
    if (a->lifecycleState == 4)
    {
        return 0; /* idempotent after successful or partial initialization */
    }
    u32 result = 0;
#ifdef NE_WINDOW
    result = window_close(a);
#endif
    if (a->timer)
    {
        if (!native_KillTimer(0, a->timer))
        {
            result = 50;
        }
        a->timer = 0;
    }
    Runtime *r = &a->runtime;
    HeapStorage *h = &a->storage;
    if (h->heap)
    {
        while (r->code)
        {
            CodeBinding *b = r->code;
            r->code = b->next;
            heap_release(h, b);
        }
        destroy_runtime(r);
        if (h->count || h->bytes || h->blocks || h->error || r->count || r->allocated ||
            r->handles || r->segments || r->code)
        {
            result = 50;
        }
        if (!native_HeapDestroy(h->heap))
        {
            result = 50;
        }
        h->heap = 0;
    }
    a->words = 0;
    a->rootPointer = 0;
    a->module = 0;
    a->lifecycleState = 4;
    return result;
}

static u32 application_open(NativeApplication *a, const u8 *data, const int *procedures, u32 length,
                            u32 available)
{
    Runtime *r = &a->runtime;
    State *s = &a->state;
    HeapStorage *h = &a->storage;
    a->lifecycleState = 1;
    ImageReader d = {data, data + length, 0};
    u32 magic = number(&d, 4), version = number(&d, 4);
    if (magic != 0x3541504e ||
        (version !=
#ifdef NE_INPUT
             5 &&
         version != 7 && version != 8 && version != 9 && version != 10 && version != 11
#elif defined(NE_SCENE)
         4
#elif defined(NE_PAINT)
         3
#elif defined(NE_WINDOW)
         2
#else
         1
#endif
#if defined(NE_SCENE) && !defined(NE_INPUT)
         && version != 6
#endif
         ) ||
        number(&d, 4) != 2 || number(&d, 4) != sizeof(State))
    {
        return 10;
    }
    u32 segments = number(&d, 4), count = number(&d, 4), root = number(&d, 4);
    a->arguments = number(&d, 4);
    a->callerCleanup = number(&d, 4);
    a->expected = number(&d, 4);
    a->delta = number(&d, 4);
    a->invocations = number(&d, 4);
    a->interval = number(&d, 4);
    h->limit = number(&d, 4);
    h->maximum = wide(&d);
    u32 globalLimit = number(&d, 4);
    uptr globalBytes = wide(&d);
    a->stackBottom = number(&d, 4);
    a->stackTop = number(&d, 4);
    a->heapOffset = number(&d, 4);
    a->heapSize = number(&d, 4);
    a->taskSelector = number(&d, 2);
    a->instance = number(&d, 2);
    a->previousInstance = number(&d, 2);
    a->showState = number(&d, 2);
    u32 commandLength = number(&d, 4);
    const u8 *initial = take(&d, sizeof(*s));
    const u8 *command = take(&d, commandLength);
    if (d.error || !segments || segments > 32767 || !count || count > 4096 || count != available ||
        root >= count || a->arguments > 256 ||
        (a->callerCleanup != 0 && a->callerCleanup != a->arguments * 2) || a->invocations < 2 ||
        a->invocations > 100 || a->interval < 10 || a->interval > 1000 || !h->limit ||
        h->limit > 32768 || !h->maximum || h->maximum > 1024ull * 1024 * 1024 || !globalLimit ||
        globalLimit > 32768 || !globalBytes || a->taskSelector != 0xe000 || a->instance != 1 ||
        a->previousInstance || a->showState != 1 || commandLength != 1 || command[0] ||
        a->stackBottom >= a->stackTop || a->stackTop > 65536)
    {
        return 10;
    }
    for (u32 i = 0; i < sizeof(*s); i++)
    {
        ((u8 *)s)[i] = initial[i];
    }
    if (s->sp != a->stackTop || s->sp < a->stackBottom + a->arguments * 2 + 64)
    {
        return 10;
    }
    NativeStartupInfo startup;
    zero(&startup, sizeof(startup));
    startup.size = sizeof(startup);
    native_GetStartupInfoW(&startup);
    if (startup.flags & 1)
    {
        if (startup.show > 11)
        {
            return 20;
        }
        a->showState = startup.show;
    }
    a->ownerThread = native_GetCurrentThreadId();
    a->module = native_GetModuleHandleW(0);
    if (!a->ownerThread || !a->module)
    {
        return 20;
    }
    h->heap = native_HeapCreate(0, 65536, 0);
    if (!h->heap)
    {
        return 20;
    }
    init_runtime(r, h, heap_alloc, heap_release, globalLimit, 32768, globalBytes);
    for (u32 i = 0; i < segments; i++)
    {
        u16 selector = number(&d, 2), permissions = number(&d, 2);
        u32 size = number(&d, 4), initialized = number(&d, 4);
        const u8 *bytes = take(&d, initialized);
        if (d.error || !selector || selector == 0xf000 || selector == a->taskSelector ||
            permissions > 7 || !size || size > 65536 || initialized > size ||
            find_segment(r, selector))
        {
            return 10;
        }
        Segment *segment = map(r, selector, size);
        if (!segment)
        {
            return 20;
        }
        for (u32 j = 0; j < initialized; j++)
        {
            segment->bytes[j] = bytes[j];
        }
        segment->permissions = permissions;
    }
    Segment *stack = find_segment(r, s->ss), *dataSegment = find_segment(r, s->ds),
            *code = find_segment(r, s->cs);
    if (!stack || (stack->permissions & 3) != 3 || a->stackTop > stack->size || !dataSegment ||
        (dataSegment->permissions & 3) != 3 || !code || !(code->permissions & 4) ||
        s->ip >= code->size || a->heapOffset > dataSegment->size ||
        a->heapSize > dataSegment->size - a->heapOffset ||
        (s->ds == s->ss && a->heapSize && a->heapOffset < a->stackTop))
    {
        return 10;
    }
    /* Private runtime task record + empty Win16 command line at offset 16. No DOS PSP or local heap API is claimed. */
    Segment *task = map(r, a->taskSelector,
#ifdef NE_WINDOW
                        4096
#else
                        32
#endif
    );
    if (!task)
    {
        return 20;
    }
    task->bytes[0] = (u8)a->instance;
    task->bytes[2] = (u8)a->showState;
    task->bytes[4] = (u8)s->ds;
    task->bytes[5] = (u8)(s->ds >> 8);
    for (u32 i = 0; i < count; i++)
    {
        u16 selector = number(&d, 2), offset = number(&d, 2);
        u32 farCall = number(&d, 4);
        code = find_segment(r, selector);
        if (d.error || !code || !(code->permissions & 4) || offset >= code->size || farCall != 1)
        {
            return 10;
        }
        for (CodeBinding *b = r->code; b; b = b->next)
        {
            if (b->segment == selector && b->offset == offset)
            {
                return 10;
            }
        }
        CodeBinding *b = heap_alloc(h, sizeof(*b));
        if (!b)
        {
            return 20;
        }
        b->next = r->code;
        b->segment = selector;
        b->offset = offset;
        b->farCall = 1;
        b->entry = (TranslatedEntry)((const u8 *)&procedures[i] + 4 + procedures[i]);
        r->code = b;
        if (i == root)
        {
            a->rootPointer = ((u32)selector << 16) | offset;
            a->rootFar = farCall;
        }
    }
    a->words = take(&d, a->arguments * 2);
#ifdef NE_WINDOW
    a->callbackLimit = number(&d, 4);
    a->callbackStackBytes = number(&d, 4);
    a->callbackOracle = number(&d, 4);
    a->callbackExpected = number(&d, 4);
    if (!a->callbackLimit || a->callbackLimit > 8 || a->callbackStackBytes < 1024 ||
        a->callbackStackBytes > 65536 || a->callbackStackBytes % 16 || a->callbackOracle > 1)
    {
        return 10;
    }
#endif
#ifdef NE_PAINT
    a->graphicsDescriptor = d.p;
    if (d.error || (u32)(d.end - d.p) < 100)
    {
        return 10;
    }
    u32 cases = (u32)d.p[96] | ((u32)d.p[97] << 8) | ((u32)d.p[98] << 16) | ((u32)d.p[99] << 24);
    take(&d, 100);
    if (!cases || cases > 32)
    {
        return 10;
    }
    take(&d, cases * 54);
#endif
#ifdef NE_SCENE
    a->sceneDescriptor = d.p;
    u32 resourceCount = number(&d, 4);
    if (resourceCount != 2)
    {
        return 10;
    }
    take(&d, resourceCount * 60);
    u32 writes = number(&d, 4);
    if (!writes || writes > 64)
    {
        return 10;
    }
    for (u32 i = 0; i < writes; i++)
    {
        number(&d, 2);
        u32 length = number(&d, 2);
        if (!length)
        {
            return 10;
        }
        take(&d, length);
    }
    u32 windows = number(&d, 4);
    if (windows != 7)
    {
        return 10;
    }
    take(&d, windows * 24);
    u32 pixels = number(&d, 4);
    if (!pixels || pixels > 16)
    {
        return 10;
    }
    take(&d, pixels * 46);
#endif
#ifdef NE_SCENE
    if (version == 6 || version == 7 || version == 8 || version == 9 || version == 10 ||
        version == 11)
    {
        a->movement = version >= 7;
        a->clock = version >= 8;
        a->tiles = version >= 9;
        a->hazards = version >= 10;
        a->completion = version == 11;
        a->levelDescriptor = d.p;
        u32 size = number(&d, 4);
        if (size != 108569)
        {
            return 10;
        }
        take(&d, 84);
        u32 calls = number(&d, 4);
        if (calls != 5)
        {
            return 10;
        }
        for (u32 i = 0; i < calls; i++)
        {
            number(&d, 4);
            u32 args = number(&d, 4);
            if (args > 8)
            {
                return 10;
            }
            take(&d, args * 2);
        }
        u32 bindings = number(&d, 4);
        if (bindings != 7)
        {
            return 10;
        }
        take(&d, bindings * 4);
        u32 writes = number(&d, 4);
        if (writes > 16)
        {
            return 10;
        }
        for (u32 i = 0; i < writes; i++)
        {
            number(&d, 2);
            u32 length = number(&d, 2);
            if (!length || length > 64)
            {
                return 10;
            }
            take(&d, length);
        }
        u32 checks = number(&d, 4);
        if (checks > 64)
        {
            return 10;
        }
        for (u32 i = 0; i < checks; i++)
        {
            number(&d, 2);
            u32 length = number(&d, 2);
            if (!length || length > 256)
            {
                return 10;
            }
            take(&d, length);
        }
    }
#endif
#ifdef NE_INPUT
    a->inputDescriptor = d.p;
    take(&d, 8);
    u32 bindings = number(&d, 4);
    if (!bindings || bindings > 16)
    {
        return 10;
    }
    take(&d, bindings * 4);
    u32 keys = number(&d, 4);
    if (!keys || keys > 16)
    {
        return 10;
    }
    take(&d, keys * 2);
    u32 inputWrites = number(&d, 4);
    if (inputWrites > 64)
    {
        return 10;
    }
    for (u32 i = 0; i < inputWrites; i++)
    {
        number(&d, 2);
        u32 length = number(&d, 2);
        if (!length || length > 4096)
        {
            return 10;
        }
        take(&d, length);
    }
    u32 inputCases = number(&d, 4);
    if (!inputCases || inputCases > (a->tiles
        ? 256u
        : a->clock
            ? 64u
            : a->movement
                                                     ? 32u
                                                     : 16u))
    {
        return 10;
    }
    for (u32 c = 0; c < inputCases; c++)
    {
        u32 initial = number(&d, 4);
        if (initial > 64)
        {
            return 10;
        }
        for (u32 i = 0; i < initial; i++)
        {
            number(&d, 2);
            u32 length = number(&d, 2);
            if (!length || length > 4096)
            {
                return 10;
            }
            take(&d, length);
        }
        if (a->hazards)
        {
            u32 modal = number(&d, 4);
            if (modal > 1)
            {
                return 10;
            }
            if (modal)
            {
                u32 length = number(&d, 4);
                if (!length || length > 79)
                {
                    return 10;
                }
                take(&d, length);
                for (u32 group = 0; group < 2; group++)
                {
                    u32 count = number(&d, 4);
                    if (count > 64)
                    {
                        return 10;
                    }
                    for (u32 i = 0; i < count; i++)
                    {
                        number(&d, 2);
                        u32 size = number(&d, 2);
                        if (!size || size > 4096)
                        {
                            return 10;
                        }
                        take(&d, size);
                    }
                }
                u32 count = number(&d, 4);
                if (!count || count > 16)
                {
                    return 10;
                }
                take(&d, count * 46);
            }
        }
        if (a->completion)
        {
            u32 count = number(&d, 4);
            if (count != 0 && count != 6)
            {
                return 10;
            }
            for (u32 i = 0; i < count; i++)
            {
                u32 length = number(&d, 4);
                if (!length || length > 127)
                {
                    return 10;
                }
                take(&d, length);
            }
        }
        u32 events = number(&d, 4);
        if (!events || events > 32)
        {
            return 10;
        }
        take(&d, events * (a->clock
            ? 12u
            : a->movement
                ? 10u
                : 8u));
        u32 checks = number(&d, 4);
        if (!checks || checks > 64)
        {
            return 10;
        }
        for (u32 i = 0; i < checks; i++)
        {
            number(&d, 2);
            u32 length = number(&d, 2);
            if (!length || length > 4096)
            {
                return 10;
            }
            take(&d, length);
        }
        if (a->hazards)
        {
            u32 count = number(&d, 4);
            if (count > 16)
            {
                return 10;
            }
            for (u32 i = 0; i < count; i++)
            {
                number(&d, 2);
                u32 length = number(&d, 2);
                if (!length || length > 4096)
                {
                    return 10;
                }
                take(&d, length);
            }
        }
        if (a->tiles)
        {
            u32 count = number(&d, 4);
            if (count > 16)
            {
                return 10;
            }
            take(&d, count * 6);
        }
        if (a->movement)
        {
            u32 pixels = number(&d, 4);
            if (!pixels || pixels > 16)
            {
                return 10;
            }
            take(&d, pixels * 46);
        }
    }
    if (a->clock)
    {
        a->clockDescriptor = d.p;
        if (number(&d, 4) != 1)
        {
            return 10;
        }
        u32 fields = number(&d, 4);
        if (!fields || fields > 32)
        {
            return 10;
        }
        for (u32 i = 0; i < fields; i++)
        {
            number(&d, 2);
            if (number(&d, 2) != 2)
            {
                return 10;
            }
            take(&d, 16);
        }
        if (a->tiles)
        {
            a->hintTarget = number(&d, 4);
            a->hintTextHash = take(&d, 32);
            a->hintText = take(&d, 128);
        }
        if (a->hazards)
        {
            a->listFinalizer = number(&d, 4);
            a->restartCommand = number(&d, 2);
            a->menuOffset = number(&d, 2);
            if (a->completion)
            {
                a->completionTarget = number(&d, 4);
            }
            if (!a->restartCommand)
            {
                return 10;
            }
        }
    }
#endif
    if (d.error || d.p != d.end)
    {
        return 10;
    }
    a->lifecycleState = 2;
    return 0;
}

static u32 application_invoke(NativeApplication *a)
{
    if ((a->lifecycleState != 2 && a->lifecycleState != 3) ||
        native_GetCurrentThreadId() != a->ownerThread || a->completed >= a->invocations)
    {
        return 60;
    }
    Runtime *r = &a->runtime;
    State *s = &a->state;
    State before = *s;
    Segment *task = find_segment(r, a->taskSelector);
    if (!task || task->size != 32 || task->bytes[0] != a->instance ||
        task->bytes[2] != a->showState ||
        (u16)(task->bytes[4] | ((u16)task->bytes[5] << 8)) != s->ds || task->bytes[16])
    {
        return 40;
    }
    for (u32 i = a->arguments; i > 0; i--)
    {
        push(r, s, (u16)(a->words[i * 2 - 2] | ((u16)a->words[i * 2 - 1] << 8)));
    }
    __win16_call_target(r, s, a->rootPointer, (u16)a->rootFar);
    if (r->error)
    {
        return 30;
    }
    s->sp = (u16)(s->sp + a->callerCleanup);
    if (s->sp != before.sp || s->ss != before.ss || s->ds != before.ds || s->cs != before.cs ||
        s->ip != before.ip || s->bp != before.bp || s->si != before.si || s->di != before.di)
    {
        return 40;
    }
    if (((u32)s->ax | ((u32)s->dx << 16)) != a->expected + a->completed * a->delta)
    {
        return 40;
    }
    a->completed++;
    return 0;
}

static u32 application_run(NativeApplication *a)
{
    u32 result = application_invoke(a);
    if (result)
    {
        return result;
    }
    NativeMessage message;
    zero(&message, sizeof(message));
    native_PeekMessageW(&message, 0, 0, 0, 0);        /* creates this UI thread's queue */
    a->timer = native_SetTimer(0, 0, a->interval, 0); /* no TIMERPROC/callback */
    if (!a->timer)
    {
        return 60;
    }
    a->lifecycleState = 3;
    for (;;)
    {
        int status = native_GetMessageW(&message, 0, 0, 0);
        if (status == -1)
        {
            return 60;
        }
        if (!status)
        {
            return a->completed == a->invocations && !message.wparam
                ? 0
                : 60;
        }
        if (native_GetCurrentThreadId() != a->ownerThread)
        {
            return 60;
        }
        /* Thread messages are infrastructure-only. No native pointers are converted to Win16 parameters. */
        if (!message.hwnd && message.message == 0x113 && message.wparam == a->timer)
        {
            result = application_invoke(a);
            if (result)
            {
                return result;
            }
            if (a->completed == a->invocations)
            {
                if (!native_KillTimer(0, a->timer))
                {
                    return 50;
                }
                a->timer = 0;
                native_PostQuitMessage(0);
            }
        }
    }
}

/* Automation can explicitly suppress modal error UI. Unknown runtime arguments fail closed. */

static int application_diagnostic_mode(void)
{
    const u16 *p = native_GetCommandLineW();
    if (!p)
    {
        return -1;
    }
    int quoted = *p == '"';
    if (quoted)
    {
        p++;
    }
    while (*p && (quoted
        ? *p != '"'
        : *p != ' ' && *p != '\t'))
    {
        p++;
    }
    if (quoted && *p)
    {
        p++;
    }
    while (*p == ' ' || *p == '\t')
    {
        p++;
    }
    if (!*p)
    {
        return 0;
    }
    const char *option = "--lifecycle-test";
    for (u32 i = 0; option[i]; i++)
    {
        if (*p++ != (u16)option[i])
        {
            return -1;
        }
    }
    while (*p == ' ' || *p == '\t')
    {
        p++;
    }
    return *p
        ? -1
        : 1;
}

static const char *application_detail(u32 result, u32 runtimeError)
{
    if (!result)
    {
        return "Completed supported original scene/input checks and released owned resources.";
    }
    if (result == 10)
    {
        return "Invalid application descriptor, code binding, memory layout or ABI.";
    }
    if (result == 20)
    {
        return "Native memory, graphics or runtime resource allocation failed.";
    }
    if (result == 30)
    {
        return runtimeError == 5 || runtimeError == 8
            ? "Unsupported runtime service or original routine reached a fatal boundary. "
              "See the translation manifest."
            : "Translated original code or a native compatibility service failed. See "
              "RuntimeError in diagnostics.";
    }
    if (result == 40)
    {
        return "Original result, pixel output, callback frame or input-state verification failed.";
    }
    if (result == 50)
    {
        return "Native resource cleanup or ownership validation failed.";
    }
    if (result == 60)
    {
        return "Window creation, callback lifetime, focus, owning-thread or message-loop failure.";
    }
    if (result == 70)
    {
        return "Invalid command line. Use --original <CHIPS.EXE>, optional --levels <CHIPS.DAT>, "
               "--diagnostics <new-file>, and gameplay --level 1 or 2.";
    }
    if (result == 80)
    {
        return "Original executable missing or unavailable. Supply --original with your CHIPS.EXE "
               "path.";
    }
    if (result == 81)
    {
        return "Original executable identity mismatch or read failure. Supply the verified "
               "CHIPS.EXE installation.";
    }
    if (result == 84)
    {
        return "Original CHIPS.DAT missing; supply --levels <CHIPS.DAT>.";
    }
    if (result == 85)
    {
        return "Original CHIPS.DAT size/read/hash mismatch.";
    }
    if (result == 82)
    {
        return "Required original bitmap resource is missing, invalid or has the wrong identity.";
    }
    if (result == 83)
    {
        return "Cannot create or write diagnostics. Choose a new writable file; existing files are "
               "never overwritten.";
    }
    return "Native application failed. See the exit code and translation manifest.";
}

static void application_error(u32 result, u32 runtimeError, int diagnostic, int logFailed)
{
    u16 text[1024];
    const char *detail = application_detail(result, runtimeError);
    u32 at = 0;
    for (u32 i = 0; detail[i] && at < 800; i++)
    {
        text[at++] = (u16)detail[i];
    }
    const char *prefix = "\r\nExit code: ";
    for (u32 i = 0; prefix[i]; i++)
    {
        text[at++] = (u16)prefix[i];
    }
    text[at++] = '0' + result / 100 % 10;
    text[at++] = '0' + result / 10 % 10;
    text[at++] = '0' + result % 10;
    if (logFailed)
    {
        const char *extra = "\r\nThe diagnostic file could not be written.";
        for (u32 i = 0; extra[i]; i++)
        {
            text[at++] = (u16)extra[i];
        }
    }
    text[at] = 0;
    native_OutputDebugStringW(text);
    u16 title[] = {'N', 'a', 't', 'i', 'v', 'e', ' ', 'a', 'p', 'p', 'l', 'i', 'c',
                   'a', 't', 'i', 'o', 'n', ' ', 'e', 'r', 'r', 'o', 'r', 0};
    if (!diagnostic)
    {
        native_MessageBoxW(0, text, title, 0x10);
    }
}
#ifdef NE_PAINT

static u32 application_log(NativeApplication *a, u32 result, u32 runtimeError, u32 cleanup,
                           u32 stage)
{
    if (!a->diagnosticsPath)
    {
        return 0;
    }
    char text[2048];
    u32 at = 0;
    const char *parts[] = {"Native application diagnostics v1\r\nStatus: ",
                           result
                               ? "failure\r\n"
                               : "success\r\n",
                           "Stage: ",
                           stage == 0
                               ? "arguments\r\n"
                               : stage == 1
                                       ? "startup\r\n"
                                       : stage == 2
                                           ? "message-loop\r\n"
                                           : "cleanup\r\n"};
    for (u32 p = 0; p < 4; p++)
    {
        for (u32 i = 0; parts[p][i]; i++)
        {
            text[at++] = parts[p][i];
        }
    }
    const char *labels[] = {"ExitCode: ", "RuntimeError: ", "CleanupCode: "};
    u32 values[] = {result, runtimeError, cleanup};
    for (u32 p = 0; p < 3; p++)
    {
        for (u32 i = 0; labels[p][i]; i++)
        {
            text[at++] = labels[p][i];
        }
        char digits[10];
        u32 n = 0, v = values[p];
        do
        {
            digits[n++] = '0' + v % 10;
            v /= 10;
        } while (v);
        while (n)
        {
            text[at++] = digits[--n];
        }
        text[at++] = '\r';
        text[at++] = '\n';
    }
#ifdef NE_SCENE
    if (a->clock)
    {
        const char *names[] = {"ClockDelivered", "ClockDeferred", "ClockPosted",
                               "ClockElapsedMs", "ClockCase", "HintCreates",
                               "HintDestroys", "HintPaints", "DeathDialogs",
                               "ListCleanup", "PrimaryResult", "ListCleanupError",
                               "OracleOffset", "OracleActual", "OracleExpected",
                               "PlacardDraws", "LastFailedService"};
        u32 values[] = {a->clockDelivered, a->clockDeferred, a->clockPosted,
                        a->clockElapsed, a->clockCase, a->hintCreates,
                        a->hintDestroys, a->hintPaints, a->deathDialogs,
                        a->listCleanup, a->primaryResult, a->listCleanupError,
                        a->oracleOffset, a->oracleActual, a->oracleExpected,
                        a->placardDraws, a->lastFailedService};
        for (u32 i = 0; i < 17; i++)
        {
            for (u32 j = 0; names[i][j]; j++)
            {
                text[at++] = names[i][j];
            }
            text[at++] = ':';
            text[at++] = ' ';
            char digits[10];
            u32 n = 0, v = values[i];
            do
            {
                digits[n++] = '0' + v % 10;
                v /= 10;
            } while (v);
            while (n)
            {
                text[at++] = digits[--n];
            }
            text[at++] = '\r';
            text[at++] = '\n';
        }
        if (a->completion)
        {
            const char *label = "CompletionDialogs: ";
            for (u32 j = 0; label[j]; j++)
            {
                text[at++] = label[j];
            }
            char digits[10];
            u32 n = 0, v = a->completionDialogs;
            do
            {
                digits[n++] = '0' + v % 10;
                v /= 10;
            } while (v);
            while (n)
            {
                text[at++] = digits[--n];
            }
            text[at++] = '\r';
            text[at++] = '\n';
        }
        if (a->clockCaptured)
        {
            const u8 *p = a->clockDescriptor + 8;
            for (u32 i = 0; i < get32(a->clockDescriptor + 4); i++, p += 20)
            {
                for (u32 j = 0; j < 16 && p[4 + j]; j++)
                {
                    text[at++] = (char)p[4 + j];
                }
                text[at++] = ':';
                text[at++] = ' ';
                char digits[10];
                u32 n = 0, v = a->clockValues[i];
                do
                {
                    digits[n++] = '0' + v % 10;
                    v /= 10;
                } while (v);
                while (n)
                {
                    text[at++] = digits[--n];
                }
                text[at++] = '\r';
                text[at++] = '\n';
            }
        }
    }
#endif
    const char *detail = application_detail(result, runtimeError);
    for (u32 i = 0; detail[i]; i++)
    {
        text[at++] = detail[i];
    }
    text[at++] = '\r';
    text[at++] = '\n';
    // CREATE_NEW protects original assets, generated binaries and existing logs from clobbering.
    void *file = native_CreateFileW(a->diagnosticsPath, 0x40000000, 1, 0, 1, 0x80, 0);
    if (file == (void *)-1)
    {
        return 83;
    }
    u32 written = 0;
    int ok = native_WriteFile(file, text, at, &written, 0);
    int closed = native_CloseHandle(file);
    return ok && written == at && closed
        ? 0
        : 83;
}
#endif

ABI NOINLINE u32 ne_application_entry(const u8 *data, const int *procedures, u32 length,
                                      u32 available)
{
    NativeApplication application, *a = &application;
    zero(a, sizeof(*a));
#ifdef NE_PAINT
    int diagnostic = paint_arguments(a);
#else
    int diagnostic = application_diagnostic_mode();
#endif
    u32 result = diagnostic < 0
        ? 70
        : 0;
#ifdef NE_SCENE
    if (!result && length >= 4 && get32(data) == 0x3647504e)
    {
        // A package selects only among complete, source-pinned launch profiles.
        // Both profiles bind the same original compiled bodies/procedure table.
        a->gameplay = 1;
        u32 requested = a->requestedLevel
            ? a->requestedLevel
            : 1;
        if (length < 12 || get32(data + 4) != 1 || get32(data + 8) != 2)
        {
            result = 10;
        }
        else
        {
            const u8 *p = data + 12, *end = data + length, *selected = 0;
            u32 selectedLength = 0;
            for (u32 level = 1; level <= 2; level++)
            {
                if ((uptr)(end - p) < 8)
                {
                    result = 10;
                    break;
                }
                u32 identity = get32(p), size = get32(p + 4);
                p += 8;
                if (identity != level || size < 16 || size > (uptr)(end - p) ||
                    get32(p) != 0x3541504e || get32(p + 4) != 11)
                {
                    result = 10;
                    break;
                }
                if (identity == requested)
                {
                    selected = p;
                    selectedLength = size;
                }
                p += size;
            }
            if (!result && (p != end || !selected))
            {
                result = 10;
            }
            if (!result)
            {
                data = selected;
                length = selectedLength;
            }
        }
    }
    else if (!result && a->requestedLevel)
    {
        result = 70;
    }
#endif
    if (!result)
    {
        result = application_open(a, data, procedures, length, available);
    }
#ifdef NE_WINDOW
    a->automation = diagnostic == 1;
    if (!result)
    {
        result = window_run(a);
    }
#else
    if (!result)
    {
        result = application_run(a);
    }
#endif
    u32 runtimeError = a->runtime.error;
    u32 stage = diagnostic < 0
        ? 0
        : a->lifecycleState == 3
                ? 2
                : 1;
#ifdef NE_SCENE
    a->primaryResult = result;
#endif
    u32 cleanup = application_close(a);
    if (cleanup)
    {
        result = cleanup;
    }
    if (application_close(a))
    {
        result = 50;
    }
    if (cleanup)
    {
        stage = 3;
    }
    int suppress = diagnostic == 1, logFailed = 0;
#ifdef NE_PAINT
    suppress = a->suppressDialog;
    logFailed = application_log(a, result, runtimeError, cleanup, stage) != 0;
    if (logFailed && !result)
    {
        result = 83;
    }
#endif
    if (result)
    {
        application_error(result, runtimeError, suppress, logFailed);
    }
    return result;
}
