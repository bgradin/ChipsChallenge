static TaskResource *ui_resource(TaskUi *ui, u16 type, u32 name)
{
    Runtime *r = ui->runtime;
    TaskResources *catalog = r->resources;
    if (!catalog)
    {
        return 0;
    }
    for (u32 i = 0; i < catalog->count; i++)
    {
        TaskResource *entry = catalog->entries + i;
        if (entry->type == type && resource_match(r, name, entry->id, entry->name))
        {
            return entry;
        }
        if (r->error)
        {
            return 0;
        }
    }
    return 0;
}
typedef struct
{
    Runtime *r;
    const u8 *source;
    u32 size, at;
    u8 *dest;
    u32 capacity, used;
} UiResourceReader;

static u8 ui_resource_byte(UiResourceReader *p)
{
    if (p->at >= p->size)
    {
        p->r->error = 9;
        return 0;
    }
    return p->source[p->at++];
}

static u16 ui_resource_word(UiResourceReader *p)
{
    u16 lo = ui_resource_byte(p);
    return lo | (u16)ui_resource_byte(p) << 8;
}

static u32 ui_resource_dword(UiResourceReader *p)
{
    u32 lo = ui_resource_word(p);
    return lo | (u32)ui_resource_word(p) << 16;
}

static void ui_resource_out(UiResourceReader *p, u16 value)
{
    if (p->used > p->capacity || p->capacity - p->used < 2)
    {
        p->r->error = 9;
        return;
    }
    ui_put16(p->dest + p->used, value);
    p->used += 2;
}

static void ui_resource_long(UiResourceReader *p, u32 value)
{
    ui_resource_out(p, (u16)value);
    ui_resource_out(p, (u16)(value >> 16));
}

static u16 *ui_resource_text(UiResourceReader *p, UiArena *arena)
{
    u32 start = p->at;
    while (!p->r->error && ui_resource_byte(p))
    {
    }
    return p->r->error
        ? 0
        : ui_wide_bytes(arena, p->source + start, p->at - start - 1);
}

static void ui_dialog_name(UiResourceReader *p, int classByte)
{
    if (p->at >= p->size)
    {
        p->r->error = 9;
        return;
    }
    u8 next = p->source[p->at];
    if (next == 0xff)
    {
        p->at++;
        ui_resource_out(p, 0xffff);
        ui_resource_out(p, ui_resource_word(p));
    }
    else if (classByte && (next & 0x80))
    {
        p->at++;
        ui_resource_out(p, 0xffff);
        ui_resource_out(p, next);
    }
    else
    {
        u8 ch;
        do
        {
            ch = ui_resource_byte(p);
            ui_resource_out(p, ui_cp1252(ch));
        } while (ch && !p->r->error);
    }
}

static u8 *ui_dialog_template(Runtime *r, UiArena *arena, const u8 *source, u32 length,
                              u32 *resultBytes)
{
    if (length > 1024 * 1024)
    {
        r->error = 9;
        return 0;
    }
    u32 cap = length * 4 + 128;
    u8 *dest = ui_alloc(arena, cap);
    if (!dest)
    {
        return 0;
    }
    UiResourceReader p = {r, source, length, 0, dest, cap, 0};
    u32 style = ui_resource_dword(&p);
    u16 count = ui_resource_byte(&p);
    ui_resource_long(&p, style);
    ui_resource_long(&p, 0);
    ui_resource_out(&p, count);
    for (u32 i = 0; i < 4; i++)
    {
        ui_resource_out(&p, ui_resource_word(&p));
    }
    ui_dialog_name(&p, 0);
    ui_dialog_name(&p, 0);
    ui_dialog_name(&p, 0);
    if (style & 0x40)
    {
        ui_resource_out(&p, ui_resource_word(&p));
        ui_dialog_name(&p, 0);
    }
    for (u32 i = 0; i < count && !r->error; i++)
    {
        u16 coords[4];
        for (u32 n = 0; n < 4; n++)
        {
            coords[n] = ui_resource_word(&p);
        }
        u16 id = ui_resource_word(&p);
        u32 controlStyle = ui_resource_dword(&p);
        while (p.used & 3)
        {
            if (p.used >= p.capacity)
            {
                r->error = 9;
                return 0;
            }
            p.dest[p.used++] = 0;
        }
        ui_resource_long(&p, controlStyle);
        ui_resource_long(&p, 0);
        for (u32 n = 0; n < 4; n++)
        {
            ui_resource_out(&p, coords[n]);
        }
        ui_resource_out(&p, id);
        ui_dialog_name(&p, 1);
        ui_dialog_name(&p, 0);
        u16 extra = ui_resource_byte(&p);
        ui_resource_out(&p, extra
            ? extra + 2
            : 0);
        for (u32 n = 0; n < extra && !r->error; n++)
        {
            if (p.used >= p.capacity)
            {
                r->error = 9;
                break;
            }
            p.dest[p.used++] = ui_resource_byte(&p);
        }
    }
    if (r->error)
    {
        return 0;
    }
    *resultBytes = p.used;
    return dest;
}

