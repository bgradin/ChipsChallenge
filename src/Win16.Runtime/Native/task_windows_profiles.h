extern __attribute__((visibility("hidden"))) ABI u32 native_GetPrivateProfileIntA(const u8 *,
                                                                                  const u8 *, int,
                                                                                  const u8 *);
extern __attribute__((visibility("hidden"))) ABI u32
native_GetPrivateProfileStringA(const u8 *, const u8 *, const u8 *, u8 *, u32, const u8 *);
extern __attribute__((visibility("hidden"))) ABI int
native_WritePrivateProfileStringA(const u8 *, const u8 *, const u8 *, const u8 *);
typedef struct WindowsTaskProfiles
{
    TaskProfiles api;
    Runtime *runtime;
} WindowsTaskProfiles;
/* Match the managed task provider's relative-path policy. Win32 profile APIs
 * otherwise search the system Windows directory, independently of process CWD. */

static const u8 *windows_profile_path(void *context, const u8 *file, u8 *output)
{
    if (!file)
    {
        return 0;
    }
    u32 length = 0;
    while (length < 65535 && file[length])
    {
        length++;
    }
    if (length == 65535)
    {
        return 0;
    }
    if (file[0] == '\\' || file[0] == '/' || (length > 1 && file[1] == ':'))
    {
        return file;
    }
    WindowsTaskProfiles *host = context;
    const u8 *module = host->runtime->system->modulePath;
    u32 prefix = 0;
    for (u32 i = 0; i < 260 && module[i]; i++)
    {
        if (module[i] == '\\' || module[i] == '/')
        {
            prefix = i + 1;
        }
    }
    if (!prefix || prefix + length >= 520)
    {
        return 0;
    }
    for (u32 i = 0; i < prefix; i++)
    {
        output[i] = module[i];
    }
    for (u32 i = 0; i <= length; i++)
    {
        output[prefix + i] = file[i];
    }
    return output;
}

static u32 windows_profile_int(void *context, const u8 *app, const u8 *key, int fallback,
                               const u8 *file)
{
    u8 path[520];
    file = windows_profile_path(context, file, path);
    return file
        ? native_GetPrivateProfileIntA(app, key, fallback, file)
        : (u32)fallback;
}

static u32 windows_profile_string(void *context, const u8 *app, const u8 *key, const u8 *fallback,
                                  u8 *output, u16 bytes, const u8 *file)
{
    u8 path[520];
    file = windows_profile_path(context, file, path);
    if (!file)
    {
        if (bytes)
        {
            output[0] = 0;
        }
        return 0;
    }
    return native_GetPrivateProfileStringA(app, key, fallback, output, bytes, file);
}

static int windows_profile_write(void *context, const u8 *app, const u8 *key, const u8 *value,
                                 const u8 *file)
{
    u8 path[520];
    file = windows_profile_path(context, file, path);
    return file
        ? native_WritePrivateProfileStringA(app, key, value, file)
        : 0;
}

ABI NOINLINE int __win16_install_windows_profiles(Runtime *r)
{
    if (r->profiles || !r->system || !r->system->modulePath)
    {
        return 0;
    }
    WindowsTaskProfiles *host = r->alloc(r->storage, sizeof(*host));
    if (!host)
    {
        return 0;
    }
    zero(host, sizeof(*host));
    host->runtime = r;
    host->api.context = host;
    host->api.integer = windows_profile_int;
    host->api.string = windows_profile_string;
    host->api.write = windows_profile_write;
    r->profiles = &host->api;
    r->ownsProfiles = 1;
    return 1;
}
