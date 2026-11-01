/* Win32's legacy HFILE adapters retain the documented 136-byte OFSTRUCT layout.
 * Original HFILE16 values remain task IDs; host HFILE32 values stay in records. */
extern __attribute__((visibility("hidden"))) ABI int native_OpenFile(const u8 *, void *, u32);
extern __attribute__((visibility("hidden"))) ABI int native__lclose(int);
extern __attribute__((visibility("hidden"))) ABI u32 native__lread(int, void *, u32);
extern __attribute__((visibility("hidden"))) ABI int native__llseek(int, int, int);

static void *windows_file_open(void *context, const u8 *name, u16 style, u8 *output, u16 *error)
{
    int handle = native_OpenFile(name, output, style);
    if (handle < 0)
    {
        *error = (u16)(output[2] | (u16)output[3] << 8);
        return 0;
    }
    return (void *)((unsigned long long)(u32)handle + 1);
}

static int windows_file_close(void *context, void *file)
{
    return native__lclose((int)((unsigned long long)file - 1));
}

static int windows_file_read(void *context, void *file, u8 *buffer, u16 count)
{
    return (int)native__lread((int)((unsigned long long)file - 1), buffer, count);
}

static int windows_file_seek(void *context, void *file, int offset, u16 origin)
{
    return native__llseek((int)((unsigned long long)file - 1), offset, origin);
}

ABI NOINLINE int __win16_install_windows_files(Runtime *r)
{
    if (r->files)
    {
        return 0;
    }
    TaskFiles *files = r->alloc(r->storage, sizeof(*files));
    if (!files)
    {
        return 0;
    }
    init_task_files(files, 0, windows_file_open, windows_file_close, windows_file_read,
                    windows_file_seek);
    r->files = files;
    r->ownsFiles = 1;
    return 1;
}
