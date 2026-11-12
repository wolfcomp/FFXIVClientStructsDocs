# Understanding Calls
The game is compiled with MSVC for Windows x64, which means every function follows the **Microsoft x64 calling convention**. Knowing how this convention moves values through registers before a `call` or `jmp` lets you figure out a function's arguments, return type and even which class it belongs to just by reading the instructions leading up to it.

> [!TIP]
> This page covers the parts most useful for reverse engineering. For the full specification of the registers and calling types, including edge cases not covered here, see Microsoft's [x64 calling convention](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170) documentation.

## Argument registers
The first four arguments are passed in registers, everything after that goes on the stack. Each argument takes a slot based on its position, and the register used for that slot depends on whether the argument is an integer/pointer or a floating point value.

| Argument | Integer / Pointer (64-bit) | 32-bit                | 16-bit               | 8-bit                | Float / Double                                 | Notes                                    |
| -------- | -------------------------- | --------------------- | -------------------- | -------------------- | ---------------------------------------------- | ---------------------------------------- |
| 1        | `rcx`                      | `ecx`                 | `cx`                 | `cl`                 | `xmm0`                                         | `this` for member functions              |
| 2        | `rdx`                      | `edx`                 | `dx`                 | `dl`                 | `xmm1`                                         |                                          |
| 3        | `r8`                       | `r8d`                 | `r8w`                | `r8b`                | `xmm2`                                         |                                          |
| 4        | `r9`                       | `r9d`                 | `r9w`                | `r9b`                | `xmm3`                                         |                                          |
| 5        | `qword ptr [rsp+20h]`      | `dword ptr [rsp+20h]` | `word ptr [rsp+20h]` | `byte ptr [rsp+20h]` | `dword ptr [rsp+20h]` / `qword ptr [rsp+20h]`  | Consecutive stack arguments are spaced 8 bytes apart, regardless of size.   |
| 6        | `qword ptr [rsp+28h]`      | `dword ptr [rsp+28h]` | `word ptr [rsp+28h]` | `byte ptr [rsp+28h]` | `dword ptr [rsp+28h]` / `qword ptr [rsp+28h]`  | (`[rsp+30h]`, `[rsp+38h]`, ...)          |

Stack arguments always get a full 8-byte slot, and smaller values are written to the low bytes at the start of it. That keeps the address the same no matter the size, only the operand size changes (for example `mov byte ptr [rsp+20h], 1` for a `bool` or `movss dword ptr [rsp+28h], xmm0` for a `float`). The addresses are from the caller's point of view right before the `call`; inside the callee they are 8 higher because of the return address (`[rsp+28h]` for the 5th argument at entry).

> [!IMPORTANT]
> Slots are positional, not per type. A function `void Foo(int a, float b, int c)` receives `a` in `rcx`, `b` in `xmm1` and `c` in `r8`. The unused `rdx` and `xmm0` are simply ignored.

Smaller integer types still occupy a full slot, but only the lower part of the register is meaningful, which is why the 32-, 16- and 8-bit register names often show up before a call. The width the compiler writes to hints at the argument's type:

| Instruction                       | Likely argument type                                                   |
| --------------------------------- | ---------------------------------------------------------------------- |
| `mov edx, 1`                      | 32-bit value (`int`, `uint`, an enum)                                  |
| `mov dl, 1`                       | `bool` (`true`) or `byte` constant                                     |
| `setz dl`, `setnz r8b`, ...       | `bool` produced from a comparison                                      |
| `movzx r8d, byte ptr [rbx+30h]`   | `bool` or `byte` loaded from a field                                   |
| `xor edx, edx`                    | Zero of **any** type (`false`, `0`, `nullptr`), not a size hint        |

> [!NOTE]
> Writing a 32-bit register (`edx`) clears the upper 32 bits of the full register (`rdx`), while writing an 8- or 16-bit register (`dl`, `dx`) leaves the rest unchanged. This is why the compiler zeroes with `xor edx, edx` no matter the type, and why a `bool` argument can only be trusted in its lowest byte: the callee will read it with `test dl, dl` or `movzx`, never the full `rdx`.

