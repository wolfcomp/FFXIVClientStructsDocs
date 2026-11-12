# Instruction Semantics
Where [Understanding Calls](understanding-calls.md) covers how values move between functions, this page covers how the instructions themselves are encoded and the patterns MSVC tends to emit for x64. Knowing which bytes of an instruction are the opcode and which are a value baked into the executable is what lets you write signatures that survive game updates.

> [!TIP]
> For the full encoding rules see the [Intel® 64 and IA-32 Architectures Software Developer's Manual](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html) (Volume 2). IDA can show the bytes next to each instruction by setting **Options > General > Number of opcode bytes** to something like `12`.

## Anatomy of an instruction
An x64 instruction is variable length (1 to 15 bytes) and is built from the following parts, in order. Only the opcode is always present.

| Part         | Size       | Example                | Meaning                                                                     |
| ------------ | ---------- | ---------------------- | --------------------------------------------------------------------------- |
| Prefixes     | 0-4 bytes  | `66`, `F3`, `F2`       | Operand size override, SSE instruction selection (`F3` = `ss`, `F2` = `sd`) |
| REX prefix   | 0-1 byte   | `48`, `4C`, `49`, `40` | 64-bit operand size (`W`) and access to `r8`-`r15` (`R`, `X`, `B`)          |
| Opcode       | 1-3 bytes  | `8B`, `E8`, `0F 10`    | The operation itself                                                        |
| ModR/M       | 0-1 byte   | `0D`, `89`, `4C`       | Which registers are used and whether a displacement follows                 |
| SIB          | 0-1 byte   | `24`                   | Scale/index/base, needed for `rsp` based addressing such as `[rsp+20h]`     |
| Displacement | 0, 1 or 4  | `20`, `A8 01 00 00`    | Offset added to the address (`[rcx+1A8h]`)                                  |
| Immediate    | 0, 1, 2, 4 | `05 00 00 00`          | Constant operand (`mov edx, 5`), 8 bytes only for `mov r64, imm64`          |

The opcode, REX, ModR/M and SIB bytes describe **what** the instruction does. The displacement and immediate bytes are the **values** that the compiler and linker filled in. All multi-byte values are stored little endian, so `0x1A8` is written as `A8 01 00 00`.

### REX prefixes
The REX prefix is any byte from `40` to `4F`. The ones you will see the most:

| Byte | Meaning                                                   | Example                               |
| ---- | --------------------------------------------------------- | ------------------------------------- |
| `48` | 64-bit operands                                           | `48 8B C8` → `mov rcx, rax`           |
| `4C` | 64-bit, `r8`-`r15` in the ModR/M `reg` field              | `4C 8B C0` → `mov r8, rax`            |
| `49` | 64-bit, `r8`-`r15` in the ModR/M `rm` field               | `49 8B C8` → `mov rcx, r8`            |
| `44` | 32-bit, `r8d`-`r15d` in the `reg` field                   | `44 8B C0` → `mov r8d, eax`           |
| `41` | 32-bit, `r8d`-`r15d` in the `rm` field                    | `41 FF C0` → `inc r8d`                |
| `40` | No change, only needed to reach `sil`/`dil` or as padding | `40 53` → `push rbx` (MSVC prologues) |

Because the register is part of these bytes, the same logical code that uses `rbx` in one patch and `rdi` in the next will produce different bytes. Register allocation changing is one of the most common reasons a signature breaks.

### Displacement sizes
The `mod` bits in the ModR/M byte pick how big the displacement is. The compiler always uses the smallest one that fits, so a field offset of `0x7F` or less uses 1 byte and anything larger uses 4 bytes:

```x86asm
48 8B 49 10                 mov     rcx, [rcx+10h]      ; disp8
48 8B 89 A8 01 00 00        mov     rcx, [rcx+1A8h]     ; disp32
48 8B 4C 24 20              mov     rcx, [rsp+20h]      ; disp8 with SIB (24) for rsp
```

> [!NOTE]
> A disp8 is signed, so `[rcx-8]` is encoded as `48 8B 49 F8`. Fields at `0x80` and up therefore always use the 4 byte form. If a struct grows and a field crosses the `0x80` boundary, the instruction changes length and not just its value.

### RIP-relative addressing
x64 has no absolute 32-bit addresses. Globals, string literals, vtables and floating point constants are instead addressed relative to the **end** of the current instruction. In the ModR/M byte this is `mod = 00, rm = 101`, which shows up as the low bits `05`, `0D`, `15`, `1D`, `25`, `2D`, `35` or `3D` depending on the destination register:

```x86asm
48 8B 0D 3A 2F 1C 02        mov     rcx, cs:qword_1420ABCDE   ; rcx = [rip+021C2F3Ah]
48 8D 05 11 22 33 01        lea     rax, off_141333444        ; vtable / string address
F3 0F 10 05 A0 B1 C2 01     movss   xmm0, cs:flt_141F00000    ; float constant
```

The target address is `address of the next instruction + disp32`. Every time code or data is added anywhere in the executable these distances change, so a RIP-relative disp32 is effectively different in every build.

### Relative calls and jumps
Direct calls and jumps work the same way, with the value being relative to the end of the instruction:

| Bytes               | Instruction                     | Value                                                   |
| ------------------- | ------------------------------- | ------------------------------------------------------- |
| `E8 xx xx xx xx`    | `call sub_X`                    | rel32 to another function                               |
| `E9 xx xx xx xx`    | `jmp sub_X`                     | rel32, often a tail call or an incremental-link thunk   |
| `EB xx`             | `jmp short loc_X`               | rel8 inside the same function                           |
| `74 xx` / `75 xx`   | `jz` / `jnz short`              | rel8 inside the same function                           |
| `0F 84 xx xx xx xx` | `jz loc_X`                      | rel32, used when the target is more than 127 bytes away |
| `FF 15 xx xx xx xx` | `call cs:__imp_X`               | RIP-relative disp32 to an import table entry            |
| `FF 50 xx`          | `call qword ptr [rax+xx]`       | disp8 vtable offset                                     |
| `FF 90 xx xx xx xx` | `call qword ptr [rax+xxxxxxxx]` | disp32 vtable offset (index 16 and up)                  |

## Common MSVC patterns
### Prologues and epilogues
MSVC functions start with a fairly fixed sequence: spill non-volatile registers into the shadow space, push the rest and then reserve the stack frame.

```x86asm
48 89 5C 24 08              mov     [rsp+8], rbx
48 89 74 24 10              mov     [rsp+10h], rsi
57                          push    rdi
48 83 EC 20                 sub     rsp, 20h            ; imm8 frame size
```

```x86asm
40 53                       push    rbx                 ; REX padded push, 2 bytes
48 81 EC 80 00 00 00        sub     rsp, 80h            ; imm32 frame size (>= 0x80)
```

Functions with a large stack buffer will also set up the security cookie right after the prologue:

```x86asm
48 8B 05 xx xx xx xx        mov     rax, cs:__security_cookie
48 33 C4                    xor     rax, rsp
48 89 84 24 xx xx xx xx     mov     [rsp+...], rax
```

The epilogue mirrors this with `add rsp, ...`, `pop` and `ret` (`C3`), or ends with a `jmp` for a tail call.

### Padding between functions
Functions are aligned to 16 bytes. The gap between the end of one function and the start of the next is filled with `CC` (`int3`) bytes, and can be anywhere from 0 to 15 bytes long. Inside a function MSVC can also insert multi-byte `nop`s to align loop heads:

```x86asm
0F 1F 40 00                 nop     dword ptr [rax+00h]
0F 1F 44 00 00              nop     dword ptr [rax+rax+00h]
66 0F 1F 84 00 00 00 00 00  nop     word ptr [rax+rax+00000000h]
```

Both kinds of padding depend on the length of the code around them, so they are not reliable signature bytes.

### Other things worth recognizing
| Bytes         | Instruction            | Meaning                                                                                |
| ------------- | ---------------------- | -------------------------------------------------------------------------------------- |
| `33 D2`       | `xor edx, edx`         | Zero a register (see [Understanding Calls](understanding-calls.md#argument-registers)) |
| `84 C0`       | `test al, al`          | Check a `bool` return value                                                            |
| `48 85 C9`    | `test rcx, rcx`        | Null pointer check                                                                     |
| `0F B6 xx`    | `movzx`                | Load a `byte`/`bool` and zero extend                                                   |
| `48 63 xx`    | `movsxd`               | Sign extend an `int` to 64-bit, usually for indexing                                   |
| `48 8D 0C 40` | `lea rcx, [rax+rax*2]` | Arithmetic through `lea`, here `rax * 3` (array of 3-wide elements)                    |
| `0F 4x xx`    | `cmovcc`               | Branchless conditional assignment (`a ? b : c`)                                        |
| `FF 24 xx`    | `jmp [base+reg*4/8]`   | Switch jump table                                                                      |

## Writing signatures
A signature is a byte pattern that has to match exactly one location in the executable. The goal is to keep the bytes that describe **what the code does** and replace the bytes that are **values the compiler or linker filled in** with `??`, since those are the ones that change between patches.

> [!IMPORTANT]
> All signatures in ClientStructs must use `??` for wildcard bytes and include 2 characters per byte. A single `?` or a partial byte such as `4?` is not supported.

### Wildcarding dword sized values
The 4 byte (dword) values embedded in the instruction stream are almost always the ones that change. Instead of copying the value from the disassembly, replace all four bytes with `?? ?? ?? ??`:

| Value                     | Example instruction            | Signature                 | Why it changes                                   |
| ------------------------- | ------------------------------ | ------------------------- | ------------------------------------------------ |
| rel32 of a `call` / `jmp` | `call sub_140123450`           | `E8 ?? ?? ?? ??`          | Distance to the target moves with every build    |
| RIP-relative disp32       | `mov rcx, cs:qword_1420ABCDE`  | `48 8B 0D ?? ?? ?? ??`    | Distance to the global moves with every build    |
| RIP-relative constant     | `movss xmm0, cs:flt_141F00000` | `F3 0F 10 05 ?? ?? ?? ??` | Constants are merged and reordered by the linker |
| Import call               | `call cs:__imp_GetTickCount64` | `FF 15 ?? ?? ?? ??`       | Distance to the import table                     |
| Field disp32              | `mov rcx, [rcx+1A8h]`          | `48 8B 89 ?? ?? ?? ??`    | Struct layouts grow when fields are added        |
| vtable disp32             | `call qword ptr [rax+0B0h]`    | `FF 90 ?? ?? ?? ??`       | Virtual functions get added or reordered         |
| Stack frame size          | `sub rsp, 80h`                 | `48 81 EC ?? ?? ?? ??`    | Depends on locals of the whole function          |
| Long conditional jump     | `jz loc_140123500`             | `0F 84 ?? ?? ?? ??`       | Depends on the size of the code it jumps over    |

Taking a typical function start as an example:

```x86asm
48 83 EC 28                 sub     rsp, 28h
48 8B 89 A8 01 00 00        mov     rcx, [rcx+1A8h]
48 85 C9                    test    rcx, rcx
74 0D                       jz      short loc_1401234C0
```

```
Copied:      48 83 EC 28 48 8B 89 A8 01 00 00 48 85 C9 74 0D
Wildcarded:  48 83 EC ?? 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ??
```

The opcode and ModR/M bytes (`48 8B 89`) are kept, so the pattern still says "load a pointer from a large offset of `rcx` into `rcx`", but it no longer cares what that offset is. The same idea applies to the smaller values: byte sized stack offsets, frame sizes and short jump distances (`48 83 EC ??`, `44 89 44 24 ??`, `74 ??`) are commonly wildcarded as well, since they shift whenever a local variable or a few instructions are added.

> [!TIP]
> Keep a value when it is the thing that makes the code unique and is unlikely to change, such as a small constant (`mov edx, 5` → `BA 05 00 00 00`), a hash, or a flag mask. If the signature is still unique with the value wildcarded, prefer wildcarding it.

#### Relative follow offsets
`[StaticAddress]`, `[VirtualTable]`, and `[MemberFunction]` can take one or more offsets in addition to the signature. Each offset tells the resolver where a 32-bit relative value starts, which can be **any** rel32 or RIP-relative disp32 in the matched bytes, not just the first `??`. The resolver reads the value there and moves to `offset + 4 + value`, so the dword it reads should always be wildcarded:

```csharp
[StaticAddress("48 8D 0D ?? ?? ?? ?? F3 0F 10 13", 3)]
//              0  1  2  3 <- disp32 of the lea

[StaticAddress("8B D0 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0", 5)]
//              0  1  2  3  4  5 <- disp32 of the lea, not the call's rel32 at 10
```

When more than one offset is given they are followed in order, and each one is relative to the location the previous step landed on. This allows following a call into another function and reading a value from inside it:

```csharp
[VirtualTable("E8 ?? ?? ?? ?? 49 8B 55 ?? 0F B7 CD", [1, 396])]
// 1:   follow the rel32 of the call into the called function (a constructor)
// 396: from the start of that function, follow the disp32 of the lea that loads the vtable
```

> [!NOTE]
> The `+ 4` assumes the relative value is the last part of its instruction, which is true for `call`, `jmp`, `lea` and most `mov`s. An instruction that has an immediate after a RIP-relative displacement, such as `cmp byte ptr cs:byte_X, 0` (`80 3D ?? ?? ?? ?? 00`) or `mov dword ptr cs:dword_X, 1` (`C7 05 ?? ?? ?? ?? 01 00 00 00`), will resolve to an address that is off by the size of that immediate.

#### Calls as signatures
A signature starting with `E8 ?? ?? ?? ??` (or `E9`) matches a **call site** instead of the function itself. When no offset is given, the resolver uses an implicit relative follow offset of `1` and follows the rel32 to get the address of the called function. This is useful when the function body is too generic to be unique but one of its callers is not:

```csharp
[MemberFunction("E8 ?? ?? ?? ?? 84 C0 74 ?? 48 8B CB")]
```

Here the bytes after the call (`test al, al` / `jz` / `mov rcx, rbx`) belong to the caller and are what makes the pattern unique.

### Crossing subroutine boundary issues
Signatures that span more than one function come with a number of issues. A pattern that starts at the end of one function and runs into the start of the next one, typically from a `ret` (`C3`) through the `CC` padding into a `sub rsp`, looks unique but breaks easily:

```x86asm
8B 05 52 D8 06 01 mov     eax, cs:dword_142F69028 ; start of sub_141EFB7D0
C3                retn                    ; end of sub_141EFB7D0
CC ... CC CC CC   align 10h
C2 00 00          retn    0               ; nullsub
CC ... CC CC CC   align 10h
40 53             push    rbx             ; 
```

```
Bad:   8B 05 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC CC C2 00 00 CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53
Good:  E8 ?? ?? ?? ?? 83 F8 01 74 ?? 48 8B 03
```

The bad signature fails for several reasons:

- **The previous function is unrelated code.** Any change to how `sub_140123480` ends, a different non-volatile register (`pop rdi` instead of `pop rbx`), a tail call `jmp` instead of `ret`, or the frame being removed, breaks the match even though the function you care about did not change at all.
- **The amount of padding is not stable.** The number of `CC` bytes depends on the length of the previous function modulo 16. Adding a single instruction to it changes `C3 CC CC` into `C3 CC CC CC CC CC` or removes the padding entirely.
- **The linker decides the order of functions.** With link-time code generation, profile guided optimization and identical COMDAT folding, functions are reordered, merged and moved between patches. There is no guarantee that the same function sits in front of yours next time.
- **A `ret` is not always the end of a function.** Functions can have several `ret`s, and PGO splits cold paths into separate chunks placed elsewhere in `.text`. The code after a `C3` might be a chunk of a completely different function rather than the start of the next one.
- **The match is at the wrong address.** `[MemberFunction]` resolves to the start of the match. A pattern beginning with `5B C3` resolves to the `pop rbx` of the previous function and needs an extra offset to reach the real function. Calling or hooking that address will execute the wrong code.

The same applies in the other direction: a signature taken from the middle of a function should not continue past its `ret` into whatever comes next. If a function is too short to be unique on its own, use a call site of it as described in [Calls as signatures](#calls-as-signatures) instead of extending the pattern into a neighbor.

> [!TIP]
> Before saving a signature, check that it still matches exactly one location (in IDA with **Search > Sequence of bytes** or a plugin such as SigMaker). A signature that only stays unique because of the bytes around a function boundary is a sign it should be taken from somewhere else.
