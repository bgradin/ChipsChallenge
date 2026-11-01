/* Reusable freestanding callback invocation. The Windows signature-specific entry is linked in 3.6.
 * Stack words are supplied in entry order, independent of native pointer representation.
 */
typedef struct
{
    const u16 *words;
    u16 count, dwordReturn;
} CallbackFrame;

ABI NOINLINE u32 __win16_callback_invoke(Runtime *r, State *s, u32 target,
                                         const CallbackFrame *frame)
{
    if (r->error)
    {
        return 0;
    }
    CodeBinding *b = r->code;
    while (b && (b->segment != (u16)(target >> 16) || b->offset != (u16)target))
    {
        b = b->next;
    }
    if (!target || !b || !b->entry || !b->farCall || !frame || !frame->words || frame->count > 16)
    {
        r->error = 11;
        return 0;
    }
    if (r->callbackDepth >= 8)
    {
        r->error = 12;
        return 0;
    }
    u16 selector = 0;
    for (u32 id = 0xD000; id <= 0xEFFF; id++)
    {
        if (!find_segment(r, (u16)id))
        {
            selector = (u16)id;
            break;
        }
    }
    if (!selector)
    {
        r->error = 12;
        return 0;
    }
    Segment *stack = map(r, selector, 65536);
    if (!stack)
    {
        return 0;
    }
    State saved = *s;
    r->callbackDepth++;
    s->ss = selector;
    s->sp = 0xFF00;
    s->high[7] = 0;
    s->bp = 0;
    s->high[6] = 0;
    s->flags &= ~0x400;
    for (u32 i = frame->count; i; i--)
    {
        push(r, s, frame->words[i - 1]);
    }
    __win16_call_target(r, s, target, 1);
    if (!r->error && (s->sp != 0xFF00 || s->ss != selector))
    {
        r->error = 13;
    }
    u32 result = r->error
        ? 0
        : frame->dwordReturn
                ? s->ax | ((u32)s->dx << 16)
                : s->ax;
    *s = saved;
    r->callbackDepth--;
    unmap(r, selector);
    return result;
}
