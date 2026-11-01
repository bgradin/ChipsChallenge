typedef struct TaskWep
{
    TaskUi *ui;
    TaskResources *utility;
    iptr helpLibrary, helpProcedure, upperProcedure;
} TaskWep;
typedef ABI iptr (*NativeHelpCall)(iptr, iptr, iptr, iptr);
typedef ABI u32 (*NativeUpperCall)(u16 *, u32);

static int wep_contents(const u16 *text)
{
    const char *word = "CONTENTS";
    for (u32 i = 0; i < 9; i++)
    {
        u16 c = text
            ? text[i]
            : 0;
        if (c >= 'a' && c <= 'z')
        {
            c -= 32;
        }
        if (c != (u8)word[i])
        {
            return 0;
        }
        if (!c)
        {
            return 1;
        }
    }
    return 0;
}

static u32 wep_help(TaskWep *wep, UiArena *arena, iptr owner, u16 command, u32 guestData)
{
    TaskUi *ui = wep->ui;
    Runtime *r = ui->runtime;
    if (!r->system)
    {
        return 0;
    }
    const u8 *module = r->system->modulePath;
    u32 length = system_path_length(r, module);
    if (length > 65500)
    {
        return 0;
    }
    u8 *path = ui_alloc(arena, length + 96);
    if (!path)
    {
        return 0;
    }
    for (u32 i = 0; i < length; i++)
    {
        path[i] = module[i];
    }
    u32 at = length > 4 && module[length - 4] == '.'
        ? length - 4
        : length;
    path[at] = '.';
    path[at + 1] = 'C';
    path[at + 2] = 'H';
    path[at + 3] = 'M';
    path[at + 4] = 0;
    u16 *wide = ui_wide_bytes(arena, path, at + 4);
    if ((u32)UI_CALL(ui, GetFileAttributesW, (iptr)wide) != 0xffffffffu)
    {
        if (!wep->helpLibrary)
        {
            static const u16 library[] = {'h', 'h', 'c', 't', 'r', 'l', '.', 'o', 'c', 'x', 0};
            wep->helpLibrary = UI_CALL(ui, LoadLibraryExW, (iptr)library, 0, 0x800);
            if (wep->helpLibrary)
            {
                wep->helpProcedure =
                    UI_CALL(ui, GetProcAddress, wep->helpLibrary, (iptr) "HtmlHelpW");
            }
        }
        if ((unsigned long long)wep->helpProcedure > 65535)
        {
            NativeHelpCall help = (NativeHelpCall)(void *)wep->helpProcedure;
            if (command == 2)
            {
                help(0, 0, 0x12, 0);
                return 1;
            }
            if (command == 4)
            {
                static const u16 title[] = {'U', 's', 'i', 'n', 'g', ' ', 'H', 'e', 'l', 'p', 0};
                static const u16 guide[] = {
                    'U', 's', 'e', ' ', 'C', 'o', 'n', 't', 'e', 'n', 't', 's', ' ', 't', 'o',
                    ' ', 'b', 'r', 'o', 'w', 's', 'e', ' ', 't', 'o', 'p', 'i', 'c', 's', ',',
                    ' ', 'I', 'n', 'd', 'e', 'x', ' ', 't', 'o', ' ', 'f', 'i', 'n', 'd', ' ',
                    'k', 'e', 'y', 'w', 'o', 'r', 'd', 's', ',', ' ', 'a', 'n', 'd', ' ', 'B',
                    'a', 'c', 'k', ' ', 't', 'o', ' ', 'r', 'e', 't', 'u', 'r', 'n', '.', '\n',
                    'C', 'l', 'i', 'c', 'k', ' ', 'a', ' ', 'l', 'i', 'n', 'k', ' ', 't', 'o',
                    ' ', 'o', 'p', 'e', 'n', ' ', 'i', 't', 's', ' ', 't', 'o', 'p', 'i', 'c',
                    '.', ' ', 'U', 's', 'e', ' ', 'P', 'r', 'i', 'n', 't', ' ', 't', 'o', ' ',
                    'p', 'r', 'i', 'n', 't', ' ', 't', 'h', 'e', ' ', 'c', 'u', 'r', 'r', 'e',
                    'n', 't', ' ', 't', 'o', 'p', 'i', 'c', '.', 0};
                UI_CALL(ui, MessageBoxW, owner, (iptr)guide, (iptr)title, 0x40);
                return 1;
            }
            if (command == 0x101 || command == 0x102 || command == 0x105)
            {
                u16 *keyword = ui_wide(arena, guestData, 0);
                if (r->error)
                {
                    return 0;
                }
                if (!keyword[0])
                {
                    return !!help(owner, (iptr)wide, 2, 0);
                }
                if (wep_contents(keyword))
                {
                    return !!help(owner, (iptr)wide, 0, 0);
                }
                if (!wep->upperProcedure)
                {
                    static const u16 library[] = {'u', 's', 'e', 'r', '3', '2',
                                                  '.', 'd', 'l', 'l', 0};
                    iptr user = UI_CALL(ui, GetModuleHandleW, (iptr)library);
                    if (user)
                    {
                        wep->upperProcedure =
                            UI_CALL(ui, GetProcAddress, user, (iptr) "CharUpperBuffW");
                    }
                }
                if ((unsigned long long)wep->upperProcedure <= 65535)
                {
                    return 0;
                }
                u32 count = 0;
                while (keyword[count])
                {
                    count++;
                }
                ((NativeUpperCall)(void *)wep->upperProcedure)(keyword, count);
                unsigned long long hash = 14695981039346656037ULL;
                for (u32 i = 0; i < count; i++)
                {
                    hash = (hash ^ (u8)keyword[i]) * 1099511628211ULL;
                    hash = (hash ^ (u8)(keyword[i] >> 8)) * 1099511628211ULL;
                }
                const char *prefix = "::/keywords/k";
                u32 n = 0;
                while (prefix[n])
                {
                    path[at + 4 + n] = (u8)prefix[n];
                    n++;
                }
                const char *hex = "0123456789abcdef";
                for (int shift = 60; shift >= 0; shift -= 4)
                {
                    path[at + 4 + n++] = (u8)hex[(hash >> shift) & 15];
                }
                const char *suffix = ".htm";
                for (u32 i = 0; suffix[i]; i++)
                {
                    path[at + 4 + n++] = (u8)suffix[i];
                }
                path[at + 4 + n] = 0;
                wide = ui_wide_bytes(arena, path, at + 4 + n);
                return !!help(owner, (iptr)wide, 0, 0);
            }
            if (command == 1 || command == 8)
            {
                const char *prefix = "::/html/topic";
                u32 n = 0;
                while (prefix[n])
                {
                    path[at + 4 + n] = (u8)prefix[n];
                    n++;
                }
                u8 digits[10];
                u32 count = 0, value = guestData;
                do
                {
                    digits[count++] = (u8)('0' + value % 10);
                    value /= 10;
                } while (value);
                while (count)
                {
                    path[at + 4 + n++] = digits[--count];
                }
                const char *suffix = ".htm";
                for (u32 i = 0; suffix[i]; i++)
                {
                    path[at + 4 + n++] = (u8)suffix[i];
                }
                path[at + 4 + n] = 0;
                wide = ui_wide_bytes(arena, path, at + 4 + n);
                return !!help(owner, (iptr)wide, 0, 0);
            }
            if (command == 3 || command == 0)
            {
                return !!help(owner, (iptr)wide, 0, 0);
            }
            if (command == 0xb)
            {
                return !!help(owner, (iptr)wide, 1, 0);
            }
            return 0; /* Unsupported requests must not silently select an unrelated topic. */
        }
    }
    path[at + 1] = 'H';
    path[at + 2] = 'L';
    path[at + 3] = 'P';
    path[at + 4] = 0;
    wide = ui_wide_bytes(arena, path, at + 4);
    iptr data =
        command == 0x101 || command == 0x102
            ? (iptr)ui_wide(arena, guestData, 0)
            : guestData;
    return r->error
        ? 0
        : (u32)UI_CALL(ui, WinHelpW, owner, (iptr)wide, command, data);
}
typedef struct WepAbout
{
    TaskWep *wep;
    u16 *caption, *title, *description, *copyright;
    iptr icon;
} WepAbout;

