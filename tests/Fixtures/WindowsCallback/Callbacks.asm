; Synthetic Win16 callback fixtures, statically translated by the existing body emitter.
func_nods ENUMCALLBACK
    %assign %$argsize 6
    %arg user:dword, hwnd:word
    mov ax,[hwnd]
    mov [0x100],ax
    mov eax,[user]
    mov [0x102],eax
    inc word [0x106]
    mov ax,1
endfunc

func_nods VALUEWNDPROC
    %assign %$argsize 10
    %arg lParam:dword, wParam:word, uMsg:word, hwnd:word
    mov ax,[hwnd]
    mov [0x110],ax
    mov ax,[uMsg]
    mov [0x112],ax
    mov ax,[wParam]
    mov [0x114],ax
    mov eax,[lParam]
    mov [0x116],eax
    mov ax,0x5678
    mov dx,0x8123
endfunc

; FALSE return must stop Windows enumeration after the first child.
func_nods STOPCALLBACK
    %assign %$argsize 6
    %arg user:dword, hwnd:word
    mov ax,[hwnd]
    mov [0x100],ax
    mov eax,[user]
    mov [0x102],eax
    inc word [0x106]
    xor ax,ax
endfunc
