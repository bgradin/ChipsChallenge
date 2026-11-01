/* Source-evidenced MMSYSTEM namespace; real WinMM pointers never enter guest data. */
typedef ABI iptr (*MmCall0)(void);
typedef ABI iptr (*MmCall2)(iptr, iptr);
typedef ABI iptr (*MmCall3)(iptr, iptr, iptr);
typedef ABI iptr (*MmCall4)(iptr, iptr, iptr, iptr);
typedef struct MmDevice
{
    struct MmDevice *next;
    u32 native;
    u16 id;
} MmDevice;
typedef struct TaskModules
{
    TaskUi *ui;
    iptr native, procedures[5];
    u32 references, nextDevice;
    MmDevice *devices;
    ProgramBinding bindings[5];
} TaskModules;
static const u16 mm_ordinals[] = {2, 201, 401, 701, 706};

static const char *mm_name(u32 index, int native)
{
    switch (index)
    {
    case 0:
        return native
            ? "sndPlaySoundW"
            : "sndPlaySound";
    case 1:
        return "midiOutGetNumDevs";
    case 2:
        return "waveOutGetNumDevs";
    case 3:
        return native
            ? "mciSendCommandW"
            : "mciSendCommand";
    case 4:
        return native
            ? "mciGetErrorStringW"
            : "mciGetErrorString";
    default:
        return 0;
    }
}

static int mm_equals(const u8 *a, const char *b, int ignoreCase)
{
    for (u32 i = 0; i < 65536; i++)
    {
        u8 x = a[i], y = (u8)b[i];
        if (ignoreCase)
        {
            if (x >= 'a' && x <= 'z')
            {
                x -= 32;
            }
            if (y >= 'a' && y <= 'z')
            {
                y -= 32;
            }
        }
        if (x != y)
        {
            return 0;
        }
        if (!x)
        {
            return 1;
        }
    }
    return 0;
}

static const ProgramBinding *module_resolve(Runtime *r, u32 address)
{
    TaskModules *m = r->modules;
    if (!m || (address >> 16) != 0xf100)
    {
        return 0;
    }
    for (u32 i = 0; i < 5; i++)
    {
        if (mm_ordinals[i] == (u16)address)
        {
            return m->bindings + i;
        }
    }
    return 0;
}

static u16 mm_guest_device(Runtime *r, u32 native)
{
    TaskModules *m = r->modules;
    if (m)
    {
        for (MmDevice *d = m->devices; d; d = d->next)
        {
            if (d->native == native)
            {
                return d->id;
            }
        }
    }
    return 0;
}

static iptr mm_procedure(TaskModules *m, u32 index)
{
    if (!m->native || !m->references)
    {
        return 0;
    }
    if (!m->procedures[index])
    {
        m->procedures[index] = UI_CALL(m->ui, GetProcAddress, m->native, (iptr)mm_name(index, 1));
    }
    return m->procedures[index];
}

static u16 *mm_path(TaskUi *ui, UiArena *arena, u32 pointer)
{
    Runtime *r = ui->runtime;
    u32 length = 0;
    u8 *text = kernel_string(r, pointer, &length);
    if (!text)
    {
        return 0;
    }
    /* Native task startup sets its OS working directory; explicit absolute original
       paths remain untouched. No CHIPS-specific file or sound name is synthesized. */
    return ui_wide_bytes(arena, text, length);
}

