/* The provider performs OS I/O. This layer owns HFILE16 identities and validates
 * every guest buffer before any OS effect. No host file pointer enters State. */
typedef void *(*TaskFileOpen)(void *, const u8 *, u16, u8 *, u16 *);
typedef int (*TaskFileClose)(void *, void *);
typedef int (*TaskFileRead)(void *, void *, u8 *, u16);
typedef int (*TaskFileSeek)(void *, void *, int, u16);
typedef struct TaskFile
{
    struct TaskFile *next;
    void *native;
    u16 handle;
} TaskFile;
typedef struct TaskFiles
{
    TaskFile *open;
    void *context;
    TaskFileOpen openFile;
    TaskFileClose closeFile;
    TaskFileRead readFile;
    TaskFileSeek seekFile;
    u32 nextHandle, count;
} TaskFiles;

static void init_task_files(TaskFiles *files, void *context, TaskFileOpen openFile,
                            TaskFileClose closeFile, TaskFileRead readFile, TaskFileSeek seekFile)
{
    zero(files, sizeof(*files));
    files->context = context;
    files->openFile = openFile;
    files->closeFile = closeFile;
    files->readFile = readFile;
    files->seekFile = seekFile;
    files->nextHandle = 3;
}

static int file_buffer(Runtime *r, u32 pointer, u32 bytes)
{
    if (!bytes)
    {
        return 1;
    }
    Segment *v = segment(r, (u16)(pointer >> 16));
    u32 offset = (u16)pointer;
    if (!v || !writable(r, v, (u16)offset, bytes))
    {
        return 0;
    }
    if (bytes > v->size || offset > v->size - bytes)
    {
        r->error = 9;
        return 0;
    }
    return 1;
}

ABI NOINLINE u16 __win16_file_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const KernelApiContract *c = kernel_contract(ordinal);
    TaskFiles *files = r->files;
    if (!c || !files || !files->openFile || !files->closeFile || !files->readFile ||
        !files->seekFile || (ordinal != 74 && ordinal != 81 && ordinal != 82 && ordinal != 84))
    {
        r->error = 14;
        return 0;
    }
    u32 result = ordinal == 84
        ? 0xffffffff
        : 0xffff,
        a = kernel_argument(r, s, c, 0);
    if (r->error)
    {
        return 0;
    }
    if (ordinal == 74)
    {
        u32 output = kernel_argument(r, s, c, 1), style = kernel_argument(r, s, c, 2), length = 0;
        if (r->error || !file_buffer(r, output, 136))
        {
            return 0;
        }
        u8 *name = kernel_string(r, a, &length);
        if (!name)
        {
            return 0;
        }
        u8 buffer[136];
        zero(buffer, sizeof(buffer));
        buffer[0] = 136;
        buffer[1] = 1;
        if (files->nextHandle > 32767 || files->count >= 4096 || (style & ~0x70u) ||
            (style & 0x70) > 0x40)
        {
            buffer[2] = 5;
        }
        else
        {
            TaskFile *record = r->alloc(r->storage, sizeof(*record));
            if (!record)
            {
                buffer[2] = 8;
            }
            else
            {
                u16 error = 0;
                void *native = files->openFile(files->context, name, (u16)style, buffer, &error);
                if (!native)
                {
                    buffer[2] = (u8)error;
                    buffer[3] = (u8)(error >> 8);
                    r->release(r->storage, record);
                }
                else
                {
                    record->native = native;
                    record->handle = (u16)files->nextHandle++;
                    record->next = files->open;
                    files->open = record;
                    files->count++;
                    result = record->handle;
                }
            }
        }
        Segment *view = segment(r, (u16)(output >> 16));
        for (u32 i = 0; i < 136; i++)
        {
            view->bytes[(u16)output + i] = buffer[i];
        }
    }
    else
    {
        TaskFile **link = &files->open;
        while (*link && (*link)->handle != (u16)a)
        {
            link = &(*link)->next;
        }
        TaskFile *record = *link;
        if (record)
        {
            if (ordinal == 81)
            {
                result = files->closeFile(files->context, record->native)
                    ? 0xffff
                    : 0;
                *link = record->next;
                files->count--;
                r->release(r->storage, record);
            }
            else if (ordinal == 82)
            {
                u32 pointer = kernel_argument(r, s, c, 1), count = kernel_argument(r, s, c, 2);
                if (r->error || !file_buffer(r, pointer, count))
                {
                    return 0;
                }
                if (!count)
                {
                    result = 0;
                }
                else
                {
                    Segment *view = segment(r, (u16)(pointer >> 16));
                    int read = files->readFile(files->context, record->native,
                                               view->bytes + (u16)pointer, (u16)count);
                    if (read >= 0 && (u32)read <= count)
                    {
                        result = (u32)read;
                    }
                    else if (read > (int)count)
                    {
                        r->error = 14;
                        return 0;
                    }
                }
            }
            else
            {
                u32 offset = kernel_argument(r, s, c, 1), origin = kernel_argument(r, s, c, 2);
                if (r->error)
                {
                    return 0;
                }
                if (origin <= 2)
                {
                    int position =
                        files->seekFile(files->context, record->native, (int)offset, (u16)origin);
                    if (position >= 0)
                    {
                        result = (u32)position;
                    }
                }
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

static void destroy_task_files(Runtime *r)
{
    if (!r->files)
    {
        return;
    }
    TaskFiles *files = r->files;
    while (files->open)
    {
        TaskFile *record = files->open;
        files->open = record->next;
        files->closeFile(files->context, record->native);
        r->release(r->storage, record);
    }
    files->count = 0;
    if (r->ownsFiles)
    {
        r->release(r->storage, files);
    }
    r->files = 0;
    r->ownsFiles = 0;
}