static TaskResource *wep_resource(TaskResources *catalog, u16 type, u16 id)
{
    if (catalog)
    {
        for (u32 i = 0; i < catalog->count; i++)
        {
            if (catalog->entries[i].type == type && catalog->entries[i].id == id &&
                !catalog->entries[i].name)
            {
                return catalog->entries + i;
            }
        }
    }
    return 0;
}

static const u8 *wep_string(Runtime *r, TaskResources *catalog, u16 id, u32 *length)
{
    TaskResource *entry = wep_resource(catalog, 6, (id >> 4) + 1);
    static const u8 empty[] = {0};
    *length = 0;
    if (!entry)
    {
        return empty;
    }
    u32 at = 0;
    for (u16 i = 0; i <= id % 16; i++)
    {
        if (at >= entry->length)
        {
            r->error = 9;
            return empty;
        }
        u32 n = entry->bytes[at++];
        if (n > entry->length - at)
        {
            r->error = 9;
            return empty;
        }
        if (i == id % 16)
        {
            *length = n;
            return entry->bytes + at;
        }
        at += n;
    }
    return empty;
}

static u16 *wep_wide_string(TaskWep *wep, UiArena *arena, TaskResources *catalog, u16 id)
{
    u32 length = 0;
    const u8 *text = wep_string(wep->ui->runtime, catalog, id, &length);
    return ui_wide_bytes(arena, text, length);
}