static iptr ui_menu_build(TaskUi *ui, UiArena *arena, UiResourceReader *reader, int popup,
                          u32 depth)
{
    Runtime *r = ui->runtime;
    if (depth > 16)
    {
        r->error = 9;
        return 0;
    }
    iptr menu = popup
        ? UI_CALL(ui, CreatePopupMenu)
        : UI_CALL(ui, CreateMenu);
    if (!menu)
    {
        return 0;
    }
    for (u32 n = 0; n < 4096 && !r->error; n++)
    {
        u16 flags = ui_resource_word(reader), id = 0;
        int child = !!(flags & 16);
        if (!child)
        {
            id = ui_resource_word(reader);
        }
        u16 *text = ui_resource_text(reader, arena);
        if (r->error)
        {
            break;
        }
        iptr item = child
            ? ui_menu_build(ui, arena, reader, 1, depth + 1)
            : id;
        if (r->error || (child && !item))
        {
            break;
        }
        if (!child && !id && text && !text[0])
        {
            flags |= 0x800;
        }
        /* Strip the template end marker, retaining MF_SEPARATOR (0x800). */
        if (!UI_CALL(ui, AppendMenuW, menu, flags & ~0x80, item, (iptr)text))
        {
            if (child)
            {
                UI_CALL(ui, DestroyMenu, item);
            }
            break;
        }
        if (flags & 0x80)
        {
            return menu;
        }
    }
    UI_CALL(ui, DestroyMenu, menu);
    return 0;
}

static u16 ui_load_menu(TaskUi *ui, UiArena *arena, u32 name)
{
    TaskResource *resource = ui_resource(ui, 4, name);
    if (!resource)
    {
        return 0;
    }
    Runtime *r = ui->runtime;
    UiResourceReader p = {r, resource->bytes, resource->length, 0, 0, 0, 0};
    u16 version = ui_resource_word(&p), extra = ui_resource_word(&p);
    p.at += extra;
    if (version)
    {
        r->error = 14;
        return 0;
    }
    iptr menu = ui_menu_build(ui, arena, &p, 0, 0);
    u16 result = ui_handle_add(ui, menu, 8, 1, 0);
    if (menu && !result)
    {
        UI_CALL(ui, DestroyMenu, menu);
    }
    return result;
}

