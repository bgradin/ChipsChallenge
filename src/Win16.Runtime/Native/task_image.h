/* Package ABI 1: resource smoke; ABI 2: original startup, with the same bounded layout.
 * No NE parser, original file, opcode decoding or
 * application orchestration at runtime. All offsets are bounded before use. */
extern __attribute__((visibility("hidden"))) ABI int package_SetCurrentDirectoryA(const u8 *);
extern __attribute__((visibility("hidden"))) ABI iptr
package_CreateDialogIndirectParamW(iptr, const void *, iptr, iptr, iptr);

static ABI iptr package_dialog(iptr window, u32 message, iptr wp, iptr lp)
{
    return 0;
}

static TaskResources *package_resources(Runtime *r, ImageReader *reader, u32 count, u16 instance)
{
    if (count > 16383)
    {
        reader->error = 1;
        return 0;
    }
    TaskResources *catalog = r->alloc(r->storage, sizeof(*catalog));
    if (!catalog)
    {
        reader->error = 1;
        return 0;
    }
    zero(catalog, sizeof(*catalog));
    catalog->instance = instance;
    catalog->count = count;
    catalog->entries = count
        ? r->alloc(r->storage, count * sizeof(TaskResource))
        : 0;
    if (count && !catalog->entries)
    {
        reader->error = 1;
        return catalog;
    }
    if (count)
    {
        zero(catalog->entries, count * sizeof(TaskResource));
    }
    for (u32 i = 0; i < count && !reader->error; i++)
    {
        TaskResource *e = catalog->entries + i;
        e->type = (u16)number(reader, 2);
        e->id = (u16)number(reader, 2);
        u32 n = number(reader, 4);
        e->typeName = n
            ? take(reader, n)
            : 0;
        if (n && e->typeName && e->typeName[n - 1])
        {
            reader->error = 1;
        }
        n = number(reader, 4);
        e->name = n
            ? take(reader, n)
            : 0;
        if (n && e->name && e->name[n - 1])
        {
            reader->error = 1;
        }
        e->length = number(reader, 4);
        e->bytes = take(reader, e->length);
        /* Derived representations remain embedded with their originals. Native
           conversion is checked against the same bytes for dialogs below. */
        n = number(reader, 4);
        const u8 *derived = take(reader, n);
        if (reader->error)
        {
            break;
        }
        if (e->type == 5)
        {
            UiArena arena = {r, 0};
            u32 size = 0;
            u8 *actual = ui_dialog_template(r, &arena, e->bytes, e->length, &size);
            if (r->error || size != n)
            {
                reader->error = 1;
            }
            else
            {
                for (u32 j = 0; j < n; j++)
                {
                    if (actual[j] != derived[j])
                    {
                        reader->error = 1;
                        break;
                    }
                }
            }
            ui_destroy_arena(&arena);
        }
    }
    return catalog;
}

