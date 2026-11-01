extern __attribute__((visibility("hidden"))) ABI u32 native_GetWindowsDirectoryA(u8 *, u32);
extern __attribute__((visibility("hidden"))) ABI u32 native_GetModuleFileNameA(void *, u8 *, u32);
extern __attribute__((visibility("hidden"))) ABI u32 native_GetShortPathNameA(const u8 *, u8 *,
                                                                              u32);
typedef struct WindowsTaskSystem
{
    TaskSystem system;
    u8 module[260], directory[260];
} WindowsTaskSystem;

ABI NOINLINE int __win16_install_windows_system(Runtime *r, u16 instance)
{
    if (r->system)
    {
        return 0;
    }
    WindowsTaskSystem *host = r->alloc(r->storage, sizeof(*host));
    if (!host)
    {
        return 0;
    }
    zero(host, sizeof(*host));
    u32 module = native_GetModuleFileNameA(0, host->module, 260),
        directory = native_GetWindowsDirectoryA(host->directory, 260);
    if (!module || module >= 260 || !directory || directory >= 260)
    {
        r->release(r->storage, host);
        return 0;
    }
    u8 shortPath[260];
    u32 length = native_GetShortPathNameA(host->module, shortPath, 260);
    if (length && length < 260)
    {
        for (u32 i = 0; i <= length; i++)
        {
            host->module[i] = shortPath[i];
        }
    }
    host->system.instance = instance;
    host->system.windowsVersion = 0x0a03;
    host->system.dosVersion = 0x0500;
    host->system.modulePath = host->module;
    host->system.windowsDirectory = host->directory;
    r->system = &host->system;
    r->ownsSystem = 1;
    return 1;
}