static u32 ui_gui_resource(TaskUi *ui, UiArena *arena, u16 ordinal, const u32 *a)
{
    Runtime *r = ui->runtime;
    iptr native = 0;
    u16 kind = 7, owned = 1;
    TaskResource *resource = 0;
    if (!a[0] && (ordinal == 173 || ordinal == 174))
    {
        kind = ordinal == 173
            ? 9
            : 10;
        owned = 0;
        u16 *name = ui_wide(arena, a[1], 1);
        if (!r->error)
        {
            native = ordinal == 173
                ? UI_CALL(ui, LoadCursorW, 0, (iptr)name)
                : UI_CALL(ui, LoadIconW, 0, (iptr)name);
        }
    }
    else if (a[0] && a[0] != ui->instance)
    {
        return 0;
    }
    else if (ordinal == 150)
    {
        return ui_load_menu(ui, arena, a[1]);
    }
    else if (ordinal == 175)
    {
        resource = ui_resource(ui, 2, a[1]);
        if (!resource)
        {
            return 0;
        }
        const u8 *data = resource->bytes;
        if (resource->length < 40 || ui_get32(data) != 40)
        {
            r->error = 14;
            return 0;
        }
        u32 depth = ui_get16(data + 14), compression = ui_get32(data + 16),
            colors = ui_get32(data + 32);
        if (!colors && depth <= 8)
        {
            colors = 1u << depth;
        }
        if (depth > 32 || colors > 256)
        {
            r->error = 14;
            return 0;
        }
        u32 bits = 40 + colors * 4 + (compression == 3
            ? 12
            : 0);
        if (bits >= resource->length)
        {
            r->error = 9;
            return 0;
        }
        iptr dc = UI_CALL(ui, GetDC, 0);
        native = UI_CALL(ui, CreateDIBitmap, dc, (iptr)data, 4, (iptr)(data + bits), (iptr)data, 0);
        if (dc)
        {
            UI_CALL(ui, ReleaseDC, 0, dc);
        }
    }
    else if (ordinal == 177)
    {
        kind = 11;
        resource = ui_resource(ui, 9, a[1]);
        if (!resource)
        {
            return 0;
        }
        u32 count = 0;
        while (count < resource->length / 5 && count < 4096)
        {
            u8 flags = resource->bytes[count * 5];
            count++;
            if (flags & 0x80)
            {
                break;
            }
        }
        if (!count)
        {
            return 0;
        }
        u8 *accel = ui_alloc(arena, count * 6);
        if (!accel)
        {
            return 0;
        }
        for (u32 i = 0; i < count; i++)
        {
            const u8 *row = resource->bytes + i * 5;
            accel[i * 6] = row[0] & 0x7f;
            ui_put16(accel + i * 6 + 2, ui_get16(row + 1));
            ui_put16(accel + i * 6 + 4, ui_get16(row + 3));
        }
        native = UI_CALL(ui, CreateAcceleratorTableW, (iptr)accel, count);
    }
    else if (ordinal == 173 || ordinal == 174)
    {
        int icon = ordinal == 174;
        kind = icon
            ? 10
            : 9;
        TaskResource *group = ui_resource(ui, icon
            ? 14
            : 12, a[1]);
        if (!group)
        {
            return 0;
        }
        if (group->length < 20)
        {
            r->error = 9;
            return 0;
        }
        u16 id = ui_get16(group->bytes + 18);
        resource = ui_resource(ui, icon
            ? 3
            : 1, id);
        if (!resource)
        {
            return 0;
        }
        native = UI_CALL(ui, CreateIconFromResourceEx, (iptr)resource->bytes, resource->length,
                         icon, 0x30000, 0, 0, 0);
    }
    else
    {
        r->error = 14;
        return 0;
    }
    u16 result = ui_handle_add(ui, native, kind, owned, 0);
    if (native && !result && owned)
    {
        if (kind == 7)
        {
            UI_CALL(ui, DeleteObject, native);
        }
        else if (kind == 9)
        {
            UI_CALL(ui, DestroyCursor, native);
        }
        else if (kind == 10)
        {
            UI_CALL(ui, DestroyIcon, native);
        }
        else if (kind == 11)
        {
            UI_CALL(ui, DestroyAcceleratorTable, native);
        }
    }
    return result;
}

ABI NOINLINE u16 __win16_ui_resource(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const UiContract *c = ui_contract(2, ordinal);
    u32 args[12] = {0};
    TaskUi *ui = ui_context(r, s);
    if (!ui || !ui_read_arguments(r, s, c, args))
    {
        return 0;
    }
    UiArena arena = {r, 0};
    u32 result = ui_gui_resource(ui, &arena, ordinal, args);
    ui_destroy_arena(&arena);
    if (!r->error)
    {
        s->ax = (u16)result;
    }
    return c->cleanup;
}