static int package_smoke(Runtime *r, TaskResources *catalog, int utility)
{
    TaskResources *saved = r->resources;
    r->resources = catalog;
    TaskUi *ui = r->gui->ui;
    int ok = 1;
    u16 scratchSelector = next_selector(r);
    Segment *scratch = scratchSelector
        ? map(r, scratchSelector, 65536)
        : 0;
    if (!scratch)
    {
        r->resources = saved;
        return 1;
    }
    for (u32 i = 0; i < catalog->count && ok && !r->error; i++)
    {
        TaskResource *e = catalog->entries + i;
        UiArena arena = {r, 0};
        u32 args[2] = {ui->instance, e->id};
        if (e->name)
        {
            u32 n = 0;
            while (n < 65535 && e->name[n])
            {
                scratch->bytes[n] = e->name[n];
                n++;
            }
            scratch->bytes[n] = 0;
            args[1] = (u32)scratchSelector << 16;
        }
        u16 ordinal = e->type == 2
            ? 175
            : e->type == 4
                    ? 150
                    : e->type == 9
                        ? 177
                        : e->type == 12
                            ? 173
                            : e->type == 14
                                ? 174
                                : 0;
        if (ordinal && !ui_gui_resource(ui, &arena, ordinal, args))
        {
            ok = 0;
        }
        /* WEP's unimported Hall-of-Fame dialogs require its original custom
           classes. They are preserved/converted, but none of the five imported
           WEP contracts loads them. Do not fake these unrelated controls. */
        if (e->type == 5 && (!utility || e->id == 100 || e->id == 101))
        {
            u32 bytes = 0;
            u8 *dialog = ui_dialog_template(r, &arena, e->bytes, e->length, &bytes);
            if (!dialog)
            {
                ok = 0;
            }
            else
            {
                /* Only the smoke copy is hidden; original/derived payloads stay intact. */
                ui_put32(dialog, ui_get32(dialog) & ~0x10000000u);
                iptr window = package_CreateDialogIndirectParamW(
                    UI_CALL(ui, GetModuleHandleW, 0), dialog, 0, (iptr)package_dialog, 0);
                if (!window)
                {
                    ok = 0;
                }
                else if (!UI_CALL(ui, DestroyWindow, window))
                {
                    ok = 0;
                }
            }
        }
        ui_destroy_arena(&arena);
        if (!ok || r->error)
        {
            r->resources = saved;
            return (int)i + 1;
        }
    }
    r->resources = saved;
    return 0;
}

