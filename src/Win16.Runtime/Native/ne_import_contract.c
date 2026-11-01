#include "acceptance.c"

int main(void)
{
    static FixtureStorage storage;
    int result = task_kernel_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_resources_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_files_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_profiles_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_system_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_instances_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_gui_contract(&storage);
    if (result)
    {
        return result;
    }
    result = task_ui_structures_contract(&storage);
    return result
        ? result
        : task_ui_windows_contract(&storage);
}