static void wep_center(TaskUi *ui, iptr window)
{
    int rect[4] = {0};
    if (!UI_CALL(ui, GetWindowRect, window, (iptr)rect))
    {
        return;
    }
    short width = (short)((int)UI_CALL(ui, GetSystemMetrics, 0) - rect[2] + rect[0]),
          height = (short)((int)UI_CALL(ui, GetSystemMetrics, 1) - rect[3] + rect[1]);
    width = (short)((u16)width & 0xfff1);
    UI_CALL(ui, SetWindowPos, window, 0, width >> 1, height / 3, 0, 0, 0x11);
}

static u32 wep_gray(TaskUi *ui, iptr window, u16 message, u16 dcId, u32 lparam)
{
    if (message == 0x19)
    {
        u16 type = (u16)(lparam >> 16);
        if (type == 1 || type == 6)
        {
            iptr dc = ui_handle(ui, dcId, 6);
            if (ui->runtime->error)
            {
                return 0;
            }
            UI_CALL(ui, SetBkColor, dc, 0xc0c0c0);
            UI_CALL(ui, SetBkMode, dc, 1);
        }
        return type == 0 || type == 1 || type == 4 || type == 6
            ? ui_handle_add(ui, UI_CALL(ui, GetStockObject, 1), 7, 0, 0)
            : 0;
    }
    if (message != 0xf)
    {
        return 0;
    }
    iptr dc = UI_CALL(ui, GetDC, window);
    if (!dc)
    {
        return 0;
    }
    int rect[4] = {0};
    iptr old = 0;
    if (UI_CALL(ui, GetClientRect, window, (iptr)rect))
    {
        int l = rect[0], t = rect[1], right = rect[2], b = rect[3];
        old = UI_CALL(ui, SelectObject, dc, UI_CALL(ui, GetStockObject, 0));
        UI_CALL(ui, PatBlt, dc, l, t, l + 2, b - 1, 0xf00021);
        UI_CALL(ui, SelectObject, dc, UI_CALL(ui, GetStockObject, 2));
        UI_CALL(ui, PatBlt, dc, l + 1, b - 2, right, b - 1, 0xf00021);
        UI_CALL(ui, PatBlt, dc, l, b - 2, right, b, 0xf00021);
        UI_CALL(ui, PatBlt, dc, right - 2, t, right, b, 0xf00021);
        rect[0] = l;
        rect[1] = b - 2;
        UI_CALL(ui, ValidateRect, window, (iptr)rect);
        rect[0] = right - 1;
        rect[1] = 0;
        UI_CALL(ui, ValidateRect, window, (iptr)rect);
        rect[0] = 0;
        rect[2] = 1;
        UI_CALL(ui, ValidateRect, window, (iptr)rect);
    }
    if (old)
    {
        UI_CALL(ui, SelectObject, dc, old);
    }
    UI_CALL(ui, ReleaseDC, window, dc);
    return 0;
}

static iptr wep_about_callback(WepAbout *about, iptr window, u32 message, iptr wp, iptr lp)
{
    TaskUi *ui = about->wep->ui;
    if (message == 0x110)
    {
        UI_CALL(ui, SetWindowTextW, window, (iptr)about->caption);
        UI_CALL(ui, SetDlgItemTextW, window, 702, (iptr)about->title);
        UI_CALL(ui, SetDlgItemTextW, window, 703, (iptr)about->description);
        UI_CALL(ui, SetDlgItemTextW, window, 704, (iptr)about->copyright);
        if (about->icon)
        {
            UI_CALL(ui, SendDlgItemMessageW, window, 701, 0x170, about->icon, 0);
        }
        wep_center(ui, window);
        return 1;
    }
    if (message == 0x111 && ((u16)wp == 1 || (u16)wp == 2))
    {
        UI_CALL(ui, EndDialog, window, (u16)wp);
        return 1;
    }
    if (message == 0x10)
    {
        UI_CALL(ui, EndDialog, window, 2);
        return 1;
    }
    if (message >= 0x132 && message <= 0x138)
    {
        u32 id =
            wep_gray(ui, window, 0x19, ui_handle_add(ui, wp, 6, 0, 0), (message - 0x132) << 16);
        return ui_handle(ui, id, 7);
    }
    if (message == 0xf)
    {
        wep_gray(ui, window, 0xf, 0, 0);
    }
    return 0;
}

