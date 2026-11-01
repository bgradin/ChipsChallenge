; Synthetic example of a 16-bit direction-code lookup; not extracted game assembly.
func TurnLeft
    sub sp,2
    %arg x:word, y:word, xOut:word, yOut:word
    mov ax,[x]
    shl ax,1
    add ax,[y]
    cmp ax,2
    je .positiveTwo
    cmp ax,1
    je .positiveOne
    cmp ax,-1
    je .negativeOne
    cmp ax,-2
    je .negativeTwo
    jmp .finished
.positiveTwo:
    mov bx,[yOut]
    mov word [bx],-1
    mov bx,[xOut]
    mov word [bx],0
    jmp .finished
.positiveOne:
    mov bx,[yOut]
    mov word [bx],0
    mov bx,[xOut]
    mov word [bx],1
    jmp .finished
.negativeOne:
    mov bx,[yOut]
    mov word [bx],0
    mov bx,[xOut]
    mov word [bx],-1
    jmp .finished
.negativeTwo:
    mov bx,[yOut]
    mov word [bx],1
    mov bx,[xOut]
    mov word [bx],0
.finished:
endfunc
