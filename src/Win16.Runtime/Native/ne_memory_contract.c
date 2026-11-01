#include "acceptance.c"

int main(void)
{
    static FixtureStorage storage;
    int legacy = memory_contract(&storage);
    return legacy
        ? legacy
        : program_memory_contract(&storage);
}