### Structs passed by value
Structs that are exactly 1, 2, 4 or 8 bytes are passed directly in the register like an integer. Anything else (including most vectors such as `Vector3`) is copied to the stack by the caller and a **pointer** to that copy is passed in the slot instead.

### Variadic functions
For variadic functions (`printf` style) floating point values are passed in **both** the `xmm` register and the matching integer register for the first four slots, so the callee can read them with `va_arg` regardless of type.

## Shadow space
The caller always reserves 32 bytes (`0x20`) on the stack directly above the return address, even if the function takes fewer than four arguments. The callee may use it to spill `rcx`, `rdx`, `r8` and `r9`. This is why the 5th argument lives at `[rsp+20h]` from the caller's perspective, and why you often see the following at the start of a function:

```x86asm
mov     [rsp+8], rbx
mov     [rsp+10h], rsi
push    rdi
sub     rsp, 20h
```

The stack must also be 16-byte aligned at the point of the `call` instruction, so frame sizes will often look slightly larger than needed.

> [!TIP]
> This only covers what you need to read a call site. For the full details of how the stack is laid out and used, see Microsoft's [x64 stack usage](https://learn.microsoft.com/en-us/cpp/build/stack-usage?view=msvc-170) documentation.

## Return values
| Return type                         | Location                                                    |
| ----------------------------------- | ----------------------------------------------------------- |
| Integer, pointer, `bool`, enum      | `rax` (`eax`, `al` for smaller types)                       |
| `float` / `double`                  | `xmm0`                                                      |
| Struct of 1, 2, 4 or 8 bytes        | `rax`                                                       |
| Any other struct                    | Caller passes a hidden pointer, callee returns it in `rax`  |

The hidden return pointer takes the first available argument slot. For a free (static) function this is `rcx`, shifting every real argument one slot to the right. For a **member function** `this` stays in `rcx` and the return pointer goes into `rdx`.

```x86asm
lea     rdx, [rsp+58h+var_28]   ; hidden return buffer
mov     rcx, rbx                ; this
call    SomeClass_GetPosition   ; Vector3* GetPosition(Vector3* ret)
movss   xmm0, dword ptr [rax]   ; reading the returned struct through rax
```

### Return values spread over more than one register
Sometimes a return value seems to come back in `rax` and in one of the argument registers at the same time. There are two common reasons for this.

**The hidden return pointer.** The callee writes the result through the pointer it received in `rcx` or `rdx` and also returns that same pointer in `rax`. Because the caller already knows the address of its own buffer, MSVC may ignore `rax` and read the result through the buffer address, or through a non-volatile register that holds a copy of it. In the example below `rdx` and `rax` hold the same address, so the result appears in both places:

```x86asm
lea     rdx, [rsp+58h+var_28]   ; hidden return buffer
mov     rcx, rbx                ; this
call    SomeClass_GetPosition
movss   xmm0, [rsp+58h+var_28]  ; same data as [rax], read through the buffer instead
movss   xmm1, [rax+4]           ; or through rax, both are valid
```

When you see this, the function returns a struct and the argument register is the return buffer, not a normal parameter.

**`__vectorcall`.** Functions using `__vectorcall` (mostly math helpers working on `__m128` or float vectors) can return a homogeneous vector aggregate, which is a struct of up to four floats, doubles or vectors, split across `xmm0` to `xmm3`:

```x86asm
call    Matrix_GetRow           ; returns a struct of { __m128, __m128 }
movaps  [rbx], xmm0             ; first member
movaps  [rbx+10h], xmm1         ; second member
```

If the caller reads `xmm1` or later right after a call without setting it first, the callee is probably a `__vectorcall` function that returns that kind of struct. `__vectorcall` also passes such arguments in `xmm0` to `xmm5`, so the parameters may not follow the table at the top of this page either.

## Volatile and non-volatile registers
A call is allowed to clobber some registers but must preserve others. This tells you which values can be trusted to survive across a call.

