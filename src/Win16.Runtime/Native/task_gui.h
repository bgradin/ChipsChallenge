/* Explicit API marshalling plans. This dispatches OS contracts, never guest opcodes. */
typedef long long iptr;
#include "task_gui_plans.h"
typedef iptr (*GuiCall)(void *, u16, u16, const iptr *, u16);
typedef struct GuiHandle
{
    struct GuiHandle *next;
    iptr native;
    u16 id, kind, owned, parent;
} GuiHandle;
typedef struct TaskGui
{
    void *context;
    GuiCall call;
    GuiHandle *handles;
    u32 nextId, count;
    struct TaskUi *ui;
} TaskGui;

static void init_task_gui(TaskGui *gui, void *context, GuiCall call)
{
    zero(gui, sizeof(*gui));
    gui->context = context;
    gui->call = call;
    gui->nextId = 1;
}

static GuiHandle *gui_handle(TaskGui *gui, u16 id, u16 kind)
{
    for (GuiHandle *h = gui->handles; h; h = h->next)
    {
        if (h->id == id && h->kind == kind)
        {
            return h;
        }
    }
    return 0;
}

static u16 gui_add(Runtime *r, TaskGui *gui, iptr native, u16 kind, u16 owned, u16 parent)
{
    if (!native || native == -1)
    {
        return 0;
    }
    for (GuiHandle *h = gui->handles; h; h = h->next)
    {
        if (h->native == native && h->kind == kind)
        {
            if (owned && !h->owned)
            {
                r->error = 14;
                return 0;
            }
            return h->id;
        }
    }
    if (gui->nextId >= 65535)
    {
        return 0;
    }
    GuiHandle *h = r->alloc(r->storage, sizeof(*h));
    if (!h)
    {
        return 0;
    }
    h->native = native;
    h->kind = kind;
    h->owned = owned;
    h->parent = parent;
    h->id = (u16)gui->nextId++;
    h->next = gui->handles;
    gui->handles = h;
    gui->count++;
    return h->id;
}

static void gui_forget(Runtime *r, TaskGui *gui, u16 id)
{
    for (;;)
    {
        GuiHandle *child = gui->handles;
        while (child && child->parent != id)
        {
            child = child->next;
        }
        if (!child)
        {
            break;
        }
        gui_forget(r, gui, child->id);
    }
    GuiHandle **p = &gui->handles;
    while (*p)
    {
        GuiHandle *h = *p;
        if (h->id == id)
        {
            *p = h->next;
            r->release(r->storage, h);
            gui->count--;
            return;
        }
        p = &h->next;
    }
}

static const GuiPlan *gui_plan(u16 module, u16 ordinal)
{
    for (u32 i = 0; i < sizeof(gui_plans) / sizeof(gui_plans[0]); i++)
    {
        if (gui_plans[i].module == module && gui_plans[i].ordinal == ordinal)
        {
            return gui_plans + i;
        }
    }
    return 0;
}

static u16 *gui_wide_string(Runtime *r, u32 pointer)
{
    static const u16 cp1252[] = {0x20ac, 0x81, 0x201a, 0x192, 0x201e, 0x2026, 0x2020, 0x2021,
                                 0x2c6, 0x2030, 0x160, 0x2039, 0x152, 0x8d, 0x17d, 0x8f,
                                 0x90, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2013, 0x2014,
                                 0x2dc, 0x2122, 0x161, 0x203a, 0x153, 0x9d, 0x17e, 0x178};
    u32 length = 0;
    u8 *text = kernel_string(r, pointer, &length);
    if (!text)
    {
        return 0;
    }
    u16 *wide = r->alloc(r->storage, (length + 1) * 2);
    if (!wide)
    {
        return 0;
    }
    for (u32 i = 0; i <= length; i++)
    {
        wide[i] = text[i] >= 0x80 && text[i] < 0xa0
            ? cp1252[text[i] - 0x80]
            : text[i];
    }
    return wide;
}

