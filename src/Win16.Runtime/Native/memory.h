/* Freestanding movable-memory runtime. Storage is supplied by an allocator, not a fixed slot array.
 * Callers serialize access. IDs are never recycled during a runtime lifetime.
 */
typedef void *(*AllocateStorage)(void *, u32);
typedef void (*ReleaseStorage)(void *, void *);
typedef struct SegmentBacking
{
    u32 references, size, executable;
    u8 bytes[];
} SegmentBacking;
typedef struct Segment
{
    struct Segment *next;
    u16 selector, permissions;
    u32 size;
    u8 *bytes;
    SegmentBacking *backing;
    u32 origin, limit, alias, locks;
} Segment;
typedef struct Allocation
{
    struct Allocation *next;
    u16 handle, selector, locks, fixed;
    u32 size;
} Allocation;
typedef struct Runtime Runtime;
typedef ABI u16 (*TranslatedEntry)(Runtime *, State *);
typedef struct CodeBinding
{
    struct CodeBinding *next;
    u16 segment, offset, farCall;
    TranslatedEntry entry;
} CodeBinding;
struct Runtime
{
    u32 error, count, allocated, callbackDepth, nextHandle, nextSelector, failAlloc, failRealloc;
    u32 maxAllocations, maxSegments;
    unsigned long long maxBytes, bytes;
    Segment *segments;
    Allocation *handles;
    CodeBinding *code;
    void *user;
    void *gdi;
    void *storage;
    AllocateStorage alloc;
    ReleaseStorage release;
    struct ProgramImage *program;
    u32 wholeMemory, codeWriteCount;
    u16 codeWriteSelector, codeWriteOffset;
    u32 codeWriteBytes;
    struct LocalHeap *local;
    u16 errorMode, environmentSelector;
    struct TaskResources *resources;
    struct TaskFiles *files;
    u32 ownsFiles;
    struct TaskProfiles *profiles;
    u32 ownsProfiles;
    struct TaskSystem *system;
    u32 ownsSystem;
    struct InstanceThunk *instances;
    u32 nextInstanceThunk, instanceCount;
    struct TaskGui *gui;
    u32 ownsGui;
    struct TaskWep *wep;
    struct TaskModules *modules;
    struct TaskLifecycle *lifecycle;
    u32 terminated, exitCode, diagnostic;
    void (*observe)(Runtime *, State *, u32, u32);
    void *observation;
};

static void zero(void *data, u32 count)
{
    volatile u8 *p = data;
    while (count--)
    {
        *p++ = 0;
    }
}

static Segment *find_segment(Runtime *r, u16 selector)
{
    for (Segment *s = r->segments; s; s = s->next)
    {
        if (s->selector == selector)
        {
            return s;
        }
    }
    return (Segment *)0;
}

static Segment *segment(Runtime *r, u16 selector)
{
    Segment *s = find_segment(r, selector);
    if (!s)
    {
        r->error = 1;
    }
    return s;
}

static Segment *new_segment(Runtime *r, u16 selector, u32 size)
{
    Segment *s = r->alloc(r->storage, (u32)sizeof(Segment));
    if (!s)
    {
        return (Segment *)0;
    }
    SegmentBacking *b = r->alloc(r->storage, (u32)sizeof(SegmentBacking) + size);
    if (!b)
    {
        r->release(r->storage, s);
        return (Segment *)0;
    }
    b->references = 1;
    b->size = size;
    b->executable = 0;
    s->backing = b;
    s->bytes = b->bytes;
    s->origin = 0;
    s->limit = 65536;
    s->alias = 0;
    s->locks = 0;
    s->selector = selector;
    s->permissions = 3;
    s->size = size;
    zero(s->bytes, size);
    return s;
}

static void attach(Runtime *r, Segment *s)
{
    s->next = r->segments;
    r->segments = s;
    r->count++;
    if (r->observe)
    {
        r->observe(r, 0, 8, s->selector);
    }
}

static Segment *map(Runtime *r, u16 selector, u32 size)
{
    if (!size || size > 65536 || r->count >= r->maxSegments || find_segment(r, selector))
    {
        r->error = 2;
        return (Segment *)0;
    }
    Segment *s = new_segment(r, selector, size);
    if (!s)
    {
        r->error = 2;
        return s;
    }
    attach(r, s);
    return s;
}

static void release_segment(Runtime *r, Segment *s)
{
    if (!--s->backing->references)
    {
        r->release(r->storage, s->backing);
    }
    r->release(r->storage, s);
}

static void unmap(Runtime *r, u16 selector)
{
    Segment **p = &r->segments;
    while (*p)
    {
        Segment *s = *p;
        if (s->selector == selector)
        {
            *p = s->next;
            release_segment(r, s);
            r->count--;
            return;
        }
        p = &s->next;
    }
}

/* Writes are checked against storage identity, including every selector alias. */