ABI NOINLINE int __win16_install_wep(Runtime *r, TaskResources *utility)
{
    if (r->wep || !r->gui || !r->gui->ui || !utility)
    {
        return 0;
    }
    TaskWep *wep = r->alloc(r->storage, sizeof(*wep));
    if (!wep)
    {
        return 0;
    }
    zero(wep, sizeof(*wep));
    wep->ui = r->gui->ui;
    wep->utility = utility;
    r->wep = wep;
    return 1;
}

ABI NOINLINE u16 __win16_wep_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    TaskWep *wep = r->wep;
    const UiContract *c = ui_contract(4, ordinal);
    u32 a[12] = {0};
    if (!wep || !ui_read_arguments(r, s, c, a))
    {
        if (!r->error)
        {
            r->error = 14;
        }
        return 0;
    }
    TaskUi *ui = wep->ui;
    UiArena arena = {r, 0};
    u32 result = 0;
    if (ordinal == 2)
    {
        result = (a[0] & 0xff00) <= 0x220;
        if (!result && !(a[0] & 1))
        {
            u16 *text = wep_wide_string(wep, &arena, wep->utility, 911);
            if (!r->error)
            {
                UI_CALL(ui, MessageBoxW, 0, (iptr)text, 0, 0x30);
            }
        }
    }
    else if (ordinal == 103)
    {
        wep_center(ui, ui_handle(ui, a[0], 5));
    }
    else if (ordinal == 1202)
    {
        result = wep_gray(ui, ui_handle(ui, a[0], 5), (u16)a[1], (u16)a[2], a[3]);
    }
    else if (ordinal == 5)
    {
        if (a[0] == ui->instance)
        {
            result = wep_help(wep, &arena, ui_handle(ui, a[1], 5), (u16)a[2], a[3]);
        }
    }
    else if (ordinal == 4)
    {
        if (a[0] == ui->instance)
        {
            TaskResource *template = wep_resource(wep->utility, 5, 100);
            if (!template)
            {
                r->error = 14;
                return 0;
            }
            WepAbout about = {0};
            about.wep = wep;
            about.title = wep_wide_string(wep, &arena, r->resources, 257);
            about.description = wep_wide_string(wep, &arena, r->resources, 258);
            about.copyright = wep_wide_string(wep, &arena, r->resources, 259);
            if (about.copyright && !about.copyright[0])
            {
                about.copyright = wep_wide_string(wep, &arena, wep->utility, 259);
            }
            u32 formatLength = 0, titleLength = 0;
            const u8 *format = wep_string(r, wep->utility, 400, &formatLength),
                     *title = wep_string(r, r->resources, 257, &titleLength);
            u8 *caption = ui_alloc(&arena, formatLength * (titleLength + 1) + 1);
            u32 used = 0;
            if (caption)
            {
                for (u32 i = 0; i < formatLength; i++)
                {
                    if (format[i] == '@' && i + 1 < formatLength && format[i + 1] == '0')
                    {
                        for (u32 n = 0; n < titleLength; n++)
                        {
                            caption[used++] = title[n];
                        }
                        i++;
                    }
                    else
                    {
                        caption[used++] = format[i];
                    }
                }
            }
            about.caption = caption
                ? ui_wide_bytes(&arena, caption, used)
                : 0;
            TaskResource *group = wep_resource(r->resources, 14, 256);
            if (group && group->length >= 20)
            {
                TaskResource *icon = wep_resource(r->resources, 3, ui_get16(group->bytes + 18));
                if (icon)
                {
                    about.icon = UI_CALL(ui, CreateIconFromResourceEx, (iptr)icon->bytes,
                                         icon->length, 1, 0x30000, 0, 0, 0);
                }
            }
            u32 bytes = 0;
            u8 *data = ui_dialog_template(r, &arena, template->bytes, template->length, &bytes);
            if (!r->error)
            {
                UI_CALL(ui, DialogBoxIndirectParamW, UI_CALL(ui, GetModuleHandleW, 0), (iptr)data,
                        ui_handle(ui, a[1], 5), ui->aboutProcedure, (iptr)&about);
            }
            if (about.icon)
            {
                UI_CALL(ui, DestroyIcon, about.icon);
            }
        }
    }
    else
    {
        r->error = 14;
    }
    ui_destroy_arena(&arena);
    if (!r->error && (ordinal == 2 || ordinal == 5 || ordinal == 1202))
    {
        s->ax = (u16)result;
    }
    return c->cleanup;
}

static void destroy_task_wep(Runtime *r)
{
    if (r->wep)
    {
        TaskWep *w = r->wep;
        if ((unsigned long long)w->helpProcedure > 65535)
        {
            ((NativeHelpCall)(void *)w->helpProcedure)(0, 0, 0x12, 0);
        }
        if (w->helpLibrary)
        {
            UI_CALL(w->ui, FreeLibrary, w->helpLibrary);
        }
        r->release(r->storage, r->wep);
        r->wep = 0;
    }
}
