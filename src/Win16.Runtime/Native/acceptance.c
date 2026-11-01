/* Acceptance entry points; production builds compile runtime.c instead. */
#include "runtime_common.h"
#ifdef NE_RUNTIME
#include "native_image.h"
#ifndef NE_WHOLE_TASK
#include "../../../tests/Translator.TestSupport/Native/ne_entry.h"
#endif
#ifdef NE_APPLICATION
#include "../../../tests/Translator.TestSupport/Native/ne_application.h"
#endif
#else
#include "marshalling.h"
#include "callbacks.h"
extern __attribute__((visibility("hidden"))) ABI u16 translated_FindMonster(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI u16 translated_GrowArray(Runtime *, State *);
extern __attribute__((visibility("hidden"))) ABI void translated_TurnLeft(short, short, short *,
                                                                          short *);

static void init(Runtime *r, State *s, FixtureStorage *storage)
{
    init_storage(storage);
    init_runtime(r, storage->bytes, fixture_alloc, fixture_release, 4096, 65535,
                 64ull * 1024 * 1024);
    zero(s, sizeof(*s));
    map(r, 0x1000, 65536);
    map(r, 0x2000, 65536);
    map(r, 0x3000, 65536);
    s->ds = 0x1000;
    s->ss = 0x2000;
    s->cs = 0x4000;
    s->sp = 0xF000;
    s->bp = 0x1234;
    s->si = 0xAAAA;
    s->di = 0xBBBB;
    s->ip = 0x0050;
    s->flags = 0x401;
}

static u16 call(Runtime *r, State *s, int grow)
{
    u16 cs = s->cs, ip = s->ip;
    push(r, s, cs);
    push(r, s, ip);
    s->cs = 0x4000;
    s->ip = grow
        ? 0x200
        : 0x100;
    u16 cleanup = grow
        ? translated_GrowArray(r, s)
        : translated_FindMonster(r, s);
    s->ip = pop(r, s);
    s->cs = pop(r, s);
    s->sp = (u16)(s->sp + cleanup);
    return cleanup;
}

static void word(Runtime *r, u16 off, u16 value)
{
    __win16_write_word(r, 0x1000, off, value);
}

static u16 data(Runtime *r, u16 off)
{
    return __win16_read_word(r, 0x1000, off);
}

static void growargs(Runtime *r, State *s, u16 count, u16 size)
{
    push(r, s, size);
    push(r, s, count);
    push(r, s, 0x106);
    push(r, s, 0x102);
    push(r, s, 0x100);
}

static int balanced(State *s)
{
    return s->sp == 0xF000 && s->bp == 0x1234 && s->si == 0xAAAA && s->di == 0xBBBB &&
        s->ds == 0x1000 && s->cs == 0x4000 && s->ip == 0x50 && (s->flags & 0x400);
}

#include "program_control_contract.h"
#include "program_memory_contract.h"
#include "task_kernel_contract.h"
#include "task_resources_contract.h"
#include "task_files_contract.h"
#include "task_profiles_contract.h"
#include "task_system_contract.h"
#include "task_instances_contract.h"
#include "task_gui_contract.h"
#include "task_ui_structures_contract.h"
#include "task_ui_windows_contract.h"
static int memory_contract(FixtureStorage *storage);
static int instruction_contract(FixtureStorage *storage);
/* Returns a stable nonzero case ID on failure, zero only when every actual target succeeds. */

ABI NOINLINE int acceptance(void)
{
    FixtureStorage storage;
    Runtime runtime;
    Runtime *r = &runtime;
    State state;
    State *s = &state;
    /* TurnLeft: 49 signed boundary/direction pairs, including the no-write cases. */
    for (int i = 0; i < 7; i++)
    {
        for (int j = 0; j < 7; j++)
        {
            short x = i == 0
                ? -32768
                : i == 6
                        ? 32767
                        : (short)(i - 3),
                  y = j == 0
                      ? -32768
                      : j == 6
                        ? 32767
                        : (short)(j - 3);
            short ox = 0x5555, oy = 0x5555, ex = 0x5555, ey = 0x5555, d = (short)(2 * x + y);
            if (d == -2)
            {
                ex = 0;
                ey = 1;
            }
            else if (d == -1)
            {
                ex = -1;
                ey = 0;
            }
            else if (d == 1)
            {
                ex = 1;
                ey = 0;
            }
            else if (d == 2)
            {
                ex = 0;
                ey = -1;
            }
            translated_TurnLeft(x, y, &ox, &oy);
            if (ox != ex || oy != ey)
            {
                return 10;
            }
        }
    }
    init(r, s, &storage);
    word(r, 0x1680, 0x3000);
    word(r, 0x3928, 3);
    word(r, 0x392E, 0xFFF8);
    word(r, 0x3930, 0x3000);
    for (int i = 0; i < 3; i++)
    {
        __win16_write_word(r, 0x3000, (u16)(0xFFF9 + i * 11), (u16)(i == 2
            ? 0x8000
            : i + 1));
        __win16_write_word(r, 0x3000, (u16)(0xFFFB + i * 11), (u16)(i == 2
            ? 0x7FFF
            : i + 7));
    }
    for (int i = 0; i < 4; i++)
    {
        push(r, s, (u16)(i == 2
            ? 0x7FFF
            : i + 7));
        push(r, s, (u16)(i == 2
            ? 0x8000
            : i + 1));
        if (call(r, s, 0) != 4 || s->ax != (u16)(i == 3
            ? -1
            : i) || !balanced(s) || r->error)
        {
            return 20 + i;
        }
    }
    word(r, 0x3928, 0);
    word(r, 0x392E, 0);
    word(r, 0x3930, 0);
    push(r, s, 1);
    push(r, s, 1);
    call(r, s, 0);
    if (s->ax != 65535 || r->error || !balanced(s))
    {
        return 24;
    }
    destroy_runtime(r);
    /* Allocation success, reallocation success, allocation failure, reallocation failure. */
    for (int mode = 0; mode < 4; mode++)
    {
        init(r, s, &storage);
        u16 h = 0, oldSelector = 0;
        if (mode == 1 || mode == 3)
        {
            h = allocate(r, 8, 0x42);
            oldSelector = allocation(r, h)->selector;
            __win16_write_word(r, oldSelector, 0, 0xA55A);
        }
        word(r, 0x100, h);
        word(r, 0x106, mode == 0 || mode == 2
            ? 4
            : 2);
        word(r, 0x102, 0);
        word(r, 0x104, oldSelector);
        r->failAlloc = mode == 2;
        r->failRealloc = mode == 3;
        growargs(r, s, mode == 0 || mode == 2
            ? 3
            : 2, mode == 0 || mode == 2
            ? 2
            : 3);
        if (call(r, s, 1) != 10 || r->error || !balanced(s) || s->ax != (u16)(mode < 2
            ? 1
            : 0))
        {
            return 30 + mode;
        }
        u16 handle = data(r, 0x100), selector = data(r, 0x104), length = data(r, 0x106);
        if (data(r, 0x102) != 0)
        {
            return 34;
        }
        if (mode == 0 &&
            (!handle || selector == handle || segment(r, selector)->size != 6 || length != 7))
        {
            return 35;
        }
        if (mode == 1 &&
            (handle != h || selector == oldSelector || segment(r, selector)->size != 12 ||
             length != 4 || __win16_read_word(r, selector, 0) != 0xA55A))
        {
            return 36;
        }
        if (mode == 2 && (handle || selector || length != 4))
        {
            return 37;
        }
        if (mode == 3 && (handle != h || selector != oldSelector || length != 2 || __win16_read_word(r, selector, 0) != 0xA55A))
        {
            return 38;
        }
        destroy_runtime(r);
    }
    init(r, s, &storage);
    r->maxAllocations = 32;
    for (u32 i = 0; i < 33; i++)
    {
        word(r, 0x100, 0);
        word(r, 0x106, 4);
        word(r, 0x102, 0);
        word(r, 0x104, 0);
        growargs(r, s, 3, 2);
        if (call(r, s, 1) != 10 || !balanced(s) || r->error || s->ax != (i < 32
            ? 1
            : 0) ||
            data(r, 0x106) != (i < 32
                ? 7
                : 4))
        {
            return 39;
        }
    }
    if (r->allocated != 32 || r->bytes != 192)
    {
        return 40;
    }
    destroy_runtime(r);
    int result = memory_contract(&storage);
    if (!result)
    {
        result = instruction_contract(&storage);
    }
    if (result)
    {
        return result;
    }
    result = task_ui_windows_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_ui_structures_contract(&storage);
    if (result)
    {
        return 150 + result;
    }
    result = task_gui_contract(&storage);
    if (result)
    {
        return 140 + result;
    }
    result = task_instances_contract(&storage);
    if (result)
    {
        return 70 + result;
    }
    result = task_system_contract(&storage);
    if (result)
    {
        return 100 + result;
    }
    result = task_profiles_contract(&storage);
    if (result)
    {
        return 90 + result;
    }
    result = task_files_contract(&storage);
    if (result)
    {
        return 70 + result;
    }
    result = task_resources_contract(&storage);
    if (result)
    {
        return 240 + result;
    }
    result = task_kernel_contract(&storage);
    if (result)
    {
        return 210 + result;
    }
    result = program_memory_contract(&storage);
    if (result)
    {
        return 180 + result;
    }
    result = program_control_contract(&storage);
    return result
        ? 160 + result
        : 0;
}

static void *fail_storage(void *context, u32 bytes)
{
    (void)context;
    (void)bytes;
    return (void *)0;
}

/* Contract tests run in the same freestanding payload as translated GrowArray. */

static NOINLINE int memory_contract(FixtureStorage *storage)
{
    Runtime runtime;
    Runtime *r = &runtime;
    State state;
    State *s = &state;
    init(r, s, storage);
    u16 handles[32];
    for (u32 i = 0; i < 32; i++)
    {
        handles[i] = allocate(r, 16, 0x42);
        if (!handles[i])
        {
            return 50;
        }
    }
    if (r->allocated != 32 || r->count != 35 || r->bytes != 512)
    {
        return 51;
    }
    u16 h = handles[0], sel = lock_memory(r, h);
    if (!sel || sel == h || lock_memory(r, h) != sel || allocation(r, h)->locks != 2)
    {
        return 52;
    }
    __win16_write_word(r, sel, 0, 0xA55A);
    if (free_memory(r, h) || !unlock_memory(r, h) || unlock_memory(r, h) || unlock_memory(r, h))
    {
        return 53;
    }
    if (reallocate(r, h, 32, 2) != h || allocation(r, h)->selector == sel || find_segment(r, sel))
    {
        return 54;
    }
    sel = lock_memory(r, h);
    if (__win16_read_word(r, sel, 0) != 0xA55A || __win16_read_word(r, sel, 16))
    {
        return 55;
    }
    if (reallocate(r, h, 40, 2) != h || allocation(r, h)->selector != sel ||
        allocation(r, h)->locks != 1)
    {
        return 56;
    }
    r->failRealloc = 1;
    if (reallocate(r, h, 48, 2) || allocation(r, h)->size != 40 ||
        __win16_read_word(r, sel, 0) != 0xA55A)
    {
        return 57;
    }
    if (reallocate(r, h, 0, 2))
    {
        return 58;
    }
    unlock_memory(r, h);
    if (reallocate(r, h, 0, 2) != h || lock_memory(r, h) || find_segment(r, sel))
    {
        return 59;
    }
    if (reallocate(r, h, 4, 2) != h || !lock_memory(r, h))
    {
        return 60;
    }
    unlock_memory(r, h);
    if (!free_memory(r, h) || free_memory(r, h) || lock_memory(r, h) || unlock_memory(r, h))
    {
        return 61;
    }
    if (allocate(r, 65537, 0x42) || allocate(r, 1, 0) || allocate(r, 1, 0x102) ||
        reallocate(r, 0xFFFF, 8, 2))
    {
        return 62;
    }
    r->maxAllocations = r->allocated;
    if (allocate(r, 1, 0x42))
    {
        return 63;
    }
    r->maxAllocations = 4096;
    r->maxBytes = r->bytes;
    if (allocate(r, 1, 0x42) || reallocate(r, handles[1], 17, 2))
    {
        return 64;
    }
    r->maxBytes = 64ull * 1024 * 1024;
    u16 zeroHandle = allocate(r, 0, 0x42);
    if (!zeroHandle || lock_memory(r, zeroHandle) || !free_memory(r, zeroHandle))
    {
        return 65;
    }
    /* Repeated allocation/free must reclaim both table and byte storage. */
    for (u32 i = 0; i < 128; i++)
    {
        u16 t = allocate(r, 4096, 0x42);
        if (!t || !free_memory(r, t))
        {
            return 66;
        }
    }
    u16 big = allocate(r, 65536, 0x42);
    sel = lock_memory(r, big);
    if (!sel)
    {
        return 67;
    }
    __win16_write_word(r, sel, 65535, 0x1234);
    if (__win16_read_word(r, sel, 65535) != 0x1234)
    {
        return 68;
    }
    /* Backend allocator failures must not corrupt the old mapping or handle state. */
    AllocateStorage saved = r->alloc;
    r->alloc = fail_storage;
    u32 allocated = r->allocated;
    unsigned long long used = r->bytes;
    if (allocate(r, 1, 0x42) || reallocate(r, handles[1], 32, 2) || r->allocated != allocated ||
        r->bytes != used || allocation(r, handles[1])->size != 16)
    {
        return 72;
    }
    r->alloc = saved;
    r->maxSegments = r->count;
    if (allocate(r, 1, 0x42))
    {
        return 73;
    }
    r->maxSegments = 65535;
    allocation(r, handles[1])->locks = 65535;
    if (lock_memory(r, handles[1]) || allocation(r, handles[1])->locks != 65535)
    {
        return 74;
    }
    allocation(r, handles[1])->locks = 0;
    /* Exhaust the real bounded fixture allocator, then reclaim it. */
    while (allocate(r, 65536, 0x42))
    {
    }
    allocated = r->allocated;
    used = r->bytes;
    if (allocate(r, 65536, 0x42) || r->allocated != allocated || r->bytes != used)
    {
        return 75;
    }
    destroy_runtime(r);
    if (r->count || r->allocated || r->bytes || r->segments || r->handles)
    {
        return 69;
    }
    Block *block = (Block *)storage->bytes;
    if (!block->available || block->next || block->size != sizeof(storage->bytes) - sizeof(Block))
    {
        return 70;
    }
    return r->error
        ? 71
        : 0;
}

/* Freestanding parity checks for new helper contracts. The call target is real translated assembly.
 */

static NOINLINE int instruction_contract(FixtureStorage *storage)
{
    Runtime runtime;
    Runtime *r = &runtime;
    State state;
    State *s = &state;
    init(r, s, storage);
    __win16_write_dword(r, 0x1000, 65534, 0x12345678);
    if (__win16_read_dword(r, 0x1000, 65534) != 0x12345678)
    {
        return 80;
    }
    __win16_write_byte(r, 0x1000, 0x10, 0xAB);
    if (__win16_read_byte(r, 0x1000, 0x10) != 0xAB)
    {
        return 81;
    }
    s->flags &= ~0x400;
    s->es = 0x3000;
    s->si = 65534;
    s->di = 65534;
    s->cx = 2;
    __win16_string(r, s, 3, 1);
    if (s->cx || s->si != 2 || s->di != 2 || __win16_read_dword(r, 0x3000, 65534) != 0x12345678)
    {
        return 82;
    }
    s->flags |= 0x400;
    s->si = 65534;
    s->cx = 1;
    __win16_string(r, s, 1, 1);
    if (s->ax != 0x5678 || s->si != 65532)
    {
        return 83;
    }
    s->flags &= ~0x400;
    s->di = 0x200;
    s->cx = 3;
    s->ax = 7;
    __win16_string(r, s, 4, 1);
    s->di = 0x200;
    s->cx = 3;
    __win16_string(r, s, 5, 3);
    if (s->cx != 2 || s->di != 0x201 || !(s->flags & 64))
    {
        return 84;
    }
    /* CMPSW: two equal words, then a signed-overflow mismatch across offset wrap. */
    __win16_write_word(r, 0x1000, 0xFFFF, 0x1234);
    __win16_write_word(r, 0x1000, 1, 0x8000);
    __win16_write_word(r, 0x3000, 0xFFFF, 0x1234);
    __win16_write_word(r, 0x3000, 1, 1);
    s->si = s->di = 0xFFFF;
    s->cx = 3;
    s->flags = 0x202;
    s->ax = 0x5678;
    __win16_string(r, s, 7, 2);
    if (r->error || s->cx != 1 || s->si != 3 || s->di != 3 || s->ax != 0x5678 ||
        (s->flags & 0x8D5) != 0x814)
    {
        return 150;
    }
    /* REPNE CMPSB must stop on equality, including backwards index wrap. */
    __win16_write_byte(r, 0x1000, 0, 1);
    __win16_write_byte(r, 0x3000, 0, 2);
    __win16_write_byte(r, 0x1000, 0xFFFF, 3);
    __win16_write_byte(r, 0x3000, 0xFFFF, 3);
    s->si = s->di = 0;
    s->cx = 3;
    s->flags = 0x602;
    __win16_string(r, s, 6, 3);
    if (r->error || s->cx != 1 || s->si != 0xFFFE || s->di != 0xFFFE || (s->flags & 0x8D5) != 0x44)
    {
        return 151;
    }
    s->ds = 0;
    s->es = 0;
    s->cx = 0;
    s->flags = 0x8D7;
    __win16_string(r, s, 7, 2);
    if (r->error || s->flags != 0x8D7 || s->si != 0xFFFE || s->di != 0xFFFE)
    {
        return 152;
    }
    s->ds = 0x1000;
    s->es = 0x3000;
    s->ax = 17;
    s->dx = 0;
    s->bx = 3;
    __win16_divide(r, s, 3, 16);
    if (s->ax != 5 || s->dx != 2)
    {
        return 85;
    }
    set_dreg(s, 0, 0xFFFFFFFF);
    set_dreg(s, 3, 0);
    __win16_divide(r, s, 2, 32);
    if (dreg(s, 0) != 0x7FFFFFFF || dreg(s, 3) != 1)
    {
        return 86;
    }
    s->ax = 0xFFFF;
    s->dx = 0xFFFF;
    __win16_divide(r, s, 1, 16);
    if (r->error != 7 || s->ax != 65535 || s->dx != 65535)
    {
        return 87;
    }
    r->error = 0;
    /* Imported procedure addresses use the registry, not a native function pointer. */
    push(r, s, 0x42);
    push(r, s, 0);
    push(r, s, 8);
    __win16_call_target(r, s, 0xF0000010, 1);
    u16 handle = s->ax;
    if (!handle || allocation(r, handle)->size != 8)
    {
        return 88;
    }
    CodeBinding binding = {0, 0x4000, 0x100, 1, translated_FindMonster};
    r->code = &binding;
    word(r, 0x1680, 0x3000);
    word(r, 0x3928, 0);
    push(r, s, 1);
    push(r, s, 1);
    __win16_call_target(r, s, 0x40000100, 1);
    if (s->ax != 65535 || s->sp != 0xF000 || r->error)
    {
        return 89;
    }
    __win16_call_target(r, s, 0x40009999, 1);
    if (r->error != 8 || s->sp != 0xF000)
    {
        return 90;
    }
    r->error = 0;
    Rect16 rect = {-1, -32768, 32767, 2}, output;
    if (!write_rect(r, 0x10000100, &rect) || !read_rect(r, 0x10000100, &output) ||
        output.left != -1 || output.top != -32768 || output.right != 32767)
    {
        return 91;
    }
    Point16 point = {-3, 4}, pointOut;
    if (!write_point(r, 0x10000200, &point) || !read_point(r, 0x10000200, &pointOut) ||
        pointOut.x != -3 || pointOut.y != 4)
    {
        return 92;
    }
    u8 ansi[] = {0x80, 0xE9, 0};
    copy_out(r, 0x10000300, ansi, 3);
    u8 *copy = copy_ansi(r, 0x10000300, 3);
    if (!copy || copy[0] != 0x80 || copy[1] != 0xE9 || copy[2])
    {
        return 93;
    }
    r->release(r->storage, copy);
    if (api_buffer(r, 0x1000FFFF, 2, 1) || r->error != 9)
    {
        return 94;
    }
    r->error = 0;
    if (copy_ansi(r, 0x10000300, 2) || r->error != 9)
    {
        return 95;
    }
    r->error = 0;
    HandleTable table = {r, 0, 1, 0};
    u16 id = add_handle(&table, 0x1234567890ull);
    if (!id || get_handle(&table, id) != 0x1234567890ull || !remove_handle(&table, id) ||
        remove_handle(&table, id))
    {
        return 96;
    }
    if (get_handle(&table, id) || r->error != 10)
    {
        return 97;
    }
    r->error = 0;
    if (!add_handle(&table, 7))
    {
        return 98;
    }
    destroy_handles(&table);
    if (table.entries)
    {
        return 99;
    }
    /* Callback target is the real generated FindMonster, using a private callback stack. */
    u16 callbackWords[] = {1, 1};
    CallbackFrame frame = {callbackWords, 2, 0};
    State saved = *s;
    u32 mappings = r->count;
    if (__win16_callback_invoke(r, s, 0x40000100, &frame) != 65535 || r->error ||
        r->count != mappings || r->callbackDepth)
    {
        return 130;
    }
    for (u32 i = 0; i < sizeof(State); i++)
    {
        if (((u8 *)s)[i] != ((u8 *)&saved)[i])
        {
            return 131;
        }
    }
    if (__win16_callback_invoke(r, s, 0x40009999, &frame) || r->error != 11)
    {
        return 132;
    }
    r->error = 0;
    for (u32 i = 0; i < sizeof(State); i++)
    {
        if (((u8 *)s)[i] != ((u8 *)&saved)[i])
        {
            return 133;
        }
    }
    r->callbackDepth = 8;
    if (__win16_callback_invoke(r, s, 0x40000100, &frame) || r->error != 12 || r->count != mappings)
    {
        return 134;
    }
    r->callbackDepth = 0;
    r->error = 0;
    u16 mismatchWords[] = {1, 1, 1};
    CallbackFrame mismatch = {mismatchWords, 3, 0};
    if (__win16_callback_invoke(r, s, 0x40000100, &mismatch) || r->error != 13 ||
        r->count != mappings)
    {
        return 135;
    }
    r->error = 0;
    for (u32 i = 0; i < sizeof(State); i++)
    {
        if (((u8 *)s)[i] != ((u8 *)&saved)[i])
        {
            return 136;
        }
    }
    destroy_runtime(r);
    return r->error
        ? 100
        : 0;
}

#ifdef GRAPHICS_ACCEPTANCE
#include "graphics.h"
#endif

#ifdef CALLBACK_ACCEPTANCE
#include "callbacks_windows.h"
#endif

#endif /* NE_RUNTIME */
