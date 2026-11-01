; Synthetic compiler acceptance examples. Field bindings are documented in README.md.
SEGMENT CODE

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

; Synthetic segmented-record search using a remaining-record counter.
func FindMonster
    %arg x:word, y:word
    push si
    push di
    mov si,[GameStatePtr]
    mov di,[si+MonsterListLen]
    cmp di,0
    jle .missing
    les bx,[si+MonsterListPtr+FarPtr.Off]
    xor cx,cx
.scan:
    mov ax,[y]
    cmp [es:bx+Monster.y],ax
    jne .advance
    mov ax,[x]
    cmp [es:bx+Monster.x],ax
    je .matched
.advance:
    inc cx
    add bx,Monster_size
    dec di
    jnz .scan
.missing:
    mov ax,-1
    jmp .finished
.matched:
    mov ax,cx
.finished:
    pop di
    pop si
endfunc

; Synthetic movable-buffer example. The initial allocation holds only the new
; elements; subsequent allocations extend the existing element count.
func GrowArray
    %arg hMem:word, ptr:word, lenPtr:word, numToAlloc:word, size:word
    %local byteCount:word, highCount:word, allocated:word
    sub sp,6
    mov bx,[hMem]
    cmp word [bx],0
    jne .existingCount
    mov ax,[numToAlloc]
    jmp .computeBytes
.existingCount:
    mov bx,[lenPtr]
    mov ax,[numToAlloc]
    add ax,[bx]
.computeBytes:
    imul word [size]
    cwd
    mov [byteCount],ax
    mov [highCount],dx
    mov bx,[hMem]
    cmp word [bx],0
    je .createBuffer
    push word [bx]
    call far KERNEL.GlobalUnlock
    mov bx,[hMem]
    push word [bx]
    push word [highCount]
    push word [byteCount]
    push word 2
    call far KERNEL.GlobalReAlloc
    jmp .saveResult
.createBuffer:
    push word 0x42
    push word [highCount]
    push word [byteCount]
    call far KERNEL.GlobalAlloc
.saveResult:
    mov [allocated],ax
    cmp ax,0
    je .refreshPointer
    mov bx,[hMem]
    mov [bx],ax
.refreshPointer:
    mov bx,[hMem]
    push word [bx]
    call far KERNEL.GlobalLock
    mov bx,[ptr]
    mov [bx+2],dx
    mov [bx],ax
    cmp word [allocated],0
    jne .increaseCount
    xor ax,ax
    jmp .finished
.increaseCount:
    mov bx,[lenPtr]
    mov ax,[numToAlloc]
    add [bx],ax
    mov ax,1
.finished:
endfunc
