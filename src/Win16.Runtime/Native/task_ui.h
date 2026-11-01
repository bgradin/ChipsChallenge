/* Whole-task structure/resource/message adaptation. Windows pointers remain host-owned. */
#include "task_ui_contracts.h"
typedef iptr (*UiCall)(void *, u16, const iptr *, u16);
typedef struct UiBuffer
{
    struct UiBuffer *next;
    u8 bytes[];
} UiBuffer;
typedef struct UiArena
{
    Runtime *r;
    UiBuffer *buffers;
} UiArena;
typedef struct TaskUi
{
    Runtime *runtime;
    State *state;
    UiCall call;
    void *context;
    u16 instance;
    struct UiWindow *windows;
    struct UiClass *classes;
    struct UiPaint *paints;
    struct UiDc *dcs;
    struct UiTimer *timers;
    struct UiMessage *message;
    u32 depth;
    iptr windowProcedure, dialogProcedure, timerProcedure, aboutProcedure;
    u32 queuedNext, queuedCount;
    u8 queuedGuest[32][18], queuedNative[32][48];
} TaskUi;

static const UiContract *ui_contract(u16 module, u16 ordinal)
{
    for (u32 i = 0; i < sizeof(ui_contracts) / sizeof(ui_contracts[0]); i++)
    {
        if (ui_contracts[i].module == module && ui_contracts[i].ordinal == ordinal)
        {
            return ui_contracts + i;
        }
    }
    return 0;
}

static u8 *ui_buffer(Runtime *r, u32 pointer, u32 bytes, int write)
{
    Segment *s = find_segment(r, (u16)(pointer >> 16));
    u32 at = (u16)pointer;
    if (!s || !(s->permissions & (write
        ? 2
        : 1)) || at > s->size || bytes > s->size - at ||
        (write && !writable(r, s, (u16)at, bytes)))
    {
        if (!r->error)
        {
            r->error = 9;
        }
        return 0;
    }
    return s->bytes + at;
}

static u16 ui_get16(const u8 *p)
{
    return p[0] | (u16)p[1] << 8;
}

static u32 ui_get32(const u8 *p)
{
    return ui_get16(p) | (u32)ui_get16(p + 2) << 16;
}

static void ui_put16(u8 *p, u16 value)
{
    p[0] = (u8)value;
    p[1] = (u8)(value >> 8);
}

static void ui_put32(u8 *p, u32 value)
{
    ui_put16(p, (u16)value);
    ui_put16(p + 2, (u16)(value >> 16));
}

static void *ui_alloc(UiArena *a, u32 bytes)
{
    if (bytes > 64 * 1024 * 1024)
    {
        a->r->error = 9;
        return 0;
    }
    UiBuffer *b = a->r->alloc(a->r->storage, sizeof(*b) + bytes);
    if (!b)
    {
        a->r->error = 10;
        return 0;
    }
    b->next = a->buffers;
    a->buffers = b;
    zero(b->bytes, bytes);
    return b->bytes;
}

static void ui_destroy_arena(UiArena *a)
{
    while (a->buffers)
    {
        UiBuffer *b = a->buffers;
        a->buffers = b->next;
        a->r->release(a->r->storage, b);
    }
}

