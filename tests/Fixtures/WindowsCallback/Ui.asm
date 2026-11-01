; Authored synthetic UI exercises. These bodies are not extracted game code.
segment CODE
func IsPointVisible
    %arg x:word, y:word
    %assign %$argsize 0
    mov bx,[FixtureStatePtr]
    mov ax,[x]
    sub ax,[bx+ViewportX]
    cmp ax,0
    jl .outside
    cmp ax,[bx+ViewportWidth]
    jge .outside
    mov ax,[y]
    sub ax,[bx+ViewportY]
    cmp ax,0
    jl .outside
    cmp ax,[bx+ViewportHeight]
    jge .outside
    mov ax,1
    jmp .done
.outside:
    xor ax,ax
.done:
endfunc
func InvertViewportTile
    %arg hdc:word, x:word, y:word
    %assign %$argsize 0
    push word [y]
    push word [x]
    call far IsPointVisible
    add sp,4
    cmp ax,0
    je .done
    push word [hdc]
    mov bx,[FixtureStatePtr]
    mov ax,[x]
    sub ax,[bx+ViewportX]
    sub ax,[bx+PaddingX]
    shl ax,5
    push ax
    mov ax,[y]
    sub ax,[bx+ViewportY]
    sub ax,[bx+PaddingY]
    shl ax,5
    push ax
    push word 32
    push word 32
    push word 0x55
    push word 9
    call far GDI.PatBlt
.done:
endfunc
func SAMPLEWNDPROC
    %arg lParam:dword, wParam:word, uMsg:word, hwnd:word
    %assign %$argsize 10
    cmp word [uMsg],15
    jne .default
    mov ax,10
    xor dx,dx
    mov bx,2
    div bx
    jmp .done
.default:
    push word [hwnd]
    push word [uMsg]
    push word [wParam]
    push word [lParam+2]
    push word [lParam]
    call far USER.DefWindowProc
.done:
endfunc
