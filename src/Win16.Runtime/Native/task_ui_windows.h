typedef struct UiClass
{
    struct UiClass *next;
    u16 *name;
    u32 target, menu;
    u16 extra, instance;
} UiClass;
typedef struct UiWindow
{
    struct UiWindow *next;
    TaskUi *ui;
    UiClass *cls;
    iptr native;
    u32 arguments[11], target;
    u16 id, dialog, extra;
    u16 *words;
} UiWindow;
typedef struct UiPaint
{
    struct UiPaint *next;
    iptr window;
    u32 pointer;
    u16 dc;
    u8 native[72];
} UiPaint;
typedef struct UiDc
{
    struct UiDc *next;
    iptr window, native;
    u16 id;
    u32 uses;
} UiDc;
typedef struct UiTimer
{
    struct UiTimer *next;
    iptr window, native;
    u32 target;
    u16 id;
} UiTimer;
typedef struct UiMessage
{
    struct UiMessage *previous;
    iptr window, wparam, lparam;
    u32 message, guestLparam;
    u16 guestMessage, guestWparam;
    int defaultCalled;
} UiMessage;

static u16 ui_window_id(TaskUi *ui, iptr native)
{
    return ui_handle_add(ui, native, 5, 0, 0);
}

static UiWindow *ui_window(TaskUi *ui, iptr native)
{
    for (UiWindow *w = ui->windows; w; w = w->next)
    {
        if (w->native == native)
        {
            return w;
        }
    }
    return 0;
}

