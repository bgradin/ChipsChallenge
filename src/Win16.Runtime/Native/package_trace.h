/* Optional, bounded diagnostic observation. No traces or sidecars are required
 * to launch. The caller explicitly supplies a fresh WIN16_TRACE_FILE path. */
extern __attribute__((visibility("hidden"))) ABI u32 package_GetEnvironmentVariableA(const u8 *,
                                                                                     u8 *, u32);
extern __attribute__((visibility("hidden"))) ABI iptr package_CreateFileA(const u8 *, u32, u32,
                                                                          void *, u32, u32, iptr);
extern __attribute__((visibility("hidden"))) ABI int package_WriteFile(iptr, const void *, u32,
                                                                       u32 *, void *);
extern __attribute__((visibility("hidden"))) ABI int package_SetFilePointerEx(iptr, long long,
                                                                              long long *, u32);
typedef struct
{
    u32 kind, address, csip, axdx, sssp, depth, dses, flags;
} PackageEvent;
typedef struct
{
    u32 header[24];
    iptr file;
    u32 next, last, ioError, seen[256], seenMask[256];
    PackageEvent events[4096];
} PackageTrace;

static void package_trace_event(PackageTrace *t, PackageEvent event)
{
    u32 n = t->header[2];
    if (n >= 4096)
    {
        t->header[3]++;
        n = 2048 + (t->next++ % 2048);
    }
    else
    {
        t->header[2]++;
    }
    t->last = n;
    t->events[n] = event;
    if (!t->header[3])
    {
        u32 wrote = 0;
        if (!package_WriteFile(t->file, t->events + n, sizeof(PackageEvent), &wrote, 0) ||
            wrote != sizeof(PackageEvent))
        {
            t->ioError = 1;
        }
    }
}

static void package_observe(Runtime *r, State *s, u32 kind, u32 address)
{
    PackageTrace *t = r->observation;
    if (!t)
    {
        return;
    }
    if (kind == 8)
    {
        Segment *v = find_segment(r, (u16)address);
        if (v)
        {
            unsigned long long p = (unsigned long long)(iptr)v->bytes;
            PackageEvent e = {8, v->selector, (u32)p, (u32)(p >> 32),
                              v->size, 0, v->permissions, v->origin};
            package_trace_event(t, e);
        }
        return;
    }
    if (kind == 1)
    {
        t->header[4]++;
        t->header[8] = address;
        return;
    }
    if (kind == 2)
    {
        t->header[5]++;
    }
    if (kind == 4)
    {
        t->header[6]++;
    }
    if (kind == 3)
    {
        t->header[7] = address;
    }
    t->header[9] = address;
    /* Counts include every service call. Store first pre/post snapshots for
       each identity so high-frequency polling/blits cannot evict new APIs. */
    if (kind == 2 || kind == 6)
    {
        u32 i = 0, mask = kind == 2
            ? 1
            : 2;
        for (; i < 256; i++)
        {
            if (!t->seen[i] || t->seen[i] == address)
            {
                break;
            }
        }
        if (i == 256)
        {
            t->header[3]++;
            return;
        }
        if (t->seenMask[i] & mask)
        {
            return;
        }
        t->seen[i] = address;
        t->seenMask[i] |= mask;
    }
    PackageEvent event = {kind,
                          address,
                          program_address(s->cs, s->ip),
                          (u32)s->ax << 16 | s->dx,
                          (u32)s->ss << 16 | s->sp,
                          r->program
                              ? r->program->depth
                              : 0,
                          (u32)s->ds << 16 | s->es,
                          s->flags};
    if (kind == 2)
    {
        /* Pre-import argument snapshot, preserving depth. Other records carry
           CPU registers, including post-import AX:DX results (kind 6). */
        Segment *v = find_segment(r, s->ss);
        u32 *row = (u32 *)(void *)&event;
        const u32 fields[] = {2, 3, 4, 6, 7};
        for (u32 i = 0; i < 5; i++)
        {
            u32 value = 0;
            for (u32 j = 0; j < 4; j++)
            {
                u16 at = (u16)(s->sp + 4 + i * 4 + j);
                if (v && at < v->size)
                {
                    value |= (u32)v->bytes[at] << (8 * j);
                }
            }
            row[fields[i]] = value;
        }
    }
    package_trace_event(t, event);
}