| Volatile (caller-saved)                        | Non-volatile (callee-saved)                          |
| ---------------------------------------------- | ---------------------------------------------------- |
| `rax`, `rcx`, `rdx`, `r8`, `r9`, `r10`, `r11`  | `rbx`, `rbp`, `rdi`, `rsi`, `rsp`, `r12` - `r15`     |
| `xmm0` - `xmm5`                                | `xmm6` - `xmm15`                                     |

In practice this means the compiler moves values it wants to keep (often `this`) into a non-volatile register early on:

```x86asm
mov     rbx, rcx                ; save this
call    SomeHelper
mov     rcx, rbx                ; restore this for the next call
call    SomeClass_DoThing
```

When reading backwards from a call to find where an argument came from, a value in `rbx`, `rsi`, `rdi` or `r12`-`r15` can be traced across earlier calls, while one in `rax`, `rcx`, `rdx`, `r8`-`r11` was set after the last call (or is the return value of it in the case of `rax`).

## Recognizing call types

### Direct calls
```x86asm
mov     r9b, 1                  ; arg4: bool (true)
mov     r8d, 5                  ; arg3: int
xor     edx, edx                ; arg2: 0 of any type (int / bool / pointer)
mov     rcx, rdi                ; arg1: this
call    sub_140123450
```
The target is a fixed address, so this can be turned into a signature or `MemberFunction` directly.

### Virtual calls
```x86asm
mov     rax, [rcx]              ; load vtable from the object (offset 0)
mov     edx, 3                  ; arg2
call    qword ptr [rax+28h]     ; vtable index 0x28 / 8 = 5
```
The first qword of an object with virtual functions is its vtable pointer. The offset used in the `call` divided by 8 gives the virtual function index. Since `rcx` holds the object being dereferenced, you know which class (or base class) the vtable belongs to.

### Calls through a field or global
```x86asm
mov     rax, cs:qword_1420ABCDE ; load a global pointer
mov     rcx, [rax+1A8h]         ; field at offset 0x1A8 of that global
call    sub_140456780
```
Here `rcx` is a pointer stored at offset `0x1A8` of some singleton, which hints at both a struct field and the class of the called function.

### Tail calls with `jmp`
When the last thing a function does is call another function and return its result, the compiler replaces the `call` + `ret` with a single `jmp`:

```x86asm
mov     rcx, [rcx+10h]          ; this->Field10
add     rsp, 20h
pop     rbx
jmp     sub_140789AB0           ; return Field10->Something(...)
```

The register flow before a `jmp` works exactly like a `call`: `rcx`, `rdx`, `r8`, `r9` and `xmm0`-`xmm3` hold the arguments for the target. The difference is that the stack frame is torn down first (`add rsp`, `pop`), so the target returns straight to the original caller. Things to keep in mind:

- Any argument registers the current function did **not** touch are passed through unchanged. A thunk like `add rcx, 18h` / `jmp sub_X` just adjusts `this` (common with multiple inheritance) and forwards every other argument.
- The return type of the current function is the return type of the jump target.
- Virtual tail calls look like `jmp qword ptr [rax+40h]` and are read the same way as virtual calls.
- IDA will sometimes not detect the end of the function correctly at a tail call and merge the target into it. If a function looks unexpectedly large, check for a `jmp` to another function's start.

## Putting it together
Given a call site like the following:

```x86asm
movss   xmm2, cs:flt_141F00000  ; arg3: float
xor     edx, edx                ; arg2: 0 (int / bool / null pointer)
mov     rcx, [rbx+0F8h]         ; arg1: this = owner->Field0F8
mov     rax, [rcx]
call    qword ptr [rax+0B0h]    ; vfunc 22
test    al, al                  ; return value used as bool
```

you can derive that the object at `owner + 0xF8` has a virtual function at index 22 with the signature `bool Func(this, int/bool/pointer, float)`. Each of these pieces maps directly to a field, virtual function and signature you can define in ClientStructs.
