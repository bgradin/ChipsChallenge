typedef struct
{
    u32 position[4], live[4], closes;
    int readFail;
} FileContractHost;

static void *file_contract_open(void *context, const u8 *name, u16 style, u8 *of, u16 *error)
{
    FileContractHost *host = context;
    if (name[0] != 'D')
    {
        *error = 2;
        return 0;
    }
    for (u32 i = 0; i < 4; i++)
    {
        if (!host->live[i])
        {
            host->live[i] = 1;
            host->position[i] = 0;
            of[0] = 136;
            of[1] = 1;
            of[8] = 'D';
            return (void *)(unsigned long long)(i + 1);
        }
    }
    *error = 4;
    return 0;
}

static int file_contract_close(void *context, void *file)
{
    FileContractHost *host = context;
    u32 index = (u32)(unsigned long long)file - 1;
    if (index >= 4 || !host->live[index])
    {
        return -1;
    }
    host->live[index] = 0;
    host->closes++;
    return 0;
}

static int file_contract_read(void *context, void *file, u8 *buffer, u16 bytes)
{
    FileContractHost *host = context;
    u32 index = (u32)(unsigned long long)file - 1;
    if (index >= 4 || !host->live[index] || host->readFail)
    {
        return -1;
    }
    u32 count = 0;
    while (count < bytes && host->position[index] < 5)
    {
        buffer[count++] = (u8)(host->position[index] + 1);
        host->position[index]++;
    }
    return (int)count;
}

static int file_contract_seek(void *context, void *file, int delta, u16 origin)
{
    FileContractHost *host = context;
    u32 index = (u32)(unsigned long long)file - 1;
    if (index >= 4 || !host->live[index] || origin > 2)
    {
        return -1;
    }
    long long position = (origin == 0
        ? 0
        : origin == 1
                                      ? host->position[index]
                                      : 5) +
        (long long)delta;
    if (position < 0 || position > 0x7fffffff)
    {
        return -1;
    }
    host->position[index] = (u32)position;
    return (int)position;
}

static int task_files_contract(FixtureStorage *storage)
{
    init_storage(storage);
    Runtime r;
    init_runtime(&r, storage, fixture_alloc, fixture_release, 64, 64, 1024 * 1024);
    State s;
    zero(&s, sizeof(s));
    if (!map(&r, 0x1000, 4096))
    {
        return 21;
    }
    s.ss = s.ds = 0x1000;
    s.sp = 1024;
    s.dx = 0xabcd;
    FileContractHost host;
    zero(&host, sizeof(host));
    TaskFiles files;
    init_task_files(&files, &host, file_contract_open, file_contract_close, file_contract_read,
                    file_contract_seek);
    r.files = &files;
    __win16_write_word(&r, s.ds, 64, 'D');
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0x20);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000100);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0x10000040);
    if (__win16_file_service(&r, &s, 74) != 10 || s.ax == 0xffff || s.dx != 0xabcd || r.error ||
        __win16_read_word(&r, s.ds, 258))
    {
        return 22;
    }
    u16 first = s.ax;
    if (__win16_file_service(&r, &s, 74) != 10 || s.ax == first || s.ax == 0xffff ||
        files.count != 2)
    {
        return 23;
    }
    u16 second = s.ax;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 3);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000200);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), first);
    if (__win16_file_service(&r, &s, 82) != 8 || s.ax != 3 ||
        __win16_read_word(&r, s.ds, 512) != 0x0201 || __win16_read_byte(&r, s.ds, 514) != 3)
    {
        return 24;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), second);
    __win16_file_service(&r, &s, 82);
    if (s.ax != 3 || __win16_read_byte(&r, s.ds, 512) != 1)
    {
        return 25;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 1);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0xffffffff);
    __win16_write_word(&r, s.ss, (u16)(s.sp + 10), first);
    if (__win16_file_service(&r, &s, 84) != 8 || s.ax != 2 || s.dx)
    {
        return 26;
    }
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 10);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000200);
    if (__win16_file_service(&r, &s, 82) != 8 || s.ax != 3 || __win16_read_byte(&r, s.ds, 512) != 3)
    {
        return 27;
    }
    host.readFail = 1;
    if (__win16_file_service(&r, &s, 82) != 8 || s.ax != 0xffff)
    {
        return 28;
    }
    host.readFail = 0;
    u32 before = host.position[0];
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000fff);
    if (__win16_file_service(&r, &s, 82) || r.error != 9 || host.position[0] != before)
    {
        return 29;
    }
    r.error = 0;
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), first);
    if (__win16_file_service(&r, &s, 81) != 2 || s.ax || files.count != 1)
    {
        return 30;
    }
    if (__win16_file_service(&r, &s, 81) != 2 || s.ax != 0xffff)
    {
        return 31;
    }
    __win16_write_word(&r, s.ds, 64, 'X');
    __win16_write_word(&r, s.ss, (u16)(s.sp + 4), 0x20);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 6), 0x10000100);
    __win16_write_dword(&r, s.ss, (u16)(s.sp + 10), 0x10000040);
    if (__win16_file_service(&r, &s, 74) != 10 || s.ax != 0xffff ||
        __win16_read_word(&r, s.ds, 258) != 2)
    {
        return 32;
    }
    destroy_runtime(&r);
    if (r.files || files.count || host.closes != 2)
    {
        return 33;
    }
    Block *b = (Block *)storage->bytes;
    return !b->available || b->next
        ? 34
        : 0;
}
