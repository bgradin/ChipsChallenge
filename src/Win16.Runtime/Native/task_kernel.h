/* Whole-task memory/string API adapters. Import identities and stack signatures
 * come from Win16ExportContracts.json; no original instructions are interpreted. */
#include "import_contracts.h"

static const KernelApiContract *kernel_contract(u16 ordinal)
{
    for (u32 i = 0; i < sizeof(kernel_api_contracts) / sizeof(kernel_api_contracts[0]); i++)
    {
        if (kernel_api_contracts[i].ordinal == ordinal)
        {
            return kernel_api_contracts + i;
        }
    }
    return 0;
}

static u32 kernel_argument(Runtime *r, State *s, const KernelApiContract *c, u16 index)
{
    if (!c || index >= c->count)
    {
        r->error = 14;
        return 0;
    }
    u16 offset = (u16)(s->sp + 4 + c->words[index] * 2);
    return c->widths[index] == 4
        ? __win16_read_dword(r, s->ss, offset)
        : __win16_read_word(r, s->ss, offset);
}

static u8 *kernel_string(Runtime *r, u32 pointer, u32 *length)
{
    Segment *s = segment(r, (u16)(pointer >> 16));
    u32 offset = (u16)pointer;
    if (!s || !(s->permissions & 1) || offset >= s->size)
    {
        r->error = 9;
        return 0;
    }
    for (u32 i = offset; i < s->size; i++)
    {
        if (!s->bytes[i])
        {
            *length = i - offset;
            return s->bytes + offset;
        }
    }
    r->error = 9;
    return 0;
}

static int kernel_local_context(Runtime *r, State *s)
{
    Segment *a = segment(r, s->ds), *b = r->local
        ? segment(r, r->local->selector)
        : 0;
    if (!a || !b || a->backing != b->backing || a->origin != b->origin || a->size != b->size)
    {
        r->error = 14;
        return 0;
    }
    return 1;
}

