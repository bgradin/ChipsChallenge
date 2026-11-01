/* Generated Win32 integer/pointer call adapter. */
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetCapture(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_ReleaseCapture(void);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetFocus(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_IsIconic(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_EnableWindow(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetWindowTextW(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_ShowWindow(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetCursor(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_GetDlgItem(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_MessageBeep(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_GetKeyState(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_UpdateWindow(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_CheckMenuItem(iptr, iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_EnableMenuItem(iptr, iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_GetSubMenu(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_DrawMenuBar(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_DestroyWindow(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetBkColor(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetBkMode(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SetTextColor(iptr, iptr);
extern __attribute__((visibility("hidden")))
ABI iptr gui_native_PatBlt(iptr, iptr, iptr, iptr, iptr, iptr);
extern __attribute__((visibility("hidden")))
ABI iptr gui_native_BitBlt(iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr);
extern __attribute__((visibility("hidden")))
ABI iptr gui_native_StretchBlt(iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_SelectObject(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_CreateCompatibleDC(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_GetDeviceCaps(iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_GetStockObject(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_MulDiv(iptr, iptr, iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_DeleteDC(iptr);
extern __attribute__((visibility("hidden"))) ABI iptr gui_native_DeleteObject(iptr);

static iptr windows_gui_call(void *context, u16 module, u16 ordinal, const iptr *args, u16 count)
{
    if (module == 2 && ordinal == 18)
    {
        return gui_native_SetCapture(args[0]);
    }
    if (module == 2 && ordinal == 19)
    {
        return gui_native_ReleaseCapture();
    }
    if (module == 2 && ordinal == 22)
    {
        return gui_native_SetFocus(args[0]);
    }
    if (module == 2 && ordinal == 31)
    {
        return gui_native_IsIconic(args[0]);
    }
    if (module == 2 && ordinal == 34)
    {
        return gui_native_EnableWindow(args[0], args[1]);
    }
    if (module == 2 && ordinal == 37)
    {
        return gui_native_SetWindowTextW(args[0], args[1]);
    }
    if (module == 2 && ordinal == 42)
    {
        return gui_native_ShowWindow(args[0], args[1]);
    }
    if (module == 2 && ordinal == 69)
    {
        return gui_native_SetCursor(args[0]);
    }
    if (module == 2 && ordinal == 91)
    {
        return gui_native_GetDlgItem(args[0], args[1]);
    }
    if (module == 2 && ordinal == 104)
    {
        return gui_native_MessageBeep(args[0]);
    }
    if (module == 2 && ordinal == 106)
    {
        return gui_native_GetKeyState(args[0]);
    }
    if (module == 2 && ordinal == 124)
    {
        return gui_native_UpdateWindow(args[0]);
    }
    if (module == 2 && ordinal == 154)
    {
        return gui_native_CheckMenuItem(args[0], args[1], args[2]);
    }
    if (module == 2 && ordinal == 155)
    {
        return gui_native_EnableMenuItem(args[0], args[1], args[2]);
    }
    if (module == 2 && ordinal == 159)
    {
        return gui_native_GetSubMenu(args[0], args[1]);
    }
    if (module == 2 && ordinal == 160)
    {
        return gui_native_DrawMenuBar(args[0]);
    }
    if (module == 2 && ordinal == 53)
    {
        return gui_native_DestroyWindow(args[0]);
    }
    if (module == 3 && ordinal == 1)
    {
        return gui_native_SetBkColor(args[0], args[1]);
    }
    if (module == 3 && ordinal == 2)
    {
        return gui_native_SetBkMode(args[0], args[1]);
    }
    if (module == 3 && ordinal == 9)
    {
        return gui_native_SetTextColor(args[0], args[1]);
    }
    if (module == 3 && ordinal == 29)
    {
        return gui_native_PatBlt(args[0], args[1], args[2], args[3], args[4], args[5]);
    }
    if (module == 3 && ordinal == 34)
    {
        return gui_native_BitBlt(args[0], args[1], args[2], args[3], args[4], args[5], args[6],
                                 args[7], args[8]);
    }
    if (module == 3 && ordinal == 35)
    {
        return gui_native_StretchBlt(args[0], args[1], args[2], args[3], args[4], args[5], args[6],
                                     args[7], args[8], args[9], args[10]);
    }
    if (module == 3 && ordinal == 45)
    {
        return gui_native_SelectObject(args[0], args[1]);
    }
    if (module == 3 && ordinal == 52)
    {
        return gui_native_CreateCompatibleDC(args[0]);
    }
    if (module == 3 && ordinal == 80)
    {
        return gui_native_GetDeviceCaps(args[0], args[1]);
    }
    if (module == 3 && ordinal == 87)
    {
        return gui_native_GetStockObject(args[0]);
    }
    if (module == 3 && ordinal == 128)
    {
        return gui_native_MulDiv(args[0], args[1], args[2]);
    }
    if (module == 3 && ordinal == 68)
    {
        return gui_native_DeleteDC(args[0]);
    }
    if (module == 3 && ordinal == 69)
    {
        return gui_native_DeleteObject(args[0]);
    }
    return 0;
}