ABI NOINLINE u16 __win16_gui_scalar(Runtime *r, State *s, u16 module, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    const GuiPlan *p = gui_plan(module, ordinal);
    TaskGui *gui = r->gui;
    if (!p || !gui || !gui->call)
    {
        r->error = 14;
        return 0;
    }
    u32 words[11] = {0}, result = p->failure;
    iptr args[11] = {0};
    u16 *strings[11] = {0};
    int valid = 1;
    for (u32 i = 0; i < p->count; i++)
    {
        u16 offset = (u16)(s->sp + 4 + p->words[i] * 2);
        words[i] = p->widths[i] == 4
            ? __win16_read_dword(r, s->ss, offset)
            : __win16_read_word(r, s->ss, offset);
    }
    if (r->error)
    {
        return 0;
    }
    for (u32 i = 0; i < p->count; i++)
    {
        u16 type = p->types[i], kind = type & 15;
        u32 value = words[i];
        if (kind == 1 || kind == 3)
        {
            args[i] = value;
        }
        else if (kind == 2)
        {
            args[i] = (short)value;
        }
        else if (kind == 4)
        {
            strings[i] = gui_wide_string(r, value);
            if (!strings[i])
            {
                valid = 0;
                break;
            }
            args[i] = (iptr)strings[i];
        }
        else if (!value && (type & 16))
        {
            args[i] = 0;
        }
        else
        {
            GuiHandle *h = value <= 65535
                ? gui_handle(gui, (u16)value, kind)
                : 0;
            if (!h || ((type & 32) && !h->owned))
            {
                valid = 0;
                break;
            }
            args[i] = h->native;
        }
    }
    if (valid && !r->error)
    {
        iptr native = gui->call(gui->context, module, ordinal, args, p->count);
        /* Win32 NUMCOLORS=-1 denotes true color; preserve signed Win16 comparisons. */
        if (module == 3 && ordinal == 80 && args[1] == 24 && (int)native == -1)
        {
            native = 32767;
        }
        if ((p->result >= 5 && p->result <= 9) || p->result == 38)
        {
            u16 kind = p->result & 15, owned = !!(p->result & 32),
                parent = module == 2 && (ordinal == 91 || ordinal == 159)
                    ? (u16)words[0]
                    : 0;
            result = gui_add(r, gui, native, kind, owned, parent);
            if (owned && native && !result)
            {
                iptr cleanup[] = {native};
                gui->call(gui->context, 3, 68, cleanup, 1);
            }
        }
        else if (p->result == 40)
        {
            int value = (int)native;
            result = (u16)(value >= -32768 && value <= 32767
                ? value
                : -1);
        }
        else
        {
            result = (u32)native;
        }
        if (p->release >= 0 && native)
        {
            gui_forget(r, gui, (u16)words[p->release]);
        }
    }
    for (u32 i = 0; i < p->count; i++)
    {
        if (strings[i])
        {
            r->release(r->storage, strings[i]);
        }
    }
    if (!r->error && p->result)
    {
        s->ax = (u16)result;
        if (p->result == 2)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    u16 bytes = 0;
    for (u32 i = 0; i < p->count; i++)
    {
        bytes += p->widths[i];
    }
    return bytes;
}
static void destroy_task_ui(Runtime *r);

static void destroy_task_gui(Runtime *r)
{
    TaskGui *gui = r->gui;
    if (!gui)
    {
        return;
    }
    destroy_task_ui(r);
    for (u16 kind = 5; kind <= 9; kind++)
    {
        GuiHandle *h = gui->handles;
        while (h)
        {
            u16 id = h->id;
            iptr native = h->native;
            u16 owned = h->owned, type = h->kind;
            h = h->next;
            if (type != kind || !owned)
            {
                continue;
            }
            iptr args[] = {native};
            if (type == 5)
            {
                gui->call(gui->context, 2, 53, args, 1);
            }
            else if (type == 6)
            {
                gui->call(gui->context, 3, 68, args, 1);
            }
            else if (type == 7)
            {
                gui->call(gui->context, 3, 69, args, 1);
            }
            gui_forget(r, gui, id);
            h = gui->handles;
        }
    }
    while (gui->handles)
    {
        gui_forget(r, gui, gui->handles->id);
    }
    if (r->ownsGui)
    {
        r->release(r->storage, gui);
    }
    r->gui = 0;
    r->ownsGui = 0;
}