static u32 mm_mci(TaskModules *m, UiArena *arena, u16 device, u16 command, u32 flags, u32 pointer)
{
    TaskUi *ui = m->ui;
    Runtime *r = ui->runtime;
    MmCall4 call = (MmCall4)(void *)mm_procedure(m, 3);
    if (!call)
    {
        return 261;
    }
    if (command == 0x803)
    {
        u8 *source = ui_buffer(r, pointer, 20, 1);
        if (!source)
        {
            return 0;
        }
        u32 values[5];
        for (u32 i = 0; i < 5; i++)
        {
            values[i] = ui_get32(source + i * 4);
        }
        u8 native[36];
        zero(native, sizeof(native));
        if (flags & 1)
        {
            *(iptr *)native = ui_handle(ui, (u16)values[0], 5);
        }
        *(iptr *)(native + 12) = flags & 0x1000
            ? values[2]
            : (iptr)ui_wide(arena, values[2], 0);
        if (flags & 0x200)
        {
            *(iptr *)(native + 20) = (iptr)mm_path(ui, arena, values[3]);
        }
        if (flags & 0x400)
        {
            *(iptr *)(native + 28) = (iptr)ui_wide(arena, values[4], 0);
        }
        if (r->error)
        {
            return 0;
        }
        u32 error = (u32)call(device, command, flags, (iptr)native);
        if (error)
        {
            return error;
        }
        u32 nativeId = ui_get32(native + 8);
        if (m->nextDevice >= 65535)
        {
            call(nativeId, 0x804, 0, 0);
            return 264;
        }
        MmDevice *d = r->alloc(r->storage, sizeof(*d));
        if (!d)
        {
            call(nativeId, 0x804, 0, 0);
            return 264;
        }
        d->id = (u16)m->nextDevice++;
        d->native = nativeId;
        d->next = m->devices;
        m->devices = d;
        ui_put16(source + 4, d->id);
        return 0;
    }
    MmDevice **link = &m->devices;
    while (*link && (*link)->id != device)
    {
        link = &(*link)->next;
    }
    MmDevice *d = *link;
    if (!d)
    {
        return 257;
    }
    if (command == 0x804)
    {
        u32 result = (u32)call(d->native, command, flags, 0);
        if (!result)
        {
            *link = d->next;
            r->release(r->storage, d);
        }
        return result;
    }
    if (command == 0x814)
    {
        u8 *source = ui_buffer(r, pointer, 16, 1);
        if (!source)
        {
            return 0;
        }
        u8 native[24];
        zero(native, 24);
        if (flags & 1)
        {
            *(iptr *)native = ui_handle(ui, (u16)ui_get32(source), 5);
        }
        ui_put32(native + 16, ui_get32(source + 8));
        ui_put32(native + 20, ui_get32(source + 12));
        if (r->error)
        {
            return 0;
        }
        u32 result = (u32)call(d->native, command, flags, (iptr)native);
        if (!result)
        {
            unsigned long long value = *(unsigned long long *)(native + 8);
            if (value > 0xffffffff)
            {
                r->error = 14;
                return 0;
            }
            ui_put32(source + 4, (u32)value);
        }
        return result;
    }
    if (command == 0x806)
    {
        u8 *source = ui_buffer(r, pointer, 12, 0);
        if (!source)
        {
            return 0;
        }
        u8 native[16];
        zero(native, 16);
        if (flags & 1)
        {
            *(iptr *)native = ui_handle(ui, (u16)ui_get32(source), 5);
        }
        ui_put32(native + 8, ui_get32(source + 4));
        ui_put32(native + 12, ui_get32(source + 8));
        if (r->error)
        {
            return 0;
        }
        return (u32)call(d->native, command, flags, (iptr)native);
    }
    return 261;
}