ABI NOINLINE u16 __win16_kernel_service(Runtime *r, State *s, u16 ordinal)
{
    if (r->error)
    {
        return 0;
    }
    r->wholeMemory = 1;
    const KernelApiContract *contract = kernel_contract(ordinal);
    if (!contract)
    {
        r->error = 14;
        return 0;
    }
    u32 result = 0, a, b, c, length = 0;
    u16 cleanup = 0, wideResult = 0;
#define KARG(index) kernel_argument(r, s, contract, index)
    if (ordinal == 5 || ordinal == 6 || ordinal == 7 || ordinal == 10)
    {
        if (!kernel_local_context(r, s))
        {
            return 0;
        }
        if (ordinal == 5)
        {
            a = KARG(0);
            b = KARG(1);
            cleanup = 4;
            if (r->error)
            {
                return 0;
            }
            result = local_allocate(r, r->local, b, (u16)a);
            if (!result && b <= 65534 && !(a & ~0x60u) &&
                resize_data(r, r->local->selector, 65536) && local_extend(r, r->local, 65536))
            {
                result = local_allocate(r, r->local, b, (u16)a);
            }
        }
        else if (ordinal == 6)
        {
            a = KARG(0);
            b = KARG(1);
            c = KARG(2);
            cleanup = 6;
            if (r->error)
            {
                return 0;
            }
            result = local_reallocate(r, r->local, (u16)a, b, (u16)c);
            if (!result && b && b <= 65534 && !(c & ~0x62u) && local_size(r->local, (u16)a) &&
                resize_data(r, r->local->selector, 65536) && local_extend(r, r->local, 65536))
            {
                result = local_reallocate(r, r->local, (u16)a, b, (u16)c);
            }
        }
        else
        {
            a = KARG(0);
            cleanup = 2;
            if (r->error)
            {
                return 0;
            }
            result = ordinal == 10
                ? local_size(r->local, (u16)a)
                : (!a || local_free(r, r->local, (u16)a)
                        ? 0
                        : a);
        }
    }
    else if (ordinal >= 15 && ordinal <= 20)
    {
        if (ordinal == 15)
        {
            a = KARG(0);
            b = KARG(1);
            cleanup = 6;
            if (r->error)
            {
                return 0;
            }
            result = allocate(r, b, (u16)a);
        }
        else if (ordinal == 16)
        {
            a = KARG(0);
            b = KARG(1);
            c = KARG(2);
            cleanup = 8;
            if (r->error)
            {
                return 0;
            }
            result = reallocate(r, (u16)a, b, (u16)c);
        }
        else
        {
            a = KARG(0);
            cleanup = 2;
            if (r->error)
            {
                return 0;
            }
            Allocation *block = allocation(r, (u16)a);
            if (ordinal == 17)
            {
                if (block)
                {
                    block->locks = 0;
                }
                result = !block || free_memory(r, (u16)a)
                    ? 0
                    : a;
            }
            if (ordinal == 18)
            {
                result = (u32)lock_memory(r, (u16)a) << 16;
                wideResult = 1;
            }
            if (ordinal == 19)
            {
                result = unlock_memory(r, (u16)a);
            }
            if (ordinal == 20)
            {
                result = block
                    ? block->size
                    : 0;
                wideResult = 1;
            }
        }
    }
    else if (ordinal == 23 || ordinal == 24)
    {
        a = KARG(0);
        cleanup = 2;
        if (r->error)
        {
            return 0;
        }
        if (a == 0xffff)
        {
            a = s->ds;
        }
        Segment *v = find_segment(r, (u16)a);
        Allocation *block = allocation(r, (u16)a);
        if (block)
        {
            if (ordinal == 23)
            {
                result = lock_memory(r, (u16)a)
                    ? a
                    : 0;
            }
            else
            {
                unlock_memory(r, (u16)a);
                return cleanup;
            }
        }
        else if (v)
        {
            if (ordinal == 23)
            {
                if (v->locks < 65535)
                {
                    v->locks++;
                    result = a;
                }
            }
            else
            {
                if (v->locks)
                {
                    v->locks--;
                }
                return cleanup;
            }
        }
        else if (ordinal == 24)
        {
            return cleanup;
        }
    }
    else if (ordinal == 88 || ordinal == 90)
    {
        a = kernel_argument(r, s, contract, ordinal == 88
            ? 1
            : 0);
        cleanup = ordinal == 88
            ? 8
            : 4;
        if (r->error)
        {
            return 0;
        }
        u8 *source = kernel_string(r, a, &length);
        if (!source)
        {
            return 0;
        }
        if (ordinal == 90)
        {
            result = length;
        }
        else
        {
            b = KARG(0);
            if (r->error)
            {
                return 0;
            }
            Segment *v = segment(r, (u16)(b >> 16));
            u32 offset = (u16)b;
            if (!v || length + 1 > v->size || offset > v->size - length - 1)
            {
                r->error = 9;
                return 0;
            }
            if (!writable(r, v, (u16)offset, length + 1))
            {
                return 0;
            }
            u8 *target = v->bytes + offset;
            /* Integer host addresses compare storage views without undefined pointer ordering. */
            if ((unsigned long long)target > (unsigned long long)source)
            {
                for (u32 i = length + 1; i > 0; i--)
                {
                    target[i - 1] = source[i - 1];
                }
            }
            else
            {
                for (u32 i = 0; i <= length; i++)
                {
                    target[i] = source[i];
                }
            }
            result = b;
            wideResult = 1;
        }
    }
    else if (ordinal == 107)
    {
        a = KARG(0);
        cleanup = 2;
        if (r->error)
        {
            return 0;
        }
        result = r->errorMode;
        r->errorMode = (u16)a;
    }
    else if (ordinal == 131)
    {
        if (!r->environmentSelector && r->count < r->maxSegments)
        {
            u16 id = next_selector(r);
            Segment *v = id
                ? new_segment(r, id, 8)
                : 0;
            if (v)
            {
                v->bytes[2] = 1;
                v->permissions = 1;
                attach(r, v);
                r->environmentSelector = id;
            }
        }
        result = (u32)r->environmentSelector << 16;
        wideResult = 1;
    }
    else
    {
        r->error = 14;
        return 0;
    }
    if (cleanup != contract->cleanup)
    {
        r->error = 14;
        return 0;
    }
    if (!r->error)
    {
        s->ax = (u16)result;
        if (wideResult)
        {
            s->dx = (u16)(result >> 16);
        }
    }
    return cleanup;
#undef KARG
}

ABI NOINLINE u16 __win16_user_string_compare(Runtime *r, State *s)
{
    u32 left = __win16_read_dword(r, s->ss, (u16)(s->sp + 8)),
        right = __win16_read_dword(r, s->ss, (u16)(s->sp + 4)), a = 0, b = 0;
    if (r->error)
    {
        return 0;
    }
    u8 *x = kernel_string(r, left, &a), *y = kernel_string(r, right, &b);
    if (!x || !y)
    {
        return 0;
    }
    for (u32 n = 0; n < a; n++)
    {
        if (x[n] >= 128)
        {
            r->error = 14;
            return 0;
        }
    }
    for (u32 n = 0; n < b; n++)
    {
        if (y[n] >= 128)
        {
            r->error = 14;
            return 0;
        }
    }
    int value = 0;
    u32 n = 0;
    while (n < a && n < b)
    {
        u8 c = x[n], d = y[n];
        if (c >= 'a' && c <= 'z')
        {
            c -= 32;
        }
        if (d >= 'a' && d <= 'z')
        {
            d -= 32;
        }
        if (c != d)
        {
            value = c < d
                ? -1
                : 1;
            break;
        }
        n++;
    }
    if (!value && a != b)
    {
        value = a < b
            ? -1
            : 1;
    }
    s->ax = (u16)value;
    return 8;
}
