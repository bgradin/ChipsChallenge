typedef struct
{
    u32 reads, writes;
    int fail;
} ProfileContractHost;

static u32 profile_contract_int(void *context, const u8 *app, const u8 *key, int fallback,
                                const u8 *file)
{
    ProfileContractHost *host = context;
    host->reads++;
    if (!app || app[0] != 'P' || !key || key[0] != 'K' || !file || file[0] != 'F' || fallback != -2)
    {
        return 0x7777;
    }
    return 0x12345678;
}

static u32 profile_contract_string(void *context, const u8 *app, const u8 *key, const u8 *fallback,
                                   u8 *output, u16 bytes, const u8 *file)
{
    ProfileContractHost *host = context;
    host->reads++;
    if (!bytes)
    {
        return 0;
    }
    if (!app && !key)
    {
        if (bytes >= 4)
        {
            output[0] = 'P';
            output[1] = 0;
            output[2] = 0;
            return 2;
        }
        output[0] = 0;
        return 0;
    }
    if (!app || !key || !fallback || fallback[0] != 'D' || !file)
    {
        return bytes;
    }
    const u8 value[] = {'a', 'b', 'c', 0};
    u32 count = bytes - 1;
    if (count > 3)
    {
        count = 3;
    }
    for (u32 i = 0; i < count; i++)
    {
        output[i] = value[i];
    }
    output[count] = 0;
    return count;
}

static int profile_contract_write(void *context, const u8 *app, const u8 *key, const u8 *value,
                                  const u8 *file)
{
    ProfileContractHost *host = context;
    host->writes++;
    if (host->fail || (!app && !key && !value))
    {
        return 0;
    }
    return file && file[0] == 'F' && (!app || app[0] == 'P') && (!key || key[0] == 'K') &&
        (!value || value[0] == 'V');
}

static int task_profiles_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 2048))
    {
        return 35;
    }
    s.ss = s.ds = 0x1000;
    s.sp = 1024;
    s.dx = 0xabcd;
    ProfileContractHost host;
    zero(&host, sizeof(host));
    TaskProfiles p = {&host, profile_contract_int, profile_contract_string, profile_contract_write};
    r.profiles = &p;
    __win16_write_word(&r, s.ds, 64, 'P');
    __win16_write_word(&r, s.ds, 68, 'K');
    __win16_write_word(&r, s.ds, 72, 'D');
    __win16_write_word(&r, s.ds, 76, 'F');
    __win16_write_word(&r, s.ds, 80, 'V');
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), 0x1000004c);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 8), 0xfffe);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0x10000044);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 14), 0x10000040);
    if (__win16_profile_service(&r, &s, 127) != 14 || s.ax != 0x5678 || s.dx != 0xabcd ||
        host.reads != 1)
    {
        return 36;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), 0x1000004c);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 8), 4);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0x10000200);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 14), 0x10000048);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 18), 0x10000044);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 22), 0x10000040);
    if (__win16_profile_service(&r, &s, 128) != 22 || s.ax != 3 ||
        __win16_read_dword(&r, s.ds, 512) != 0x00636261 || s.dx != 0xabcd)
    {
        return 37;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0x100007ff);
    u32 before = host.reads;
    if (__win16_profile_service(&r, &s, 128) || r.error != 9 || host.reads != before)
    {
        return 38;
    }
    r.error = 0;
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 8), 0);
    if (__win16_profile_service(&r, &s, 128) != 22 || s.ax)
    {
        return 39;
    }
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 4), 0x1000004c);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 8), 0x10000050);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 12), 0x10000044);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 16), 0x10000040);
    if (__win16_profile_service(&r, &s, 129) != 16 || s.ax != 1 || host.writes != 1)
    {
        return 40;
    }
    host.fail = 1;
    if (__win16_profile_service(&r, &s, 129) != 16 || s.ax || host.writes != 2)
    {
        return 41;
    }
    host.fail = 0;
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 8), 0);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 12), 0);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 16), 0);
    if (__win16_profile_service(&r, &s, 129) != 16 || s.ax)
    {
        return 42;
    }
    destroy_runtime(&r);
    Block *b = (Block *)storage->bytes;
    return r.profiles || !b->available || b->next
        ? 43
        : 0;
}