static int ui_wide_equal(const u16 *a, const u16 *b)
{
    for (u32 i = 0; i < 65536; i++)
    {
        u16 x = a[i], y = b[i];
        if (x >= 'a' && x <= 'z')
        {
            x -= 32;
        }
        if (y >= 'a' && y <= 'z')
        {
            y -= 32;
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

static u32 ui_reverse(TaskUi *ui, u32 target, const u16 *arguments, u16 count, int dialog)
{
    Runtime *r = ui->runtime;
    if (++ui->depth > 32)
    {
        ui->depth--;
        r->error = 8;
        return 0;
    }
    u32 result = task_callback_arguments(r, ui->state, target, 1, 8192, 0, 0, arguments, count,
                                         dialog
                                             ? 1
                                             : 2);
    ui->depth--;
    return result;
}
static u16 mm_guest_device(Runtime *, u32);

static void ui_to_guest(TaskUi *ui, UiMessage *m)
{
    m->guestMessage = (u16)m->message;
    m->guestWparam = (u16)m->wparam;
    m->guestLparam = (u32)m->lparam;
    if (m->message == 0x111)
    {
        m->guestLparam = (u32)ui_window_id(ui, m->lparam) |
            ((u32)((unsigned long long)m->wparam >> 16) & 65535) << 16;
    }
    else if (m->message >= 0x132 && m->message <= 0x138)
    {
        m->guestMessage = 0x19;
        m->guestWparam = ui_handle_add(ui, m->wparam, 6, 0, 0);
        m->guestLparam = ui_window_id(ui, m->lparam) | (m->message - 0x132) << 16;
    }
    else if (m->message == 6)
    {
        m->guestLparam = ui_window_id(ui, m->lparam) |
            ((u32)((unsigned long long)m->wparam >> 16) & 65535) << 16;
    }
    else if (m->message == 7 || m->message == 8 || m->message == 0x20 || m->message == 0x21 ||
             m->message == 0x110)
    {
        m->guestWparam = ui_window_id(ui, m->wparam);
    }
    else if (m->message == 0x116 || m->message == 0x117)
    {
        m->guestWparam = ui_handle_add(ui, m->wparam, 8, 0, 0);
    }
    else if (m->message == 0x11f)
    {
        m->guestLparam = ui_handle_add(ui, m->lparam, 8, 0, 0) |
            ((u32)((unsigned long long)m->wparam >> 16) & 65535) << 16;
    }
    else if (m->message == 0x114 || m->message == 0x115)
    {
        m->guestLparam =
            (u16)((unsigned long long)m->wparam >> 16) | (u32)ui_window_id(ui, m->lparam) << 16;
    }
    else if (m->message == 0x113 && m->lparam)
    {
        m->guestLparam = 0;
        for (UiTimer *t = ui->timers; t; t = t->next)
        {
            if (t->window == m->window && t->id == (u16)m->wparam)
            {
                m->guestLparam = t->target;
                break;
            }
        }
    }
    else if (m->message == 0x3b9)
    {
        m->guestLparam = mm_guest_device(ui->runtime, (u32)m->lparam);
    }
    else if (m->message == 0x30)
    {
        m->guestWparam = ui_handle_add(ui, m->wparam, 7, 0, 0);
    }
}

static void ui_forget_window(UiWindow *window)
{
    TaskUi *ui = window->ui;
    Runtime *r = ui->runtime;
    gui_forget(r, r->gui, window->id);
    window->native = 0;
    window->id = 0;
    /* Window metadata is retired until task disposal: a nested callback may still hold it. */
}

static iptr ui_window_callback(UiWindow *window, iptr native, u32 message, iptr wparam, iptr lparam)
{
    TaskUi *ui = window->ui;
    Runtime *r = ui->runtime;
    if (r->terminated)
    {
        return window->dialog
            ? 0
            : UI_CALL(ui, DefWindowProcW, native, message, wparam, lparam);
    }
    if (r->error)
    {
        return 0;
    }
    window->native = native;
    if (!window->id)
    {
        window->id = ui_window_id(ui, native);
    }
    UiMessage m = {0};
    m.window = native;
    m.message = message;
    m.wparam = wparam;
    m.lparam = lparam;
    ui_to_guest(ui, &m);
    u16 lease = 0;
    u32 scratch = 0, structureBytes = 0;
    if (message == 1 || message == 0x81)
    {
        structureBytes = 34;
        lease = local_allocate(r, r->local, structureBytes, 0x40);
        if (!lease)
        {
            r->error = 8;
            return 0;
        }
        scratch = (u32)ui->instance << 16 | lease;
        u8 *p = ui_buffer(r, scratch, 34, 1);
        if (!p)
        {
            return 0;
        }
        const u32 *a = window->arguments;
        ui_put32(p, a[10]);
        ui_put16(p + 4, (u16)a[9]);
        ui_put16(p + 6, (u16)a[8]);
        ui_put16(p + 8, (u16)a[7]);
        ui_put16(p + 10, (u16)a[6]);
        ui_put16(p + 12, (u16)a[5]);
        ui_put16(p + 14, (u16)a[4]);
        ui_put16(p + 16, (u16)a[3]);
        ui_put32(p + 18, a[2]);
        ui_put32(p + 22, a[1]);
        ui_put32(p + 26, a[0]);
        ui_put32(p + 30, 0);
        m.guestLparam = scratch;
    }
    else if ((message == 0xc || message == 0x1a) && lparam)
    {
        u16 *text = (u16 *)lparam;
        u32 length = 0;
        while (length < 4096 && text[length])
        {
            length++;
        }
        if (length >= 4096)
        {
            r->error = 9;
            return 0;
        }
        lease = local_allocate(r, r->local, length + 1, 0x40);
        if (!lease)
        {
            r->error = 8;
            return 0;
        }
        scratch = (u32)ui->instance << 16 | lease;
        u8 *p = ui_buffer(r, scratch, length + 1, 1);
        if (!p)
        {
            return 0;
        }
        for (u32 i = 0; i < length; i++)
        {
            p[i] = ui_ansi(r, text[i]);
        }
        p[length] = 0;
        m.guestLparam = scratch;
    }
    else if (lparam && (message == 0x24 || message == 0x46 || message == 0x47 || message == 0x83 || message == 0x7c || message == 0x7d))
    {
        structureBytes = message == 0x24
            ? 20
            : message == 0x46 || message == 0x47
                    ? 14
                    : message == 0x83
                        ? (wparam
                            ? 28
                            : 8)
                        : 8;
        lease = local_allocate(r, r->local, structureBytes, 0x40);
        if (!lease)
        {
            r->error = 8;
            return 0;
        }
        scratch = (u32)ui->instance << 16 | lease;
        u8 *dest = ui_buffer(r, scratch, structureBytes, 1), *source = (u8 *)lparam;
        if (!dest)
        {
            return 0;
        }
        if (message == 0x24)
        {
            for (u32 i = 0; i < 10; i++)
            {
                ui_put16(dest + i * 2, (u16)ui_get32(source + i * 4));
            }
        }
        else if (message == 0x46 || message == 0x47)
        {
            ui_put16(dest, ui_window_id(ui, *(iptr *)source));
            iptr after = *(iptr *)(source + 8);
            ui_put16(dest + 2, after == 0 || after == -1 || after == -2
                ? (u16)after
                : ui_window_id(ui, after));
            for (u32 i = 0; i < 4; i++)
            {
                ui_put16(dest + 4 + i * 2, (u16)ui_get32(source + 16 + i * 4));
            }
            ui_put16(dest + 12, (u16)ui_get32(source + 32));
        }
        else if (message == 0x83)
        {
            for (u32 i = 0; i < (wparam
                ? 12
                : 4);
                 i++)
            {
                ui_put16(dest + i * 2, (u16)ui_get32(source + i * 4));
            }
            if (wparam)
            {
                ui_put32(dest + 24, 0);
            }
        }
        else
        {
            for (u32 i = 0; i < 8; i++)
            {
                dest[i] = source[i];
            }
        }
        m.guestLparam = scratch;
    }
    if (r->observe)
    {
        r->observe(r, ui->state, 5u | ((u32)m.guestMessage << 16), window->target);
    }
    m.previous = ui->message;
    ui->message = &m;
    u16 args[] = {(u16)m.guestLparam, (u16)(m.guestLparam >> 16), m.guestWparam, m.guestMessage,
                  window->id};
    u32 result = ui_reverse(ui, window->target, args, 5, window->dialog);
    ui->message = m.previous;
    if (r->terminated)
    {
        if (window->dialog)
        {
            UI_CALL(ui, EndDialog, native, -1);
        }
        else
        {
            UI_CALL(ui, PostQuitMessage, r->exitCode);
        }
    }
    if (lease && structureBytes && message != 1 && message != 0x81 && !m.defaultCalled && !r->error)
    {
        u8 *source = ui_buffer(r, scratch, structureBytes, 0), *dest = (u8 *)lparam;
        if (message == 0x24)
        {
            for (u32 i = 0; i < 10; i++)
            {
                ui_put32(dest + i * 4, (u32)(int)(short)ui_get16(source + i * 2));
            }
        }
        else if (message == 0x46 || message == 0x47)
        {
            for (u32 i = 0; i < 4; i++)
            {
                ui_put32(dest + 16 + i * 4, (u32)(int)(short)ui_get16(source + 4 + i * 2));
            }
            ui_put32(dest + 32, ui_get16(source + 12));
        }
        else if (message == 0x83)
        {
            for (u32 i = 0; i < (wparam
                ? 12
                : 4);
                 i++)
            {
                ui_put32(dest + i * 4, (u32)(int)(short)ui_get16(source + i * 2));
            }
        }
        else
        {
            for (u32 i = 0; i < 8; i++)
            {
                dest[i] = source[i];
            }
        }
    }
    if (lease)
    {
        local_free(r, r->local, lease);
    }
    if (message == 0x82)
    {
        ui_forget_window(window);
    }
    if (m.guestMessage == 0x19)
    {
        return ui_handle(ui, (u16)result, 7);
    }
    return window->dialog
        ? (u16)result
        : (int)result;
}

static void ui_from_guest(TaskUi *ui, UiArena *arena, iptr window, u16 message, u16 wparam,
                          u32 lparam, u32 *nativeMessage, iptr *nativeWparam, iptr *nativeLparam)
{
    *nativeMessage = message;
    *nativeWparam = wparam;
    *nativeLparam = (int)lparam;
    if (message == 0x111)
    {
        *nativeWparam = wparam | (lparam & 0xffff0000u);
        *nativeLparam = ui_handle(ui, (u16)lparam, 5);
        return;
    }
    if (message == 0x30)
    {
        *nativeWparam = ui_handle(ui, wparam, 7);
        return;
    }
    if (message == 7 || message == 8 || message == 0x20 || message == 0x21)
    {
        *nativeWparam = ui_handle(ui, wparam, 5);
        return;
    }
    if (message == 0xc)
    {
        *nativeLparam = (iptr)ui_wide(arena, lparam, 0);
        return;
    }
    if (message < 0x400 || message > 0x4ff)
    {
        return;
    }
    u16 className[64];
    zero(className, sizeof(className));
    UI_CALL(ui, GetClassNameW, window, (iptr)className, 64);
    static const u16 edit[] = {'E', 'D', 'I', 'T', 0},
                     listbox[] = {'L', 'I', 'S', 'T', 'B', 'O', 'X', 0};
    if (ui_wide_equal(className, edit) && message == 0x401)
    {
        *nativeMessage = 0xb1;
        *nativeWparam = (short)lparam;
        *nativeLparam = (short)(lparam >> 16);
        return;
    }
    if (!ui_wide_equal(className, listbox))
    {
        return;
    }
    if (message >= 0x401 && message <= 0x410)
    {
        *nativeMessage = 0x180 + message - 0x401;
        *nativeWparam = (short)wparam;
        if (message == 0x401 || message == 0x402 || message == 0x40d || message == 0x410)
        {
            *nativeLparam = (iptr)ui_wide(arena, lparam, 0);
        }
    }
}

static void ui_message_native(TaskUi *ui, UiArena *arena, u32 pointer, u8 *dest)
{
    Runtime *r = ui->runtime;
    u8 *source = ui_buffer(r, pointer, 18, 0);
    if (!source)
    {
        return;
    }
    for (u32 n = 0; n < ui->queuedCount; n++)
    {
        u32 i = 0;
        while (i < 18 && source[i] == ui->queuedGuest[n][i])
        {
            i++;
        }
        if (i == 18)
        {
            for (i = 0; i < 48; i++)
            {
                dest[i] = ui->queuedNative[n][i];
            }
            return;
        }
    }
    zero(dest, 48);
    iptr window = ui_handle(ui, ui_get16(source), 5);
    u32 message;
    iptr wp, lp;
    ui_from_guest(ui, arena, window, ui_get16(source + 2), ui_get16(source + 4),
                  ui_get32(source + 6), &message, &wp, &lp);
    *(iptr *)dest = window;
    ui_put32(dest + 8, message);
    *(iptr *)(dest + 16) = wp;
    *(iptr *)(dest + 24) = lp;
    ui_put32(dest + 32, ui_get32(source + 10));
    ui_put32(dest + 36, (u32)(int)(short)ui_get16(source + 14));
    ui_put32(dest + 40, (u32)(int)(short)ui_get16(source + 16));
}

static u32 ui_window_service(TaskUi *ui, UiArena *arena, State *state, u16 ordinal, const u32 *a)
{
    Runtime *r = ui->runtime;
    u32 result = 0;
    if (ordinal == 57)
    {
        u8 *source = ui_buffer(r, a[0], 26, 0);
        if (!source)
        {
            return 0;
        }
        u32 target = ui_get32(source + 2);
        const ProgramBinding *binding = program_resolve(r, target);
        if (!binding || binding->kind != 1)
        {
            r->error = 8;
            return 0;
        }
        if (ui_get16(source + 10) != ui->instance || (short)ui_get16(source + 6) < 0 ||
            (short)ui_get16(source + 8) < 0)
        {
            return 0;
        }
        u16 *name = ui_wide(arena, ui_get32(source + 22), 0);
        if (!name || r->error)
        {
            return 0;
        }
        for (UiClass *c = ui->classes; c; c = c->next)
        {
            if (ui_wide_equal(c->name, name))
            {
                return 0;
            }
        }
        u32 length = 0;
        while (name[length])
        {
            length++;
        }
        UiClass *cls = r->alloc(r->storage, sizeof(*cls) + (length + 1) * 2);
        if (!cls)
        {
            return 0;
        }
        zero(cls, sizeof(*cls));
        cls->name = (u16 *)(cls + 1);
        for (u32 i = 0; i <= length; i++)
        {
            cls->name[i] = name[i];
        }
        cls->target = target;
        cls->extra = ui_get16(source + 8);
        cls->instance = ui->instance;
        cls->menu = ui_get32(source + 18);
        u8 native[72];
        zero(native, 72);
        ui_put32(native, ui_get16(source));
        *(iptr *)(native + 8) = ui->windowProcedure;
        ui_put32(native + 20, cls->extra);
        *(iptr *)(native + 24) = UI_CALL(ui, GetModuleHandleW, 0);
        *(iptr *)(native + 32) = ui_handle(ui, ui_get16(source + 12), 10);
        *(iptr *)(native + 40) = ui_handle(ui, ui_get16(source + 14), 9);
        u16 brush = ui_get16(source + 16);
        GuiHandle *h = gui_handle(r->gui, brush, 7);
        *(iptr *)(native + 48) = h
            ? h->native
            : brush <= 32
                    ? brush
                    : ui_handle(ui, brush, 7);
        *(iptr *)(native + 64) = (iptr)cls->name;
        if (!r->error)
        {
            result = (u32)UI_CALL(ui, RegisterClassW, (iptr)native);
        }
        if (result)
        {
            cls->next = ui->classes;
            ui->classes = cls;
        }
        else
        {
            r->release(r->storage, cls);
        }
    }
    else if (ordinal == 41)
    {
        if (a[9] != ui->instance)
        {
            return 0;
        }
        u16 *name = ui_wide(arena, a[0], 1), *title = ui_wide(arena, a[1], 0);
        UiClass *cls = 0;
        if (a[0] >> 16)
        {
            for (UiClass *c = ui->classes; c; c = c->next)
            {
                if (ui_wide_equal(c->name, name))
                {
                    cls = c;
                    break;
                }
            }
        }
        int child = !!(a[2] & 0x40000000);
        u16 menuId = (u16)a[8];
        if (!child && !menuId && cls && cls->menu)
        {
            menuId = ui_load_menu(ui, arena, cls->menu);
        }
        iptr parent = ui_handle(ui, a[7], 5), menu = child
            ? (u16)a[8]
            : ui_handle(ui, menuId, 8);
        if (r->error)
        {
            return 0;
        }
        UiWindow *w = r->alloc(r->storage, sizeof(*w) + (cls
            ? cls->extra
            : 0));
        if (!w)
        {
            return 0;
        }
        zero(w, sizeof(*w) + (cls
            ? cls->extra
            : 0));
        w->words = (u16 *)(w + 1);
        w->ui = ui;
        w->cls = cls;
        w->target = cls
            ? cls->target
            : 0;
        w->extra = cls
            ? cls->extra
            : 0;
        for (u32 i = 0; i < 11; i++)
        {
            w->arguments[i] = a[i];
        }
        w->arguments[8] = menuId;
        w->next = ui->windows;
        ui->windows = w;
        iptr native = UI_CALL(ui, CreateWindowExW, 0, (iptr)name, (iptr)title, a[2],
                              a[3] == 0x8000
                                  ? (int)0x80000000
                                  : (short)a[3],
                              a[4] == 0x8000
                                  ? (int)0x80000000
                                  : (short)a[4],
                              a[5] == 0x8000
                                  ? (int)0x80000000
                                  : (short)a[5],
                              a[6] == 0x8000
                                  ? (int)0x80000000
                                  : (short)a[6],
                              parent, menu,
                              UI_CALL(ui, GetModuleHandleW, 0), (iptr)w);
        if (native)
        {
            w->native = native;
            w->id = ui_window_id(ui, native);
            GuiHandle *h = gui_handle(r->gui, w->id, 5);
            if (h)
            {
                h->owned = 1;
                h->parent = (u16)a[7];
            }
            if (!child && menuId)
            {
                h = gui_handle(r->gui, menuId, 8);
                if (h)
                {
                    h->parent = w->id;
                }
            }
            result = w->id;
        }
    }
    else if (ordinal == 133 || ordinal == 134)
    {
        iptr native = ui_handle(ui, a[0], 5);
        UiWindow *w = ui_window(ui, native);
        int index = (short)a[1];
        if (!w)
        {
            return 0;
        }
        if (index >= 0 && index + 2 <= w->extra)
        {
            u8 *words = (u8 *)w->words;
            result = ui_get16(words + index);
            if (ordinal == 134)
            {
                ui_put16(words + index, (u16)a[2]);
            }
        }
        else if (index == -6)
        {
            result = ui->instance;
        }
        else if (index == -8)
        {
            result = ui_window_id(ui, UI_CALL(ui, GetParent, native));
            if (ordinal == 134)
            {
                result = ui_window_id(ui, UI_CALL(ui, SetParent, native, ui_handle(ui, a[2], 5)));
            }
        }
        else if (index == -12)
        {
            result = (u16)UI_CALL(ui, GetWindowLongW, native, -12);
            if (ordinal == 134)
            {
                result = (u16)UI_CALL(ui, SetWindowLongW, native, -12, (u16)a[2]);
            }
        }
    }
    else if (ordinal == 66)
    {
        iptr window = ui_handle(ui, a[0], 5);
        if (r->error)
        {
            return 0;
        }
        iptr dc = UI_CALL(ui, GetDC, window);
        if (!dc)
        {
            return 0;
        }
        u16 id = ui_handle_add(ui, dc, 6, 0, (u16)a[0]);
        UiDc *lease = 0;
        for (lease = ui->dcs; lease; lease = lease->next)
        {
            if (lease->id == id && lease->window == window)
            {
                break;
            }
        }
        if (lease)
        {
            lease->uses++;
        }
        else
        {
            lease = r->alloc(r->storage, sizeof(*lease));
            if (!lease)
            {
                UI_CALL(ui, ReleaseDC, window, dc);
                return 0;
            }
            lease->id = id;
            lease->window = window;
            lease->native = dc;
            lease->uses = 1;
            lease->next = ui->dcs;
            ui->dcs = lease;
        }
        result = id;
    }
    else if (ordinal == 68)
    {
        iptr window = ui_handle(ui, a[0], 5);
        UiDc **p = &ui->dcs;
        while (*p)
        {
            UiDc *d = *p;
            if (d->id == a[1] && d->window == window)
            {
                result = (u32)UI_CALL(ui, ReleaseDC, window, d->native);
                if (result && !--d->uses)
                {
                    *p = d->next;
                    gui_forget(r, r->gui, d->id);
                    r->release(r->storage, d);
                }
                break;
            }
            p = &d->next;
        }
    }
    else if (ordinal == 39)
    {
        u8 *dest = ui_buffer(r, a[1], 32, 1);
        iptr window = ui_handle(ui, a[0], 5);
        for (UiPaint *p = ui->paints; p; p = p->next)
        {
            if (p->pointer == a[1])
            {
                r->error = 14;
                return 0;
            }
        }
        if (!dest || r->error)
        {
            return 0;
        }
        UiPaint *p = r->alloc(r->storage, sizeof(*p));
        if (!p)
        {
            return 0;
        }
        zero(p, sizeof(*p));
        p->window = window;
        p->pointer = a[1];
        iptr dc = UI_CALL(ui, BeginPaint, window, (iptr)p->native);
        if (!dc)
        {
            r->release(r->storage, p);
            return 0;
        }
        p->dc = ui_handle_add(ui, dc, 6, 0, (u16)a[0]);
        if (!p->dc)
        {
            UI_CALL(ui, EndPaint, window, (iptr)p->native);
            r->release(r->storage, p);
            return 0;
        }
        zero(dest, 32);
        ui_put16(dest, p->dc);
        ui_put16(dest + 2, !!ui_get32(p->native + 8));
        for (u32 i = 0; i < 4; i++)
        {
            ui_put16(dest + 4 + i * 2, (u16)ui_get32(p->native + 12 + i * 4));
        }
        ui_put16(dest + 12, !!ui_get32(p->native + 28));
        ui_put16(dest + 14, !!ui_get32(p->native + 32));
        p->next = ui->paints;
        ui->paints = p;
        result = p->dc;
    }
    else if (ordinal == 40)
    {
        iptr window = ui_handle(ui, a[0], 5);
        if (!ui_buffer(r, a[1], 32, 0))
        {
            return 0;
        }
        UiPaint **p = &ui->paints;
        while (*p)
        {
            UiPaint *paint = *p;
            if (paint->window == window && paint->pointer == a[1])
            {
                result = (u32)UI_CALL(ui, EndPaint, window, (iptr)paint->native);
                if (result)
                {
                    *p = paint->next;
                    gui_forget(r, r->gui, paint->dc);
                    r->release(r->storage, paint);
                }
                break;
            }
            p = &paint->next;
        }
    }
    else if (ordinal == 107)
    {
        iptr window = ui_handle(ui, a[0], 5);
        UiMessage *m = ui->message;
        if (m && m->window == window && m->guestMessage == a[1] && m->guestWparam == a[2] &&
            m->guestLparam == a[3])
        {
            m->defaultCalled = 1;
            iptr native = UI_CALL(ui, DefWindowProcW, window, m->message, m->wparam, m->lparam);
            result = m->guestMessage == 0x19
                ? ui_handle_add(ui, native, 7, 0, 0)
                : (u32)native;
        }
        else
        {
            u32 message;
            iptr wp, lp;
            ui_from_guest(ui, arena, window, (u16)a[1], (u16)a[2], a[3], &message, &wp, &lp);
            if (!r->error)
            {
                result = (u32)UI_CALL(ui, DefWindowProcW, window, message, wp, lp);
            }
        }
    }
    else if (ordinal == 110 || ordinal == 111 || ordinal == 101)
    {
        iptr window = ui_handle(ui, a[0], 5);
        u32 base = ordinal == 101
            ? 2
            : 1;
        if (ordinal == 101)
        {
            window = UI_CALL(ui, GetDlgItem, window, (short)a[1]);
        }
        u32 message;
        iptr wp, lp;
        ui_from_guest(ui, arena, window, (u16)a[base], (u16)a[base + 1], a[base + 2], &message, &wp,
                      &lp);
        if (ordinal == 110 && (message == 0xc || message == 0xd))
        {
            r->error = 14;
            return 0;
        }
        if (!r->error)
        {
            iptr value = ordinal == 110
                ? UI_CALL(ui, PostMessageW, window, message, wp, lp)
                : UI_CALL(ui, SendMessageW, window, message, wp, lp);
            result = message == 0x31
                ? ui_handle_add(ui, value, 7, 0, 0)
                : (u32)value;
        }
    }
    else if (ordinal == 109)
    {
        u8 *dest = ui_buffer(r, a[0], 18, 1), native[48];
        if (!dest)
        {
            return 0;
        }
        zero(native, 48);
        iptr window = ui_handle(ui, a[1], 5);
        if (!r->error)
        {
            result = (u32)UI_CALL(ui, PeekMessageW, (iptr)native, window, (u16)a[2], (u16)a[3],
                                  (u16)a[4]);
        }
        if (result)
        {
            UiMessage m = {0};
            m.window = *(iptr *)native;
            m.message = ui_get32(native + 8);
            m.wparam = *(iptr *)(native + 16);
            m.lparam = *(iptr *)(native + 24);
            ui_to_guest(ui, &m);
            ui_put16(dest, ui_window_id(ui, m.window));
            ui_put16(dest + 2, m.guestMessage);
            ui_put16(dest + 4, m.guestWparam);
            ui_put32(dest + 6, m.guestLparam);
            ui_put32(dest + 10, ui_get32(native + 32));
            ui_put16(dest + 14, (u16)ui_get32(native + 36));
            ui_put16(dest + 16, (u16)ui_get32(native + 40));
            u32 slot = ui->queuedNext++ % 32;
            if (ui->queuedCount < 32)
            {
                ui->queuedCount++;
            }
            for (u32 i = 0; i < 18; i++)
            {
                ui->queuedGuest[slot][i] = dest[i];
            }
            for (u32 i = 0; i < 48; i++)
            {
                ui->queuedNative[slot][i] = native[i];
            }
        }
    }
    else if (ordinal == 113 || ordinal == 114 || ordinal == 178)
    {
        u8 native[48];
        ui_message_native(ui, arena, a[ordinal == 178
            ? 2
            : 0], native);
        if (!r->error)
        {
            if (ordinal == 178)
            {
                result = (u32)UI_CALL(ui, TranslateAcceleratorW, ui_handle(ui, a[0], 5),
                                      ui_handle(ui, a[1], 11), (iptr)native);
            }
            else if (ordinal == 113)
            {
                result = (u32)UI_CALL(ui, TranslateMessage, (iptr)native);
            }
            else
            {
                result = (u32)UI_CALL(ui, DispatchMessageW, (iptr)native);
            }
        }
    }
    else if (ordinal == 10)
    {
        iptr window = ui_handle(ui, a[0], 5);
        u32 target = a[3];
        if (target)
        {
            const ProgramBinding *binding = program_resolve(r, target);
            if (!binding || binding->kind != 1 || !window)
            {
                return 0;
            }
        }
        if (r->error)
        {
            return 0;
        }
        iptr id =
            UI_CALL(ui, SetTimer, window, (u16)a[1], (u16)a[2], target
                ? ui->timerProcedure
                : 0);
        if (id <= 0 || id > 65535)
        {
            if (id)
            {
                UI_CALL(ui, KillTimer, window, id);
            }
            return 0;
        }
        result = (u16)id;
        UiTimer *timer = 0;
        for (timer = ui->timers; timer; timer = timer->next)
        {
            if (timer->window == window && timer->id == (u16)id)
            {
                break;
            }
        }
        if (!timer)
        {
            timer = r->alloc(r->storage, sizeof(*timer));
            if (!timer)
            {
                UI_CALL(ui, KillTimer, window, id);
                return 0;
            }
            timer->next = ui->timers;
            ui->timers = timer;
        }
        timer->window = window;
        timer->native = id;
        timer->id = (u16)id;
        timer->target = target;
    }
    else if (ordinal == 12)
    {
        iptr window = ui_handle(ui, a[0], 5);
        if (r->error)
        {
            return 0;
        }
        result = (u32)UI_CALL(ui, KillTimer, window, (u16)a[1]);
        if (result)
        {
            UiTimer **p = &ui->timers;
            while (*p)
            {
                UiTimer *t = *p;
                if (t->window == window && t->id == a[1])
                {
                    *p = t->next;
                    r->release(r->storage, t);
                    break;
                }
                p = &t->next;
            }
        }
    }
    else if (ordinal == 87)
    {
        if (a[0] != ui->instance)
        {
            return (u16)-1;
        }
        TaskResource *resource = ui_resource(ui, 5, a[1]);
        if (!resource)
        {
            return (u16)-1;
        }
        const ProgramBinding *binding = program_resolve(r, a[3]);
        if (!binding || binding->kind != 1)
        {
            r->error = 8;
            return 0;
        }
        u32 bytes = 0;
        u8 *data = ui_dialog_template(r, arena, resource->bytes, resource->length, &bytes);
        if (!data)
        {
            return (u16)-1;
        }
        UiWindow *window = r->alloc(r->storage, sizeof(*window) + 30);
        if (!window)
        {
            return (u16)-1;
        }
        zero(window, sizeof(*window) + 30);
        window->ui = ui;
        window->dialog = 1;
        window->target = a[3];
        window->extra = 30;
        window->words = (u16 *)(window + 1);
        window->next = ui->windows;
        ui->windows = window;
        result =
            (u16)UI_CALL(ui, DialogBoxIndirectParamW, UI_CALL(ui, GetModuleHandleW, 0), (iptr)data,
                         ui_handle(ui, a[2], 5), ui->dialogProcedure, (iptr)window);
    }
    else
    {
        r->error = 14;
    }
    return result;
}

ABI NOINLINE u16 __win16_ui_window(Runtime *r, State *s, u16 ordinal)
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
    u32 result = ui_window_service(ui, &arena, s, ordinal, args);
    ui_destroy_arena(&arena);
    if (!r->error && c->result)
    {
        s->ax = (u16)result;
        if (c->result == 4)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return c->cleanup;
}

static void destroy_task_ui(Runtime *r)
{
    TaskUi *ui = r->gui
        ? r->gui->ui
        : 0;
    if (!ui)
    {
        return;
    }
    while (ui->timers)
    {
        UiTimer *t = ui->timers;
        ui->timers = t->next;
        UI_CALL(ui, KillTimer, t->window, t->native);
        r->release(r->storage, t);
    }
    while (ui->paints)
    {
        UiPaint *p = ui->paints;
        ui->paints = p->next;
        UI_CALL(ui, EndPaint, p->window, (iptr)p->native);
        gui_forget(r, r->gui, p->dc);
        r->release(r->storage, p);
    }
    while (ui->dcs)
    {
        UiDc *d = ui->dcs;
        ui->dcs = d->next;
        for (u32 i = 0; i < d->uses; i++)
        {
            UI_CALL(ui, ReleaseDC, d->window, d->native);
        }
        gui_forget(r, r->gui, d->id);
        r->release(r->storage, d);
    }
    for (UiWindow *w = ui->windows; w; w = w->next)
    {
        if (w->native)
        {
            UI_CALL(ui, DestroyWindow, w->native);
            if (w->id)
            {
                ui_forget_window(w);
            }
        }
    }
    while (ui->windows)
    {
        UiWindow *w = ui->windows;
        ui->windows = w->next;
        r->release(r->storage, w);
    }
    while (ui->classes)
    {
        UiClass *c = ui->classes;
        ui->classes = c->next;
        UI_CALL(ui, UnregisterClassW, (iptr)c->name, UI_CALL(ui, GetModuleHandleW, 0));
        r->release(r->storage, c);
    }
    for (;;)
    {
        GuiHandle *h = r->gui->handles;
        while (h && !(h->owned && h->kind >= 8 && h->kind <= 11))
        {
            h = h->next;
        }
        if (!h)
        {
            break;
        }
        if (h->kind == 8)
        {
            UI_CALL(ui, DestroyMenu, h->native);
        }
        else if (h->kind == 9)
        {
            UI_CALL(ui, DestroyCursor, h->native);
        }
        else if (h->kind == 10)
        {
            UI_CALL(ui, DestroyIcon, h->native);
        }
        else
        {
            UI_CALL(ui, DestroyAcceleratorTable, h->native);
        }
        gui_forget(r, r->gui, h->id);
    }
    destroy_task_lifecycle(r);
    destroy_task_modules(r);
    destroy_task_wep(r);
    r->release(r->storage, ui);
    r->gui->ui = 0;
}