static int writable(Runtime *r, Segment *s, u16 offset, u32 bytes)
{
    if (!s)
    {
        return 0;
    }
    for (Segment *v = r->segments; v; v = v->next)
    {
        if (v->backing == s->backing && (v->permissions & 4))
        {
            s->backing->executable = 1;
        }
    }
    if (s->backing->executable || (s->permissions & 4))
    {
        if (r->codeWriteCount != 0xffffffff)
        {
            r->codeWriteCount++;
        }
        r->codeWriteSelector = s->selector;
        r->codeWriteOffset = offset;
        r->codeWriteBytes = bytes;
        r->error = 15;
        return 0;
    }
    if (!(s->permissions & 2))
    {
        r->error = 4;
        return 0;
    }
    return 1;
}

static Segment *alias_segment(Runtime *r, u16 selector, u16 source, u32 offset, u32 size,
                              u16 permissions)
{
    Segment *s = segment(r, source);
    if (!s || !selector || selector == 0xf000 || selector == 0xfff8 || find_segment(r, selector) ||
        r->count >= r->maxSegments || offset >= s->size || size > s->size - offset ||
        (permissions & ~s->permissions) || (permissions & 6) == 6)
    {
        r->error = 2;
        return 0;
    }
    Segment *a = r->alloc(r->storage, sizeof(Segment));
    if (!a)
    {
        r->error = 2;
        return 0;
    }
    *a = *s;
    a->selector = selector;
    a->permissions = permissions;
    a->origin += offset;
    a->limit = size
        ? size
        : (offset
                ? s->size - offset
                : s->limit);
    a->size = size
        ? size
        : s->size - offset;
    a->bytes += offset;
    a->alias = 1;
    if (s->permissions & 4)
    {
        s->backing->executable = 1;
    }
    a->backing->references++;
    attach(r, a);
    if (selector >= r->nextSelector)
    {
        r->nextSelector = (u32)selector + 1;
    }
    return a;
}

static void invalidate_aliases(Runtime *r, Segment *s)
{
    Segment *v = r->segments;
    while (v)
    {
        Segment *next = v->next;
        if (v->alias && v->backing == s->backing)
        {
            unmap(r, v->selector);
        }
        v = next;
    }
}
/* Commit after successful allocation/copy. All aliases retain shared selector views. */

static void replace_backing(Runtime *r, Segment *s, Segment *replacement)
{
    SegmentBacking *old = s->backing, *b = replacement->backing;
    u32 references = old->references;
    b->references = references;
    old->references = 1;
    for (Segment *v = r->segments; v; v = v->next)
    {
        if (v->backing == old)
        {
            v->backing = b;
            v->bytes = b->bytes + (v->origin < b->size
                ? v->origin
                : b->size);
            v->size = v->origin >= b->size
                ? 0
                : b->size - v->origin;
            if (v->size > v->limit)
            {
                v->size = v->limit;
            }
            if (r->observe)
            {
                r->observe(r, 0, 8, v->selector);
            }
        }
    }
    replacement->backing = old;
    release_segment(r, replacement);
}

static int resize_data(Runtime *r, u16 selector, u32 size)
{
    Segment *s = segment(r, selector);
    if (!s || s->alias || !size || size > 65536 || !writable(r, s, 0, size))
    {
        return 0;
    }
    Segment *n = new_segment(r, selector, size);
    if (!n)
    {
        return 0;
    }
    for (u32 i = 0; i < size && i < s->size; i++)
    {
        n->bytes[i] = s->bytes[i];
    }
    replace_backing(r, s, n);
    return 1;
}

static Allocation *allocation(Runtime *r, u16 handle)
{
    for (Allocation *a = r->handles; a; a = a->next)
    {
        if (a->handle == handle)
        {
            return a;
        }
    }
    return (Allocation *)0;
}

static u16 next_selector(Runtime *r)
{
    while (r->nextSelector <= 65535)
    {
        u16 s = (u16)r->nextSelector++;
        if (s != 0xF000 && s != 0xF100 && s != 0xF200 && s != 0xFFF8 && !find_segment(r, s))
        {
            return s;
        }
    }
    return 0;
}

static int supported_flags(u16 flags)
{
    return (flags & 2) && !(flags & ~0x42);
}

static u16 allocate(Runtime *r, u32 bytes, u16 flags)
{
    if (r->wholeMemory && !(flags & 2) && !bytes)
    {
        bytes = 1;
    }
    u32 fail = r->failAlloc;
    r->failAlloc = 0;
    if (fail || !(r->wholeMemory
        ? !(flags & ~0x62)
        : supported_flags(flags)) || bytes > 65536 ||
        r->allocated >= r->maxAllocations || r->bytes + bytes > r->maxBytes ||
        ((!r->wholeMemory || (flags & 2)) && r->nextHandle > 0x3FFF))
    {
        return 0;
    }
    if (bytes && r->count >= r->maxSegments)
    {
        return 0;
    }
    u16 sel = bytes
        ? next_selector(r)
        : 0;
    if (bytes && !sel)
    {
        return 0;
    }
    Allocation *a = r->alloc(r->storage, sizeof(Allocation));
    if (!a)
    {
        return 0;
    }
    Segment *s = bytes
        ? new_segment(r, sel, bytes)
        : (Segment *)0;
    if (bytes && !s)
    {
        r->release(r->storage, a);
        return 0;
    }
    if (s)
    {
        attach(r, s);
    }
    a->fixed = r->wholeMemory && !(flags & 2);
    a->handle = a->fixed
        ? sel
        : (u16)r->nextHandle++;
    a->selector = sel;
    a->locks = 0;
    a->size = bytes;
    a->next = r->handles;
    r->handles = a;
    r->allocated++;
    r->bytes += bytes;
    return a->handle;
}

