typedef struct TaskSystem
{
    u16 instance, windowsVersion, dosVersion;
    const u8 *modulePath, *windowsDirectory;
} TaskSystem;

static u32 system_path_length(Runtime *r, const u8 *text)
{
    if (!text)
    {
        r->error = 14;
        return 0;
    }
    for (u32 i = 0; i < 65535; i++)
    {
        if (!text[i])
        {
            return i;
        }
    }
    r->error = 14;
    return 0;
}

ABI NOINLINE u16 __win16_system_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const KernelApiContract *c = kernel_contract(ordinal);
    TaskSystem *system = r->system;
    if (!c || !system || (ordinal != 3 && ordinal != 49 && ordinal != 134))
    {
        r->error = 14;
        return 0;
    }
    u32 result = 0;
    if (ordinal == 3)
    {
        result = ((u32)system->dosVersion << 16) | system->windowsVersion;
    }
    else
    {
        int module = ordinal == 49;
        u32 target = kernel_argument(r, s, c, module
            ? 1
            : 0),
            capacity = kernel_argument(r, s, c, module
                ? 2
                : 1);
        if (module)
        {
            capacity = (int)(short)capacity > 0
                ? capacity
                : 0;
        }
        u32 instance = module
            ? kernel_argument(r, s, c, 0)
            : 0;
        if (r->error)
        {
            return 0;
        }
        if (!module || !instance || instance == system->instance)
        {
            const u8 *text = module
                ? system->modulePath
                : system->windowsDirectory;
            u32 length = system_path_length(r, text);
            if (r->error)
            {
                return 0;
            }
            if (!module && capacity <= length)
            {
                result = length + 1;
            }
            else if (capacity)
            {
                if (!file_buffer(r, target, capacity))
                {
                    return 0;
                }
                Segment *v = segment(r, (u16)(target >> 16));
                u32 copied = length < capacity
                    ? length
                    : capacity - 1;
                for (u32 i = 0; i < copied; i++)
                {
                    v->bytes[(u16)target + i] = text[i];
                }
                v->bytes[(u16)target + copied] = 0;
                result = copied;
            }
        }
    }
    s->ax = (u16)result;
    if (c->resultWidth == 4)
    {
        s->dx = (u16)(result >> 16);
    }
    return c->cleanup;
}

static void destroy_task_system(Runtime *r)
{
    if (r->ownsSystem && r->system)
    {
        r->release(r->storage, r->system);
    }
    r->system = 0;
    r->ownsSystem = 0;
}
