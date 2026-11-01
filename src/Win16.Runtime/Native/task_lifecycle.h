typedef struct TaskLifecycle
{
    TaskUi *ui;
    u32 events, vectors[256];
    u16 psp, show, stackSize;
    u32 initialized;
    iptr event;
    const u8 *command;
} TaskLifecycle;

static void task_terminate(Runtime *r, u16 code, u32 diagnostic)
{
    r->terminated = 1;
    r->exitCode = code;
    r->diagnostic = diagnostic;
    r->error = 16;
}

static void task_dos(Runtime *r, State *s)
{
    TaskLifecycle *task = r->lifecycle;
    if (!task)
    {
        r->error = 13;
        return;
    }
    u16 ah = s->ax >> 8;
    u8 vector = (u8)s->ax;
    if (ah == 0x30)
    {
        s->ax = 5;
        s->bx = 0xff00;
        s->cx = 0;
    }
    else if (ah == 0x35)
    {
        s->es = (u16)(task->vectors[vector] >> 16);
        s->bx = (u16)task->vectors[vector];
    }
    else if (ah == 0x25)
    {
        u32 target = (u32)s->ds << 16 | s->dx;
        const ProgramBinding *binding = target
            ? program_resolve(r, target)
            : 0;
        if (target && (!binding || binding->kind != 1))
        {
            r->error = 8;
            return;
        }
        task->vectors[vector] = target;
    }
    else if (ah == 0x4c)
    {
        task_terminate(r, (u8)s->ax, 0);
        return;
    }
    else
    {
        r->error = 14;
        return;
    }
    s->flags &= ~1u;
}

ABI NOINLINE int __win16_install_task(Runtime *r, u16 instance, u16 show, u16 stackSize,
                                      const u8 *command)
{
    if (r->lifecycle || !r->gui || !r->gui->ui || r->gui->ui->instance != instance)
    {
        return 0;
    }
    TaskLifecycle *task = r->alloc(r->storage, sizeof(*task));
    if (!task)
    {
        return 0;
    }
    zero(task, sizeof(*task));
    task->ui = r->gui->ui;
    task->show = show;
    task->stackSize = stackSize;
    task->command = command;
    task->events = 1;
    task->event = UI_CALL(task->ui, CreateEventW, 0, 0, 0, 0);
    if (!task->event)
    {
        r->release(r->storage, task);
        return 0;
    }
    r->lifecycle = task;
    return 1;
}

ABI NOINLINE int __win16_post_task_event(Runtime *r)
{
    TaskLifecycle *task = r->lifecycle;
    if (!task)
    {
        return 0;
    }
    if (task->events < 0xffffffff)
    {
        task->events++;
    }
    return !!UI_CALL(task->ui, SetEvent, task->event);
}

ABI NOINLINE u16 __win16_task_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    TaskLifecycle *task = r->lifecycle;
    const KernelApiContract *c = kernel_contract(ordinal);
    if (!task || !c)
    {
        r->error = 14;
        return 0;
    }
    if (ordinal == 1)
    {
        task_terminate(r, (u16)kernel_argument(r, s, c, 0), 0);
    }
    else if (ordinal == 137)
    {
        u32 diagnostic = kernel_argument(r, s, c, 1), length = 0;
        if (!kernel_string(r, diagnostic, &length))
        {
            return 0;
        }
        task_terminate(r, 255, diagnostic);
    }
    else if (ordinal == 102)
    {
        task_dos(r, s);
    }
    else if (ordinal == 30)
    {
        u16 id = (u16)kernel_argument(r, s, c, 0);
        if (id && id != task->ui->instance)
        {
            r->error = 14;
            return 0;
        }
        if (task->events)
        {
            task->events--;
            s->ax = 0;
        }
        else
        {
            if (UI_CALL(task->ui, WaitForSingleObject, task->event, 0xffffffffu) != 0)
            {
                r->error = 14;
                return 0;
            }
            if (task->events)
            {
                task->events--;
            }
            s->ax = 1;
        }
    }
    else if (ordinal == 91)
    {
        u16 ds = task->ui->instance;
        if (task->initialized)
        {
            s->ax = 0;
            return c->cleanup;
        }
        if (s->ds != ds || s->ss != ds)
        {
            r->error = 14;
            return 0;
        }
        u32 commandLength = 0;
        if (task->command)
        {
            while (commandLength < 127 && task->command[commandLength])
            {
                commandLength++;
            }
        }
        if (commandLength > 126)
        {
            r->error = 9;
            return 0;
        }
        u16 selector = next_selector(r);
        if (!selector)
        {
            s->ax = 0;
            return c->cleanup;
        }
        Segment *psp = map(r, selector, 256);
        if (!psp)
        {
            s->ax = 0;
            return c->cleanup;
        }
        task->psp = selector;
        State environment = *s;
        __win16_kernel_service(r, &environment, 131);
        if (r->error)
        {
            return 0;
        }
        ui_put16(psp->bytes + 0x2c, environment.dx);
        psp->bytes[0x80] = (u8)commandLength;
        for (u32 i = 0; i < commandLength; i++)
        {
            psp->bytes[0x81 + i] = task->command[i];
        }
        psp->bytes[0x81 + commandLength] = 13;
        u16 minimum = (u16)(s->sp + 4), stackSize = s->bx
            ? s->bx
            : task->stackSize;
        u32 limit = (minimum > stackSize
            ? minimum - stackSize
            : 0) +
            150;
        if (limit > 65535)
        {
            r->error = 14;
            return 0;
        }
        u8 *instance = ui_buffer(r, (u32)ds << 16 | 10, 6, 1);
        if (!instance)
        {
            return 0;
        }
        ui_put16(instance, (u16)limit);
        ui_put16(instance + 2, minimum);
        ui_put16(instance + 4, minimum);
        u16 old = s->sp;
        u32 address = __win16_read_dword(r, s->ss, old);
        __win16_write_dword(r, s->ss, (u16)(old - 2), address);
        __win16_write_word(r, s->ss, (u16)(old + 2), 0);
        if (r->error)
        {
            return 0;
        }
        s->sp = (u16)(old - 2);
        s->ax = 1;
        s->cx = (u16)limit;
        s->dx = task->show;
        s->si = 0;
        s->di = ds;
        s->es = selector;
        s->bx = commandLength
            ? 0x81
            : 0x80;
        task->initialized = 1;
    }
    else
    {
        r->error = 14;
    }
    return c->cleanup;
}

static void destroy_task_lifecycle(Runtime *r)
{
    TaskLifecycle *task = r->lifecycle;
    if (!task)
    {
        return;
    }
    if (task->event)
    {
        UI_CALL(task->ui, CloseHandle, task->event);
    }
    r->release(r->storage, task);
    r->lifecycle = 0;
}