static u16 reallocate(Runtime *r, u16 handle, u32 bytes, u16 flags)
{
    u32 fail = r->failRealloc;
    r->failRealloc = 0;
    Allocation *a = allocation(r, handle);
    if (a && a->fixed && !bytes)
    {
        bytes = 1;
    }
    if (fail || !(r->wholeMemory
        ? !(flags & ~0x62)
        : supported_flags(flags)) || bytes > 65536 ||
        !a || r->bytes - a->size + bytes > r->maxBytes || (!bytes && a->locks))
    {
        return 0;
    }
    if (bytes && !a->size && r->count >= r->maxSegments)
    {
        return 0;
    }
    u16 sel = bytes
        ? ((a->locks || a->fixed)
                ? a->selector
                : next_selector(r))
        : 0;
    if (bytes && !sel)
    {
        return 0;
    }
    Segment *old = a->size
        ? segment(r, a->selector)
        : (Segment *)0;
    Segment *s = bytes
        ? new_segment(r, sel, bytes)
        : (Segment *)0;
    if (bytes && !s)
    {
        return 0;
    }
    if (old && s)
    {
        for (u32 i = 0; i < bytes && i < old->size; i++)
        {
            s->bytes[i] = old->bytes[i];
        }
    }
    if (old && (a->locks || a->fixed) && s)
    {
        replace_backing(r, old, s);
        s = 0;
    }
    else
    {
        if (old)
        {
            invalidate_aliases(r, old);
            unmap(r, a->selector);
        }
        if (s)
        {
            attach(r, s);
        }
    }
    r->bytes = r->bytes - a->size + bytes;
    a->size = bytes;
    a->selector = sel;
    return a->handle;
}

static u16 lock_memory(Runtime *r, u16 handle)
{
    Allocation *a = allocation(r, handle);
    if (!a || !a->size || a->locks == 65535)
    {
        return 0;
    }
    a->locks++;
    return a->selector;
}

static u16 unlock_memory(Runtime *r, u16 handle)
{
    Allocation *a = allocation(r, handle);
    if (!a || !a->locks)
    {
        return 0;
    }
    return --a->locks
        ? 1
        : 0;
}

static int free_memory(Runtime *r, u16 handle)
{
    Allocation **p = &r->handles;
    while (*p)
    {
        Allocation *a = *p;
        if (a->handle == handle)
        {
            if (a->locks)
            {
                return 0;
            }
            if (a->size)
            {
                invalidate_aliases(r, segment(r, a->selector));
                unmap(r, a->selector);
            }
            r->bytes -= a->size;
            *p = a->next;
            r->allocated--;
            r->release(r->storage, a);
            return 1;
        }
        p = &a->next;
    }
    return 0;
}

static void destroy_local_memory(Runtime *r);
static void destroy_task_files(Runtime *r);
static void destroy_task_profiles(Runtime *r);
static void destroy_task_system(Runtime *r);
static void destroy_instance_thunks(Runtime *r);
static void destroy_task_gui(Runtime *r);
static void destroy_task_modules(Runtime *r);
static void destroy_task_wep(Runtime *r);
static void destroy_task_lifecycle(Runtime *r);

static void destroy_runtime(Runtime *r)
{
    destroy_task_wep(r);
    destroy_task_gui(r);
    destroy_task_lifecycle(r);
    destroy_task_modules(r);
    destroy_instance_thunks(r);
    destroy_task_system(r);
    destroy_task_profiles(r);
    destroy_task_files(r);
    destroy_local_memory(r);
    while (r->handles)
    {
        Allocation *a = r->handles;
        r->handles = a->next;
        r->release(r->storage, a);
    }
    while (r->segments)
    {
        Segment *s = r->segments;
        r->segments = s->next;
        release_segment(r, s);
    }
    r->bytes = 0;
    r->count = 0;
    r->allocated = 0;
    r->code = (CodeBinding *)0;
    r->environmentSelector = 0;
    r->resources = 0;
}

static void init_runtime(Runtime *r, void *storage, AllocateStorage alloc, ReleaseStorage release,
                         u32 maxAllocations, u32 maxSegments, unsigned long long maxBytes)
{
    zero(r, sizeof(*r));
    r->storage = storage;
    r->alloc = alloc;
    r->release = release;
    r->nextHandle = 1;
    r->nextSelector = 0x5000;
    r->maxAllocations = maxAllocations;
    r->maxSegments = maxSegments;
    r->maxBytes = maxBytes;
}