ABI NOINLINE u16 __win16_mm_service(Runtime *r, State *s)
{
    if (r->error)
    {
        return 0;
    }
    TaskModules *m = r->modules;
    if (!m || !m->references)
    {
        r->error = 14;
        return 0;
    }
    TaskUi *ui = m->ui;
    UiArena arena = {r, 0};
    u16 ordinal = s->ip;
    u32 result = 0;
    u16 cleanup = 0;
    if (ordinal == 201 || ordinal == 401)
    {
        MmCall0 call = (MmCall0)(void *)mm_procedure(m, ordinal == 201
            ? 1
            : 2);
        if (!call)
        {
            r->error = 14;
            return 0;
        }
        result = (u16)call();
    }
    else if (ordinal == 2)
    {
        cleanup = 6;
        u16 flags = __win16_read_word(r, s->ss, s->sp + 4);
        u32 pointer = __win16_read_dword(r, s->ss, s->sp + 6);
        if (flags & 4)
        {
            r->error = 14;
            return 0;
        }
        u16 *path = pointer
            ? mm_path(ui, &arena, pointer)
            : 0;
        MmCall2 call = (MmCall2)(void *)mm_procedure(m, 0);
        if (!r->error && call)
        {
            result = (u32)call((iptr)path, flags);
        }
    }
    else if (ordinal == 706)
    {
        cleanup = 10;
        u32 capacity = __win16_read_word(r, s->ss, s->sp + 4),
            pointer = __win16_read_dword(r, s->ss, s->sp + 6),
            error = __win16_read_dword(r, s->ss, s->sp + 10);
        if (capacity)
        {
            u8 *dest = ui_buffer(r, pointer, capacity, 1);
            u16 *text = ui_alloc(&arena, capacity * 2);
            MmCall3 call = (MmCall3)(void *)mm_procedure(m, 4);
            if (dest && text && call && !r->error)
            {
                result = (u32)call(error, (iptr)text, capacity);
                if (result)
                {
                    u32 n = 0;
                    while (n + 1 < capacity && text[n])
                    {
                        dest[n] = ui_ansi(r, text[n]);
                        n++;
                    }
                    dest[n] = 0;
                }
            }
        }
    }
    else if (ordinal == 701)
    {
        cleanup = 12;
        u16 device = __win16_read_word(r, s->ss, s->sp + 14),
            command = __win16_read_word(r, s->ss, s->sp + 12);
        u32 flags = __win16_read_dword(r, s->ss, s->sp + 8),
            pointer = __win16_read_dword(r, s->ss, s->sp + 4);
        if (!r->error)
        {
            result = mm_mci(m, &arena, device, command, flags, pointer);
        }
    }
    else
    {
        r->error = 14;
    }
    ui_destroy_arena(&arena);
    if (!r->error)
    {
        s->ax = (u16)result;
        if (ordinal == 701)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return cleanup;
}

static void mm_close_devices(TaskModules *m)
{
    Runtime *r = m->ui->runtime;
    MmCall4 close = (MmCall4)(void *)mm_procedure(m, 3);
    while (m->devices)
    {
        MmDevice *d = m->devices;
        m->devices = d->next;
        if (close)
        {
            close(d->native, 0x804, 0, 0);
        }
        r->release(r->storage, d);
    }
    MmCall2 stop = (MmCall2)(void *)mm_procedure(m, 0);
    if (stop)
    {
        stop(0, 0);
    }
}

ABI NOINLINE u16 __win16_module_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    TaskModules *m = r->modules;
    const KernelApiContract *c = kernel_contract(ordinal);
    if (!m || !c || (ordinal != 50 && ordinal != 95 && ordinal != 96))
    {
        r->error = 14;
        return 0;
    }
    u32 result = 0;
    TaskUi *ui = m->ui;
    if (ordinal == 95)
    {
        u32 length = 0;
        u8 *name = kernel_string(r, kernel_argument(r, s, c, 0), &length);
        if (!name)
        {
            return 0;
        }
        for (u32 i = 0; i < length; i++)
        {
            if (name[i] == '/' || name[i] == '\\')
            {
                name += i + 1, length -= i + 1, i = 0;
            }
        }
        if (mm_equals(name, "MMSYSTEM.DLL", 1) || mm_equals(name, "MMSYSTEM", 1))
        {
            if (!m->native)
            {
                static const u16 dll[] = {'w', 'i', 'n', 'm', 'm', '.', 'd', 'l', 'l', 0};
                m->native = UI_CALL(ui, LoadLibraryExW, (iptr)dll, 0, 0x800);
            }
            if (m->native)
            {
                m->references++;
                result = 0x600;
            }
            else
            {
                result = 2;
            }
        }
        else
        {
            result = 2;
        }
    }
    else if (ordinal == 96)
    {
        u32 handle = kernel_argument(r, s, c, 0);
        if (handle == 0x600 && m->references)
        {
            if (m->references == 1)
            {
                mm_close_devices(m);
                UI_CALL(ui, FreeLibrary, m->native);
                m->native = 0;
                for (u32 i = 0; i < 5; i++)
                {
                    m->procedures[i] = 0;
                }
            }
            m->references--;
        }
    }
    else if (kernel_argument(r, s, c, 0) == 0x600 && m->references)
    {
        u32 value = kernel_argument(r, s, c, 1), length = 0;
        u8 *name = value >> 16
            ? kernel_string(r, value, &length)
            : 0;
        if (r->error)
        {
            return 0;
        }
        for (u32 i = 0; i < 5; i++)
        {
            if (name
                ? mm_equals(name, mm_name(i, 0), 0)
                : value == mm_ordinals[i])
            {
                if (mm_procedure(m, i))
                {
                    result = 0xf1000000u | mm_ordinals[i];
                }
                break;
            }
        }
    }
    if (!r->error && c->resultWidth)
    {
        s->ax = (u16)result;
        if (c->resultWidth == 4)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return c->cleanup;
}

ABI NOINLINE int __win16_install_modules(Runtime *r)
{
    if (r->modules || !r->gui || !r->gui->ui)
    {
        return 0;
    }
    TaskModules *m = r->alloc(r->storage, sizeof(*m));
    if (!m)
    {
        return 0;
    }
    zero(m, sizeof(*m));
    m->ui = r->gui->ui;
    m->nextDevice = 1;
    for (u32 i = 0; i < 5; i++)
    {
        m->bindings[i].segment = 0xf100;
        m->bindings[i].offset = mm_ordinals[i];
        m->bindings[i].kind = 2;
        m->bindings[i].entry = __win16_mm_service;
    }
    r->modules = m;
    return 1;
}

static void destroy_task_modules(Runtime *r)
{
    TaskModules *m = r->modules;
    if (!m)
    {
        return;
    }
    if (m->references)
    {
        mm_close_devices(m);
        UI_CALL(m->ui, FreeLibrary, m->native);
    }
    r->release(r->storage, m);
    r->modules = 0;
}