static iptr ui_os(TaskUi *ui, u16 function, u16 count, const iptr *args)
{
    if (!ui || !ui->call)
    {
        if (ui)
        {
            ui->runtime->error = 14;
        }
        return 0;
    }
    return ui->call(ui->context, function, args, count);
}
#define UI_CALL(ui, name, ...) \
    ui_os(ui, UI_##name, (u16)(sizeof((iptr[]){__VA_ARGS__}) / sizeof(iptr)), (iptr[]){__VA_ARGS__})

static iptr ui_handle(TaskUi *ui, u32 id, u16 kind)
{
    if (!id)
    {
        return 0;
    }
    GuiHandle *h = id <= 65535
        ? gui_handle(ui->runtime->gui, (u16)id, kind)
        : 0;
    if (!h)
    {
        ui->runtime->error = 14;
        return 0;
    }
    return h->native;
}

static u16 ui_handle_add(TaskUi *ui, iptr native, u16 kind, u16 owned, u16 parent)
{
    return gui_add(ui->runtime, ui->runtime->gui, native, kind, owned, parent);
}

static u16 ui_cp1252(u8 ch)
{
    static const u16 map[] = {0x20ac, 0x81, 0x201a, 0x192, 0x201e, 0x2026, 0x2020, 0x2021,
                              0x2c6, 0x2030, 0x160, 0x2039, 0x152, 0x8d, 0x17d, 0x8f,
                              0x90, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014,
                              0x2dc, 0x2122, 0x161, 0x203a, 0x153, 0x9d, 0x17e, 0x178};
    return ch >= 0x80 && ch < 0xa0
        ? map[ch - 0x80]
        : ch;
}

static u8 ui_ansi(Runtime *r, u16 ch)
{
    if (ch < 0x80 || (ch >= 0xa0 && ch <= 0xff))
    {
        return (u8)ch;
    }
    for (u16 i = 0x80; i < 0xa0; i++)
    {
        if (ui_cp1252((u8)i) == ch)
        {
            return (u8)i;
        }
    }
    r->error = 14;
    return 0;
}

static u16 *ui_wide_bytes(UiArena *a, const u8 *text, u32 length)
{
    u16 *wide = ui_alloc(a, (length + 1) * 2);
    if (wide)
    {
        for (u32 i = 0; i < length; i++)
        {
            wide[i] = ui_cp1252(text[i]);
        }
    }
    return wide;
}

static u16 *ui_wide(UiArena *a, u32 pointer, int identifier)
{
    if (!pointer)
    {
        return 0;
    }
    if (identifier && !(pointer >> 16))
    {
        return (u16 *)(iptr)pointer;
    }
    u32 length = 0;
    u8 *text = kernel_string(a->r, pointer, &length);
    return text
        ? ui_wide_bytes(a, text, length)
        : 0;
}

static int *ui_rect(UiArena *a, u32 pointer)
{
    if (!pointer)
    {
        return 0;
    }
    u8 *source = ui_buffer(a->r, pointer, 8, 0);
    if (!source)
    {
        return 0;
    }
    int *rect = ui_alloc(a, 16);
    if (rect)
    {
        for (u32 i = 0; i < 4; i++)
        {
            rect[i] = (short)ui_get16(source + i * 2);
        }
    }
    return rect;
}

static void ui_write_rect(Runtime *r, u32 pointer, const int *rect)
{
    u8 *dest = ui_buffer(r, pointer, 8, 1);
    if (!dest)
    {
        return;
    }
    for (u32 i = 0; i < 4; i++)
    {
        if (rect[i] < -32768 || rect[i] > 32767)
        {
            r->error = 14;
            return;
        }
    }
    for (u32 i = 0; i < 4; i++)
    {
        ui_put16(dest + i * 2, (u16)rect[i]);
    }
}

static int ui_read_arguments(Runtime *r, State *s, const UiContract *c, u32 *args)
{
    if (!c)
    {
        r->error = 14;
        return 0;
    }
    for (u32 i = 0; i < c->count; i++)
    {
        u16 at = (u16)(s->sp + 4 + c->words[i] * 2);
        args[i] =
            c->widths[i] == 4
                ? __win16_read_dword(r, s->ss, at)
                : __win16_read_word(r, s->ss, at);
    }
    return !r->error;
}

static TaskUi *ui_context(Runtime *r, State *s)
{
    TaskUi *ui = r->gui
        ? r->gui->ui
        : 0;
    if (!ui)
    {
        r->error = 14;
        return 0;
    }
    ui->state = s;
    return ui;
}