ABI NOINLINE int __win16_package_bootstrap(const u8 *data, u32 length, const u8 *code,
                                           u32 codeBytes)
{
    if (!data || !code || length < 92 || length > 64 * 1024 * 1024)
    {
        return 10;
    }
    ImageReader reader = {data, data + length, 0};
    if (number(&reader, 4) != 0x374b5457)
    {
        return 10;
    }
    u32 mode = number(&reader, 4);
    if ((mode != 1 && mode != 2) || number(&reader, 4) != 44)
    {
        return 10;
    }
    u32 segments = number(&reader, 4), bindings = number(&reader, 4),
        resources = number(&reader, 4), utility = number(&reader, 4);
    u16 heapOffset = (u16)number(&reader, 2), heapSize = (u16)number(&reader, 2);
    const u8 *initial = take(&reader, 44);
    u32 entry = number(&reader, 4);
    if (reader.error || !segments || segments > 4096 || !bindings || bindings > 65536)
    {
        return 10;
    }
    HeapStorage heap;
    zero(&heap, sizeof(heap));
    heap.heap = native_HeapCreate(0, 0, 0);
    if (!heap.heap)
    {
        return 20;
    }
    heap.maximum = 32 * 1024 * 1024;
    heap.limit = 65536;
    Runtime r;
    init_runtime(&r, &heap, heap_alloc, heap_release, 16384, 65535, 16 * 1024 * 1024);
    r.wholeMemory = 1;
    State state;
    for (u32 i = 0; i < 44; i++)
    {
        ((u8 *)&state)[i] = initial[i];
    }
    int result = 0;
    ProgramImage program;
    zero(&program, sizeof(program));
    LocalHeap local;
    zero(&local, sizeof(local));
    for (u32 i = 0; i < segments && !reader.error; i++)
    {
        u16 selector = (u16)number(&reader, 2), permissions = (u16)number(&reader, 2);
        u32 size = number(&reader, 4), initialized = number(&reader, 4);
        const u8 *bytes = take(&reader, initialized);
        if (reader.error || initialized > size || (permissions & ~7))
        {
            reader.error = 1;
            break;
        }
        Segment *s = map(&r, selector, size);
        if (!s)
        {
            result = 20;
            goto finish;
        }
        for (u32 j = 0; j < initialized; j++)
        {
            s->bytes[j] = bytes[j];
        }
        s->permissions = permissions;
        s->backing->executable = !!(permissions & 4);
    }
    ProgramBinding *table = r.alloc(r.storage, bindings * sizeof(*table));
    if (!table)
    {
        result = 20;
        goto finish;
    }
    zero(table, bindings * sizeof(*table));
    for (u32 i = 0; i < bindings && !reader.error; i++)
    {
        ProgramBinding *b = table + i;
        b->segment = (u16)number(&reader, 2);
        b->offset = (u16)number(&reader, 2);
        b->kind = (u16)number(&reader, 2);
        number(&reader, 2);
        u32 offset = number(&reader, 4);
        if (offset >= codeBytes || (b->kind != 1 && b->kind != 2) ||
            (i && program_address(table[i - 1].segment, table[i - 1].offset) >= program_address(b->segment, b->offset)))
        {
            reader.error = 1;
            break;
        }
        b->entry = (TranslatedEntry)(void *)(code + offset);
    }
    if (reader.error)
    {
        result = 10;
        goto finish;
    }
    program.table = table;
    program.count = bindings;
    program.maximumTransfers = mode == 2
        ? 0xffffffffu
        : 1000000000;
    program.maximumDepth = 64;
    r.program = &program;
    r.resources = package_resources(&r, &reader, resources, state.ds);
    TaskResources *wep = package_resources(&r, &reader, utility, state.ds);
    if (reader.error || reader.p != reader.end)
    {
        result = 10;
        goto finish;
    }
    if (!local_init(&r, &local, state.ds, heapOffset, heapSize))
    {
        result = 20;
        goto finish;
    }
    r.local = &local;
    if (!__win16_install_windows_system(&r, state.ds) || !__win16_install_windows_files(&r) ||
        !__win16_install_windows_profiles(&r) ||
        !__win16_install_windows_ui(&r, &state, state.ds) || !__win16_install_modules(&r) ||
        !__win16_install_task(&r, state.ds, 1, state.bx, 0) || !__win16_install_wep(&r, wep))
    {
        result = 20;
        goto finish;
    }
    const ProgramBinding *start = program_lookup(&program, entry);
    if (!start || start->kind != 1 || entry != program_address(state.cs, state.ip))
    {
        result = 10;
        goto finish;
    }
    if (mode == 2)
    {
        /* Original relative data/audio paths belong to this task's module
           directory, independently of an Explorer/shell caller's current path. */
        u8 directory[260];
        u32 n = 0, last = 0;
        const u8 *module = r.system->modulePath;
        while (n < 259 && module[n])
        {
            directory[n] = module[n];
            if (module[n] == '\\' || module[n] == '/')
            {
                last = n;
            }
            n++;
        }
        if (!last)
        {
            result = 20;
            goto finish;
        }
        directory[last + 1] = 0;
        if (!package_SetCurrentDirectoryA(directory))
        {
            result = 20;
            goto finish;
        }
    }
    if (!package_trace_open(&r))
    {
        result = 60;
        goto finish;
    }
    if (mode == 2)
    {
        __win16_program_enter(&r, &state, entry);
        if (r.terminated && r.error == 16)
        {
            r.error = 0;
            result = (int)r.exitCode;
        }
        else
        {
            result = 30;
        }
        goto finish;
    }
    int failure = package_smoke(&r, r.resources, 0);
    if (failure)
    {
        result = 4000 + failure;
        goto finish;
    }
    failure = package_smoke(&r, wep, 1);
    if (failure)
    {
        result = 8000 + failure;
        goto finish;
    }
    /* 7.7 deliberately ends after package initialization/resource smoke.
       Original entry execution is the separately verified 7.8 lifecycle gate. */
finish:
    if (r.error && !result)
    {
        result = 30;
    }
    u32 executionError = r.error;
    destroy_runtime(&r);
    if (r.error && !result)
    {
        result = 50;
    }
    if (!package_trace_finish(&r, &state, result, executionError))
    {
        result = 60;
    }
    /* Catalog/table records are package-owned, not Win16 allocation handles. */
    while (heap.blocks)
    {
        heap_release(&heap, heap.blocks + 1);
    }
    if (heap.error && !result)
    {
        result = 50;
    }
    if (!native_HeapDestroy(heap.heap) && !result)
    {
        result = 50;
    }
    return result;
}
