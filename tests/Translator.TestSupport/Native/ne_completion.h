/* Completion compatibility only: translated EndLevel decides the win and the
 * original dialog procedure computes every score and chooses the next level.
 * INI writes are process-local, bounded and readable by subsequent original calls. */
extern __attribute__((visibility("hidden"))) ABI int native_GetWindowRect(void *, NativeRect *);
extern __attribute__((visibility("hidden"))) ABI int native_SetWindowPos(void *, void *, int, int,
                                                                         int, int, u32);
typedef struct
{
    u16 file[80], section[80], key[80], value[128];
} CompletionEntry;
typedef struct
{
    CompletionEntry entries[32];
    u32 count, lease, ended;
    u16 result, text[6][128];
    void *dialog, *items[6];
} CompletionState;

static int completion_equal(const u16 *a, const u16 *b)
{
    for (u32 i = 0; i < 128; i++)
    {
        u16 x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z')
        {
            x += 32;
        }
        if (y >= 'A' && y <= 'Z')
        {
            y += 32;
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

static void completion_copy(u16 *d, const u16 *s)
{
    for (u32 i = 0; i < 128; i++)
    {
        d[i] = s[i];
        if (!s[i])
        {
            return;
        }
    }
}

static int completion_read(Runtime *r, u32 fp, u16 *out, u32 size)
{
    for (u32 i = 0; i < size; i++)
    {
        u8 *p = window_memory(r, fp + i, 1, 0);
        if (!p)
        {
            return 0;
        }
        out[i] = *p;
        if (!*p)
        {
            return 1;
        }
    }
    r->error = 14;
    return 0;
}

static int completion_number(const u16 *text, int *value)
{
    u32 at = 0;
    int negative = 0;
    unsigned v = 0;
    while (text[at] == ' ')
    {
        at++;
    }
    if (text[at] == '-' || text[at] == '+')
    {
        negative = text[at++] == '-';
    }
    u32 start = at;
    while (text[at] >= '0' && text[at] <= '9')
    {
        if (v > 214748364u)
        {
            return 0;
        }
        v = v * 10 + text[at++] - '0';
    }
    if (at == start)
    {
        return 0;
    }
    *value = negative
        ? -(int)v
        : (int)v;
    return 1;
}

static u32 completion_open(WindowHost *h)
{
    if (!h->app->completion)
    {
        return 0;
    }
    h->app->completionState = heap_alloc(&h->app->storage, sizeof(CompletionState));
    if (!h->app->completionState)
    {
        return 20;
    }
    zero(h->app->completionState, sizeof(CompletionState));
    return 0;
}

static u32 completion_close(WindowHost *h)
{
    CompletionState *c = h->app->completionState;
    if (!c)
    {
        return 0;
    }
    if (c->dialog)
    {
        native_DestroyWindow(c->dialog);
    }
    heap_release(&h->app->storage, c);
    h->app->completionState = 0;
    return 0;
}
/* Win16 wsprintf is cdecl: arguments stay on the original stack. Only audited
 * string/signed-word/signed-long conversions are accepted, with no truncation. */

static u32 completion_format(WindowHost *h, State *s)
{
    Runtime *r = &h->app->runtime;
    u16 format[128];
    if (!completion_read(r, window_arg32(r, s, 2), format, 128))
    {
        return 0;
    }
    u8 result[128];
    u32 at = 0, word = 4;
    for (u32 i = 0; format[i]; i++)
    {
        if (format[i] != '%')
        {
            if (at == 127)
            {
                goto bad;
            }
            result[at++] = (u8)format[i];
            continue;
        }
        u16 type = format[++i];
        if (type == '%')
        {
            if (at == 127)
            {
                goto bad;
            }
            result[at++] = '%';
            continue;
        }
        if (type == 's')
        {
            u16 value[128];
            if (!completion_read(r, window_arg32(r, s, word), value, 128))
            {
                return 0;
            }
            word += 2;
            for (u32 j = 0; value[j]; j++)
            {
                if (at == 127)
                {
                    goto bad;
                }
                result[at++] = (u8)value[j];
            }
            continue;
        }
        int value;
        if (type == 'd')
        {
            value = (short)arg(r, s, word++);
        }
        else if (type == 'l' && format[i + 1] == 'i')
        {
            value = (int)window_arg32(r, s, word);
            word += 2;
            i++;
        }
        else
        {
            goto bad;
        }
        unsigned magnitude = value < 0
            ? 0u - (unsigned)value
            : (unsigned)value;
        u8 digits[10];
        u32 count = 0;
        do
        {
            digits[count++] = (u8)('0' + magnitude % 10);
            magnitude /= 10;
        } while (magnitude);
        if (at + count + (value < 0) > 127)
        {
            goto bad;
        }
        if (value < 0)
        {
            result[at++] = '-';
        }
        while (count)
        {
            result[at++] = digits[--count];
        }
    }
    result[at] = 0;
    u8 *dest = window_memory(r, window_arg32(r, s, 0), at + 1, 1);
    if (!dest)
    {
        return 0;
    }
    for (u32 i = 0; i <= at; i++)
    {
        dest[i] = result[i];
    }
    return at;
bad:
    r->error = 14;
    return 0;
}

static u32 completion_dialog(WindowHost *h, State *s)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CompletionState *c = a->completionState;
    u16 name[80], expected[] = {'D', 'L', 'G', '_', 'C', 'O', 'M', 'P', 'L', 'E', 'T', 'E', 0};
    WindowIdentity *owner = window_id(h, arg(r, s, 2));
    if (!owner || arg(r, s, 5) != a->instance || !c->lease ||
        window_arg32(r, s, 0) != a->completionTarget ||
        !window_string(r, window_arg32(r, s, 3), name) || !completion_equal(name, expected) ||
        h->nextId != 7)
    {
        r->error = 14;
        return 0;
    }
    u16 cls[] = {'S', 'T', 'A', 'T', 'I', 'C', 0};
    c->ended = 0;
    zero(c->text, sizeof(c->text));
    c->dialog = native_CreateWindowExW(0, cls, 0, 0x80000000u, 0, 0, 420, 220, owner->native, 0,
                                       a->module, 0);
    if (!c->dialog)
    {
        r->error = 14;
        return 0;
    }
    WindowIdentity *w = &h->windows[h->nextId++];
    zero(w, sizeof(*w));
    w->id = 8;
    w->native = c->dialog;
    w->live = 1;
    w->target = a->completionTarget;
    for (u32 i = 0; i < 6; i++)
    {
        c->items[i] = native_CreateWindowExW(0, cls, 0, 0x40000000, 0, i * 24, 400, 24, c->dialog,
                                             0, a->module, 0);
        if (!c->items[i])
        {
            r->error = 14;
            break;
        }
    }
    u32 previous = h->boundTarget;
    h->boundTarget = a->completionTarget;
    if (!r->error)
    {
        window_dispatch(h, c->dialog, 0x110, 0, 0);
    }
    if (h->result && !a->oracleOffset)
    {
        a->oracleOffset = 0x6300;
    }
    if (!r->error && !h->result && a->automation)
    {
        const u8 *p = a->completionPlan;
        if (!p || get32(p) != 6)
        {
            h->result = 40;
        }
        else
        {
            p += 4;
            for (u32 i = 0; i < 6; i++)
            {
                u32 n = get32(p);
                p += 4;
                for (u32 j = 0; j < n; j++)
                {
                    if (c->text[i][j] != p[j])
                    {
                        a->oracleOffset = 0x6000 + i;
                        a->oracleActual = c->text[i][j];
                        a->oracleExpected = p[j];
                        h->result = 40;
                    }
                }
                if (c->text[i][n])
                {
                    a->oracleOffset = 0x6100 + i;
                    a->oracleActual = c->text[i][n];
                    h->result = 40;
                }
                p += n;
            }
        }
    }
    if (!r->error && !h->result)
    {
        if (!a->automation)
        {
            u16 text[800],
                title[] = {'L', 'e', 'v', 'e', 'l', ' ', 'c', 'o', 'm', 'p', 'l', 'e', 't', 'e', 0};
            u32 at = 0;
            for (u32 i = 0; i < 6; i++)
            {
                for (u32 j = 0; c->text[i][j]; j++)
                {
                    text[at++] = c->text[i][j];
                }
                text[at++] = '\n';
            }
            text[at] = 0;
            native_MessageBoxW(owner->native, text, title, 0);
        }
        window_dispatch(h, c->dialog, 0x111, 106, 0);
        if (!c->ended && !h->result)
        {
            a->oracleOffset = 0x6200;
            h->result = 40;
        }
    }
    h->boundTarget = previous;
    native_DestroyWindow(c->dialog);
    c->dialog = 0;
    zero(w, sizeof(*w));
    h->nextId = 7;
    a->completionDialogs++;
    return c->result;
}

static u32 completion_service(WindowHost *h, State *s, u16 offset, u16 *cleanup)
{
    NativeApplication *a = h->app;
    Runtime *r = &a->runtime;
    CompletionState *c = a->completionState;
    if (!a->completion || !c)
    {
        r->error = 5;
        return 0;
    }
    if (offset == 0x36c)
    {
        *cleanup = 6;
        if (c->lease || arg(r, s, 0) != a->instance || window_arg32(r, s, 1) != a->completionTarget)
        {
            r->error = 14;
            return 0;
        }
        c->lease = 1;
        return a->completionTarget;
    }
    if (offset == 0x370)
    {
        *cleanup = 4;
        if (!c->lease || c->dialog || window_arg32(r, s, 0) != a->completionTarget)
        {
            r->error = 14;
        }
        else
        {
            c->lease = 0;
        }
        return 0;
    }
    if (offset == 0x380)
    {
        *cleanup = 12;
        return completion_dialog(h, s);
    }
    if (offset == 0x384)
    {
        *cleanup = 4;
        WindowIdentity *w = window_id(h, arg(r, s, 1));
        if (!w || w->native != c->dialog || c->ended)
        {
            r->error = 14;
            return 0;
        }
        c->ended = 1;
        c->result = arg(r, s, 0);
        return 1;
    }
    if (offset == 0x388)
    {
        *cleanup = 8;
        WindowIdentity *w = window_id(h, arg(r, s, 3));
        u16 id = arg(r, s, 2);
        u32 index = id == 108
            ? 5
            : id >= 101 && id <= 105
                    ? id - 101
                    : 6;
        if (!w || w->native != c->dialog || index == 6 ||
            !completion_read(r, window_arg32(r, s, 0), c->text[index], 128))
        {
            r->error = 14;
            return 0;
        }
        return native_SetWindowTextW(c->items[index], c->text[index]) != 0;
    }
    if (offset == 0x38c)
    {
        *cleanup = 2;
        WindowIdentity *w = window_id(h, arg(r, s, 0));
        NativeRect rect, owner;
        if (!w || w->native != c->dialog || !native_GetWindowRect(h->windows[0].native, &owner) ||
            !native_GetWindowRect(c->dialog, &rect))
        {
            r->error = 14;
            return 0;
        }
        return native_SetWindowPos(
                   c->dialog, 0, (owner.left + owner.right - rect.right + rect.left) / 2,
                   (owner.top + owner.bottom - rect.bottom + rect.top) / 2, 0, 0, 0x15) != 0;
    }
    u32 section = offset == 0x374
        ? 5
        : offset == 0x378
                ? 9
                : 6,
        key = section - 2;
    u16 file[80], group[80], name[80], value[128];
    *cleanup = offset == 0x374
        ? 14
        : offset == 0x378
                ? 22
                : 16;
    if (!completion_read(r, window_arg32(r, s, 0), file, 80) ||
        !completion_read(r, window_arg32(r, s, section), group, 80) ||
        !completion_read(r, window_arg32(r, s, key), name, 80))
    {
        return 0;
    }
    CompletionEntry *entry = 0;
    for (u32 i = 0; i < c->count; i++)
    {
        if (completion_equal(c->entries[i].file, file) &&
            completion_equal(c->entries[i].section, group) &&
            completion_equal(c->entries[i].key, name))
        {
            entry = &c->entries[i];
            break;
        }
    }
    if (offset == 0x37c)
    {
        if (!completion_read(r, window_arg32(r, s, 2), value, 128))
        {
            return 0;
        }
        if (!entry)
        {
            if (c->count == 32)
            {
                return 0;
            }
            entry = &c->entries[c->count++];
            completion_copy(entry->file, file);
            completion_copy(entry->section, group);
            completion_copy(entry->key, name);
        }
        completion_copy(entry->value, value);
        return 1;
    }
    if (offset == 0x374)
    {
        int number;
        if (entry && completion_number(entry->value, &number))
        {
            return (u16)number;
        }
        return arg(r, s, 2);
    }
    if (!entry && !completion_read(r, window_arg32(r, s, 5), value, 128))
    {
        return 0;
    }
    const u16 *text = entry
        ? entry->value
        : value;
    u32 size = arg(r, s, 2);
    if (!size)
    {
        return 0;
    }
    u32 length = 0;
    while (text[length] && length < size - 1)
    {
        length++;
    }
    u8 *dest = window_memory(r, window_arg32(r, s, 3), length + 1, 1);
    if (!dest)
    {
        return 0;
    }
    for (u32 i = 0; i < length; i++)
    {
        dest[i] = (u8)text[i];
    }
    dest[length] = 0;
    return length;
}
