/* Host-runnable differential tests against actual host CMP flags, using the
 * same freestanding helper source and segmented storage embedded into the PE. */
#include "acceptance.c"

static unsigned long host_cmp(u16 a, u16 b, int word)
{
    unsigned long flags;
    if (word)
    {
        __asm__ volatile("cmpw %w2,%w1; pushfq; popq %0" : "=r"(flags) : "a"(a), "d"(b) : "cc");
    }
    else
    {
        __asm__ volatile("cmpb %b2,%b1; pushfq; popq %0" : "=r"(flags) : "a"(a), "d"(b) : "cc");
    }
    return flags & 0x8D5;
}

int main(void)
{
    static FixtureStorage storage;
    Runtime r;
    State s;
    init(&r, &s, &storage);
    s.es = 0x3000;
    const u16 pairs[][2] = {{0, 0}, {0, 1}, {0x10, 1}, {0x80, 1}, {0x7f, 0xff}, {0x8000, 1}, {0x7fff, 0xffff}, {0xffff, 0xff}};
    for (u32 word = 0; word < 2; word++)
    {
        for (u32 backwards = 0; backwards < 2; backwards++)
        {
            for (u32 n = 0; n < sizeof(pairs) / sizeof(pairs[0]); n++)
            {
                u16 a = pairs[n][0], b = pairs[n][1];
                __win16_write_word(&r, 0x1000, 0xFFFF, a);
                __win16_write_word(&r, 0x3000, 0xFFFF, b);
                s.si = s.di = 0xFFFF;
                s.cx = 3;
                s.ax = 0x5678;
                s.flags = 0x302 | (backwards
                    ? 0x400
                    : 0);
                u16 sp = s.sp;
                __win16_string(&r, &s, word
                    ? 7
                    : 6, 0);
                u16 expected = (u16)(0xFFFF + (backwards
                    ? -1
                    : 1) * (word
                    ? 2
                    : 1));
                if (r.error || s.si != expected || s.di != expected || s.cx != 3 ||
                    s.ax != 0x5678 || s.sp != sp)
                {
                    return 1;
                }
                if ((s.flags & 0x8D5) != host_cmp(a, b, word) ||
                    (s.flags & 0x700) != (0x300 | (backwards
                        ? 0x400
                        : 0)))
                {
                    return 2;
                }
                if (__win16_read_word(&r, 0x1000, 0xFFFF) != a ||
                    __win16_read_word(&r, 0x3000, 0xFFFF) != b)
                {
                    return 3;
                }
            }
        }
    }
    for (u32 word = 0; word < 2; word++)
    {
        for (u32 backwards = 0; backwards < 2; backwards++)
        {
            for (u32 repne = 0; repne < 2; repne++)
            {
                int step = (backwards
                    ? -1
                    : 1) *
                    (word
                        ? 2
                        : 1);
                for (u32 n = 0; n < 3; n++)
                {
                    u16 off = (u16)(0xFFFF + (int)n * step);
                    int equal = repne
                        ? n == 1
                        : n != 1;
                    if (word)
                    {
                        __win16_write_word(&r, 0x1000, off, 0x8000);
                        __win16_write_word(&r, 0x3000, off, equal
                            ? 0x8000
                            : 1);
                    }
                    else
                    {
                        __win16_write_byte(&r, 0x1000, off, 0x80);
                        __win16_write_byte(&r, 0x3000, off, equal
                            ? 0x80
                            : 1);
                    }
                }
                s.si = s.di = 0xFFFF;
                s.cx = 3;
                s.flags = 0x202 | (backwards
                    ? 0x400
                    : 0);
                __win16_string(&r, &s, word
                    ? 7
                    : 6, repne
                    ? 3
                    : 2);
                if (r.error || s.cx != 1 || s.si != (u16)(0xFFFF + 2 * step) || s.di != s.si ||
                    !!(s.flags & 64) != !!repne)
                {
                    return 4;
                }
            }
        }
    }
    s.ds = s.es = 0;
    s.cx = 0;
    s.flags = 0xBD7;
    u16 si = s.si, di = s.di;
    __win16_string(&r, &s, 7, 2);
    if (r.error || s.flags != 0xBD7 || s.si != si || s.di != di)
    {
        return 5;
    }
    s.cx = 1;
    __win16_string(&r, &s, 6, 2);
    if (!r.error || s.flags != 0xBD7 || s.si != si || s.di != di || s.cx != 1)
    {
        return 6;
    }
    r.error = 0;
    __win16_interrupt(&r, &s, 0x21, 0);
    if (r.error != 13)
    {
        return 7; /* an unimplemented service must never succeed */
    }
    return 0;
}