static int package_trace_open(Runtime *r)
{
    const u8 variable[] = "WIN16_TRACE_FILE";
    u8 path[520];
    u32 n = package_GetEnvironmentVariableA(variable, path, 520);
    if (!n)
    {
        return 1;
    }
    if (n >= 520)
    {
        return 0;
    }
    PackageTrace *trace = r->alloc(r->storage, sizeof(*trace));
    if (!trace)
    {
        return 0;
    }
    zero(trace, sizeof(*trace));
    trace->file = package_CreateFileA(path, 0x40000000, 1, 0, 1, 0x80, 0);
    if (trace->file == -1)
    {
        r->release(r->storage, trace);
        return 0;
    }
    trace->header[0] = 0x38525457;
    trace->header[1] = 1;
    u32 wrote = 0;
    if (!package_WriteFile(trace->file, trace->header, sizeof(trace->header), &wrote, 0) ||
        wrote != sizeof(trace->header))
    {
        trace->ioError = 1;
    }
    r->observation = trace;
    r->observe = package_observe;
    /* Optional, read-only debugger discovery of initial segment backing. This
       does not change guest state, allocate guest storage or supply any rules. */
    for (Segment *s = r->segments; s; s = s->next)
    {
        unsigned long long pointer = (unsigned long long)(iptr)s->bytes;
        PackageEvent e = {8, s->selector, (u32)pointer, (u32)(pointer >> 32),
                          s->size, 0, s->permissions, s->origin};
        package_trace_event(trace, e);
    }
    return 1;
}

static int package_trace_finish(Runtime *r, State *state, int result, u32 executionError)
{
    PackageTrace *t = r->observation;
    if (!t)
    {
        return 1;
    }
    t->header[10] = executionError;
    t->header[11] = (u32)result;
    t->header[12] = r->terminated;
    t->header[13] = r->exitCode;
    t->header[14] = r->codeWriteCount;
    t->header[15] = program_address(state->cs, state->ip);
    t->header[16] = (u32)state->ss << 16 | state->sp;
    t->header[17] = r->program
        ? r->program->depth
        : 0;
    t->header[18] = (u32)state->ax << 16 | state->dx;
    t->header[19] = state->flags;
    t->header[20] = r->count;
    t->header[21] = (u32)r->bytes;
    t->header[22] = r->allocated;
    t->header[23] =
        !!(r->gui || r->modules || r->lifecycle || r->files || r->profiles || r->system);
    u32 wrote = 0, bytes = sizeof(t->header);
    int ok = !t->ioError && package_SetFilePointerEx(t->file, 0, 0, 0) &&
        package_WriteFile(t->file, t->header, bytes, &wrote, 0) && wrote == bytes;
    if (t->header[2] < 4096)
    {
        bytes = t->header[2] * sizeof(PackageEvent);
        wrote = 0;
        if (ok)
        {
            ok = package_WriteFile(t->file, t->events, bytes, &wrote, 0) && wrote == bytes;
        }
    }
    else
    {
        /* Keep startup and the latest transitions in chronological order. A
           long session must not evict shutdown from a startup-only buffer. */
        u32 start = t->next % 2048, counts[] = {2048, 2048 - start, start};
        const PackageEvent *parts[] = {t->events, t->events + 2048 + start, t->events + 2048};
        for (u32 i = 0; i < 3 && ok; i++)
        {
            if (counts[i])
            {
                bytes = counts[i] * sizeof(PackageEvent);
                wrote = 0;
                ok = package_WriteFile(t->file, parts[i], bytes, &wrote, 0) && wrote == bytes;
            }
        }
    }
    if (!ui_native_CloseHandle(t->file))
    {
        ok = 0;
    }
    r->observation = 0;
    r->observe = 0;
    r->release(r->storage, t);
    return ok;
}
