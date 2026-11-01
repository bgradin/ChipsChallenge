/* Resource metadata is produced from the parsed original image before execution.
 * These host pointers never enter original Win16 handle or pointer fields. */
typedef struct TaskResource
{
    u16 type, id;
    const u8 *typeName, *name, *bytes;
    u32 length;
    u16 dataHandle, uses;
} TaskResource;
typedef struct TaskResources
{
    TaskResource *entries;
    u32 count;
    u16 instance;
} TaskResources;

static int resource_match(Runtime *r, u32 value, u16 id, const u8 *name)
{
    if (!(value >> 16))
    {
        return !name && (u16)(value & 0x7fff) == id;
    }
    u32 length = 0;
    u8 *text = kernel_string(r, value, &length);
    if (!text)
    {
        return 0;
    }
    if (text[0] == '#')
    {
        if (name)
        {
            return 0;
        }
        u32 number = 0;
        for (u32 i = 1; i < length; i++)
        {
            if (text[i] < '0' || text[i] > '9')
            {
                return 0;
            }
            number = number * 10 + text[i] - '0';
            if (number > 65535)
            {
                return 0;
            }
        }
        return length > 1 && (number & 0x7fff) == id;
    }
    if (!name)
    {
        return 0;
    }
    for (u32 i = 0; i <= length; i++)
    {
        u8 a = text[i], b = name[i];
        if (a >= 'a' && a <= 'z')
        {
            a -= 32;
        }
        if (b >= 'a' && b <= 'z')
        {
            b -= 32;
        }
        if (a != b)
        {
            return 0;
        }
        if (!b)
        {
            return 1;
        }
    }
    return 0;
}

ABI NOINLINE u16 __win16_resource_service(Runtime *r, State *s, u16 ordinal)
{
    const KernelApiContract *contract = kernel_contract(ordinal);
    if (r->error)
    {
        return 0;
    }
    if (!contract || ordinal < 60 || ordinal > 63 || !r->resources || r->resources->count > 0x3fff)
    {
        r->error = 14;
        return 0;
    }
    TaskResources *catalog = r->resources;
    u32 result = 0, a = kernel_argument(r, s, contract, 0);
    if (r->error)
    {
        return 0;
    }
    TaskResource *entry = 0;
    if (ordinal == 60)
    {
        u32 name = kernel_argument(r, s, contract, 1), type = kernel_argument(r, s, contract, 2);
        if (r->error)
        {
            return 0;
        }
        if (!a || a == catalog->instance)
        {
            for (u32 i = 0; i < catalog->count; i++)
            {
                TaskResource *e = catalog->entries + i;
                if (resource_match(r, type, e->type, e->typeName) &&
                    resource_match(r, name, e->id, e->name))
                {
                    result = 0x8000 + i;
                    break;
                }
            }
        }
    }
    else if (ordinal == 61)
    {
        u32 reference = kernel_argument(r, s, contract, 1);
        if (r->error)
        {
            return 0;
        }
        if ((!a || a == catalog->instance) && reference >= 0x8000 &&
            reference - 0x8000 < catalog->count)
        {
            entry = catalog->entries + reference - 0x8000;
        }
        if (entry && entry->uses < 65535)
        {
            if (!entry->dataHandle)
            {
                if (!entry->length)
                {
                    s->ax = 0;
                    return contract->cleanup;
                }
                if (entry->length > 65536)
                {
                    r->error = 14;
                    return 0;
                }
                r->wholeMemory = 1;
                u16 handle = allocate(r, entry->length, 2);
                if (!handle)
                {
                    s->ax = 0;
                    return contract->cleanup;
                }
                u16 selector = lock_memory(r, handle);
                Segment *v = selector
                    ? segment(r, selector)
                    : 0;
                if (!v)
                {
                    r->error = 14;
                    return 0;
                }
                for (u32 i = 0; i < entry->length; i++)
                {
                    v->bytes[i] = entry->bytes[i];
                }
                unlock_memory(r, handle);
                entry->dataHandle = handle;
            }
            entry->uses++;
            result = entry->dataHandle;
        }
    }
    else
    {
        for (u32 i = 0; i < catalog->count; i++)
        {
            if (catalog->entries[i].dataHandle && catalog->entries[i].dataHandle == a)
            {
                entry = catalog->entries + i;
                break;
            }
        }
        if (ordinal == 62)
        {
            if (entry)
            {
                result = (u32)lock_memory(r, (u16)a) << 16;
            }
        }
        else if (entry)
        {
            if (entry->uses)
            {
                entry->uses--;
            }
            if (!entry->uses)
            {
                Allocation *block = allocation(r, (u16)a);
                if (block)
                {
                    block->locks = 0;
                }
                free_memory(r, (u16)a);
                entry->dataHandle = 0;
            }
            result = 0;
        }
        else
        {
            Allocation *block = allocation(r, (u16)a);
            if (block)
            {
                block->locks = 0;
                result = free_memory(r, (u16)a)
                    ? 0
                    : a;
            }
        }
    }
    if (!r->error)
    {
        s->ax = (u16)result;
        if (contract->resultWidth == 4)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return contract->cleanup;
}
