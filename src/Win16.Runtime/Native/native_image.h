/* Shared bounded image-reader and native heap ownership primitives. No parser, CLR or CRT at runtime.
 * All embedded Win16 addresses remain selector:offset. Native code uses a separate rel32 table.
 */
extern __attribute__((visibility("hidden"))) ABI void *native_HeapCreate(u32, unsigned long long,
                                                                         unsigned long long);
extern __attribute__((visibility("hidden"))) ABI void *native_HeapAlloc(void *, u32,
                                                                        unsigned long long);
extern __attribute__((visibility("hidden"))) ABI int native_HeapFree(void *, u32, void *);
extern __attribute__((visibility("hidden"))) ABI int native_HeapDestroy(void *);
typedef struct HeapBlock
{
    struct HeapBlock *next;
    unsigned long long size;
} HeapBlock;
typedef struct
{
    void *heap;
    HeapBlock *blocks;
    unsigned long long bytes, maximum;
    u32 count, limit, error;
} HeapStorage;

static void *heap_alloc(void *context, u32 size)
{
    HeapStorage *h = context;
    if (h->count >= h->limit || size > h->maximum - h->bytes)
    {
        return 0;
    }
    HeapBlock *b = native_HeapAlloc(h->heap, 8, sizeof(*b) + size);
    if (!b)
    {
        return 0;
    }
    b->next = h->blocks;
    b->size = size;
    h->blocks = b;
    h->bytes += size;
    h->count++;
    return b + 1;
}

static void heap_release(void *context, void *pointer)
{
    HeapStorage *h = context;
    HeapBlock **p = &h->blocks;
    while (*p && (void *)(*p + 1) != pointer)
    {
        p = &(*p)->next;
    }
    if (!*p)
    {
        h->error = 1;
        return;
    }
    HeapBlock *b = *p;
    *p = b->next;
    h->bytes -= b->size;
    h->count--;
    zero(b + 1, (u32)b->size);
    if (!native_HeapFree(h->heap, 0, b))
    {
        h->error = 1;
    }
}
typedef struct
{
    const u8 *p, *end;
    u32 error;
} ImageReader;

static const u8 *take(ImageReader *d, u32 n)
{
    if (d->error || n > (unsigned long long)(d->end - d->p))
    {
        d->error = 1;
        return 0;
    }
    const u8 *p = d->p;
    d->p += n;
    return p;
}

static u32 number(ImageReader *d, u32 n)
{
    const u8 *p = take(d, n);
    u32 value = 0;
    if (p)
    {
        for (u32 i = 0; i < n; i++)
        {
            value |= (u32)p[i] << (i * 8);
        }
    }
    return value;
}

static unsigned long long wide(ImageReader *d)
{
    u32 lo = number(d, 4), hi = number(d, 4);
    return lo | ((unsigned long long)hi << 32);
}
#ifdef NE_WHOLE_TASK
#include "task_windows_files.h"
#include "task_windows_profiles.h"
#include "task_windows_system.h"
#include "task_windows_gui.h"
#include "package_trace.h"
#include "task_image.h"
#endif
