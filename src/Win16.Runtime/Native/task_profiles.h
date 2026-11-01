typedef u32 (*TaskProfileInt)(void *, const u8 *, const u8 *, int, const u8 *);
typedef u32 (*TaskProfileString)(void *, const u8 *, const u8 *, const u8 *, u8 *, u16, const u8 *);
typedef int (*TaskProfileWrite)(void *, const u8 *, const u8 *, const u8 *, const u8 *);
typedef struct TaskProfiles
{
    void *context;
    TaskProfileInt integer;
    TaskProfileString string;
    TaskProfileWrite write;
} TaskProfiles;

static const u8 *profile_string(Runtime *r, u32 pointer)
{
    u32 length = 0;
    return pointer
        ? kernel_string(r, pointer, &length)
        : 0;
}

ABI NOINLINE u16 __win16_profile_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const KernelApiContract *c = kernel_contract(ordinal);
    TaskProfiles *p = r->profiles;
    if (!c || !p || !p->integer || !p->string || !p->write || ordinal < 127 || ordinal > 129)
    {
        r->error = 14;
        return 0;
    }
    u32 args[6] = {0};
    for (u32 i = 0; i < c->count; i++)
    {
        args[i] = kernel_argument(r, s, c, (u16)i);
    }
    if (r->error)
    {
        return 0;
    }
    const u8 *app = profile_string(r, args[0]), *key = profile_string(r, args[1]);
    if (r->error)
    {
        return 0;
    }
    u32 result = 0;
    if (ordinal == 127)
    {
        const u8 *file = profile_string(r, args[3]);
        if (r->error)
        {
            return 0;
        }
        result = p->integer(p->context, app, key, (short)args[2], file);
    }
    else if (ordinal == 128)
    {
        const u8 *fallback = profile_string(r, args[2]), *file = profile_string(r, args[5]);
        if (r->error || !file_buffer(r, args[3], args[4]))
        {
            return 0;
        }
        u8 *output = 0;
        if (args[4])
        {
            Segment *v = segment(r, (u16)(args[3] >> 16));
            output = v->bytes + (u16)args[3];
        }
        result = p->string(p->context, app, key, fallback, output, (u16)args[4], file);
        if (args[4] && result >= args[4])
        {
            r->error = 14;
            return 0;
        }
    }
    else
    {
        const u8 *value = profile_string(r, args[2]), *file = profile_string(r, args[3]);
        if (r->error)
        {
            return 0;
        }
        result = p->write(p->context, app, key, value, file)
            ? 1
            : 0;
    }
    s->ax = (u16)result;
    return c->cleanup;
}

static void destroy_task_profiles(Runtime *r)
{
    if (r->ownsProfiles && r->profiles)
    {
        r->release(r->storage, r->profiles);
    }
    r->profiles = 0;
    r->ownsProfiles = 0;
}
