/* Shared freestanding Win16 runtime services for standalone PE payloads.
 * Rebuild with rebuild.py. No CLR, CRT, interpreter, host pointers as selectors, or OS API stubs.
 * ABI/layout matches MachineState and the generated read/write/call_service helper contracts.
 */
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
#include "api_bindings.h"
#define ABI __attribute__((ms_abi))
#define NOINLINE __attribute__((noinline))
typedef struct __attribute__((packed))
{
    u16 ax, bx, cx, dx, si, di, bp, sp, ip, cs, ds, es, ss, flags;
    u16 high[8];
} State;
_Static_assert(sizeof(State) == 44 && __builtin_offsetof(State, flags) == 26 &&
                   __builtin_offsetof(State, high) == 28,
               "MachineState ABI v2 mismatch");
#include "memory.h"
#ifndef NE_RUNTIME
#include "fixture_storage.h"
#endif

static u8 read8(Runtime *r, u16 sel, u16 off)
{
    Segment *s = segment(r, sel);
    if (!s)
    {
        return 0;
    }
    if (!(s->permissions & 1) || off >= s->size)
    {
        r->error = 3;
        return 0;
    }
    return s->bytes[off];
}

ABI NOINLINE u16 __win16_read_word(Runtime *r, u16 sel, u16 off)
{
    return read8(r, sel, off) | ((u16)read8(r, sel, (u16)(off + 1)) << 8);
}

ABI NOINLINE void __win16_write_word(Runtime *r, u16 sel, u16 off, u16 value)
{
    Segment *s = segment(r, sel);
    if (!s)
    {
        return;
    }
    u16 next = (u16)(off + 1);
    if (!writable(r, s, off, 2))
    {
        return;
    }
    if (off >= s->size || next >= s->size)
    {
        r->error = 4;
        return;
    }
    s->bytes[off] = (u8)value;
    s->bytes[next] = (u8)(value >> 8);
}

#ifdef NE_SCENE
static int window_stack_push_valid(Runtime *, State *);
#endif

static void push(Runtime *r, State *s, u16 value)
{
#ifdef NE_SCENE
    if (!window_stack_push_valid(r, s))
    {
        return;
    }
#endif
    s->sp = (u16)(s->sp - 2);
    __win16_write_word(r, s->ss, s->sp, value);
}

static u16 pop(Runtime *r, State *s)
{
    u16 v = __win16_read_word(r, s->ss, s->sp);
    s->sp = (u16)(s->sp + 2);
    return v;
}

static u16 arg(Runtime *r, State *s, u32 n)
{
    return __win16_read_word(r, s->ss, (u16)(s->sp + 4 + n * 2));
}
#ifdef NE_PAINT
static u16 paint_unlock(Runtime *, u16);
#endif
#ifdef NE_WINDOW
static void dispatch_window_default(Runtime *, State *);
static int window_import_target(Runtime *, State *, u16);
#endif
#ifdef CALLBACK_ACCEPTANCE
static void dispatch_defwindowproc(Runtime *r, State *s);
#endif
#ifdef GRAPHICS_ACCEPTANCE
static void dispatch_patblt(Runtime *r, State *s);
#endif

ABI NOINLINE void __win16_call_service(Runtime *r, State *s, u16 id)
{
    const ApiBinding *binding = 0;
    for (u32 i = 0; i < sizeof(api_bindings) / sizeof(api_bindings[0]); i++)
    {
        if (api_bindings[i].id == id)
        {
            binding = &api_bindings[i];
        }
    }
    if (!binding)
    {
        r->error = 5;
        return;
    }
    u16 oldCs = s->cs, oldIp = s->ip, bp = s->bp, ds = s->ds, si = s->si, di = s->di;
    push(r, s, oldCs);
    push(r, s, oldIp);
    s->cs = 0xF000;
    s->ip = binding->offset;
    u16 cleanup = binding->cleanup;
    if (id == SERVICE_GlobalUnlock)
    {
#ifdef NE_PAINT
        s->ax = arg(r, s, ARG_GlobalUnlock_handle) == 0x7101
            ? paint_unlock(r, 0x7101)
            : unlock_memory(r, arg(r, s, ARG_GlobalUnlock_handle));
#else
        s->ax = unlock_memory(r, arg(r, s, ARG_GlobalUnlock_handle));
#endif
    }
    else if (id == SERVICE_GlobalReAlloc)
    {
        u32 bytes = arg(r, s, ARG_GlobalReAlloc_bytes) |
            ((u32)arg(r, s, ARG_GlobalReAlloc_bytes + 1) << 16);
        s->ax = reallocate(r, arg(r, s, ARG_GlobalReAlloc_handle), bytes,
                           arg(r, s, ARG_GlobalReAlloc_flags));
    }
    else if (id == SERVICE_GlobalAlloc)
    {
        u32 bytes =
            arg(r, s, ARG_GlobalAlloc_bytes) | ((u32)arg(r, s, ARG_GlobalAlloc_bytes + 1) << 16);
        s->ax = allocate(r, bytes, arg(r, s, ARG_GlobalAlloc_flags));
    }
    else if (id == SERVICE_GlobalLock)
    {
        s->ax = 0;
        s->dx = lock_memory(r, arg(r, s, ARG_GlobalLock_handle));
    }
#ifdef GRAPHICS_ACCEPTANCE

    else if (id == SERVICE_PatBlt)
    {
        dispatch_patblt(r, s);
    }
#endif
#ifdef NE_WINDOW

    else if (id == SERVICE_DefWindowProc)
    {
        dispatch_window_default(r, s);
    }
#endif
#ifdef CALLBACK_ACCEPTANCE

    else if (id == SERVICE_DefWindowProc)
    {
        dispatch_defwindowproc(r, s);
    }
#endif
    else
    {
        r->error = 5;
    }
    s->ip = pop(r, s);
    s->cs = pop(r, s);
    s->sp = (u16)(s->sp + cleanup);
    s->bp = bp;
    s->ds = ds;
    s->si = si;
    s->di = di;
}
#include "instructions.h"
#include "program_control.h"
#include "local_memory.h"
#include "task_kernel.h"
#include "task_resources.h"
#include "task_files.h"
#include "task_profiles.h"
#include "task_system.h"
#include "task_instances.h"
#include "task_gui.h"
#include "task_ui.h"
#include "task_ui_structures.h"
#include "task_ui_resources.h"
#include "task_ui_windows.h"
#include "task_modules.h"
#include "task_lifecycle.h"
#include "task_wep.h"
#if defined(NE_WHOLE_TASK) || defined(NE_SERVICE_DISPATCH)
#include "task_import_dispatch.h"
#endif
