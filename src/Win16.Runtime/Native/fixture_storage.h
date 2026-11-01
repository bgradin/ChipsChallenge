/* A bounded, reclaiming allocator used only by the deterministic standalone fixture.
 * The runtime itself accepts any allocator (future Windows heap bindings can use the same
 * interface).
 */
typedef struct Block
{
    unsigned long long size;
    struct Block *next;
    u32 available;
} Block;
typedef union
{
    unsigned long long alignment;
    u8 bytes[512 * 1024];
} FixtureStorage;

static void init_storage(FixtureStorage *storage)
{
    Block *b = (Block *)storage->bytes;
    b->size = sizeof(storage->bytes) - sizeof(Block);
    b->next = (Block *)0;
    b->available = 1;
}

static void *fixture_alloc(void *context, u32 bytes)
{
    unsigned long long size = ((unsigned long long)bytes + 7) & ~7ull;
    for (Block *b = (Block *)context; b; b = b->next)
    {
        if (b->available && b->size >= size)
        {
            if (b->size >= size + sizeof(Block) + 8)
            {
                Block *n = (Block *)((u8 *)(b + 1) + size);
                n->size = b->size - size - sizeof(Block);
                n->next = b->next;
                n->available = 1;
                b->next = n;
                b->size = size;
            }
            b->available = 0;
            return b + 1;
        }
    }
    return (void *)0;
}

static void fixture_release(void *context, void *pointer)
{
    if (!pointer)
    {
        return;
    }
    ((Block *)pointer - 1)->available = 1;
    for (Block *b = (Block *)context; b && b->next;)
    {
        if (b->available && b->next->available)
        {
            b->size += sizeof(Block) + b->next->size;
            b->next = b->next->next;
        }
        else
        {
            b = b->next;
        }
    }
}
