
# ZedExEss

ZedExEss is a ZX Spectrum/ZX80/ZX81 emulator written in C# and .NET 9. Primarily built as a Windows WPF application, there is also an Avalonia version implemented for cross-platform support. The CPU core passes ZEXALL and all Raxoft Z80 CPU instruction tests.

For binary releases for various platforms, see [Releases](https://github.com/pinkencat/ZedExEss/releases)

## Features

- Z80 CPU emulation with Spectrum-specific memory, port, contention, and timing models
- ZX80, ZX81, Spectrum 16K, 48K, 128K, +2, +2A, +3, Scorpion 256 and Pentagon 128 machine profiles
- Beeper and AY-3-8912 audio, with an audio oscilloscope
- TAP, TZX, and CSW tape playback with a block browser, seeking, autoloading, and optional loader acceleration
- Z80 and SNA snapshot loading
- Two-drive +3 disk support using DSK images
- Two-drive Beta 128/TR-DOS support using TRD and SCL images
- DivMMC support using storage images or a host folder
- Kempston, Sinclair 1, Sinclair 2, and Cursor/Protek joystick mappings
-- Joysticks are mapped to cursor keys and left-alt for fire
- Interface 1 and Microdrive emulation
- Gigascreen frame blending and integer zoom controls
- Integrated Z80 debugger with stepping, breakpoints, watchpoints, disassembly, memory editing, and inline assembly
- Sinclair BASIC program viewing, editing, syntax checking, tokenisation, and injection
- POKE entry and drag-and-drop media loading
- Headless verification and benchmarking commands

## Machine support

| Machine | Status | Notes |
| --- | --- | --- |
| ZX80/ZX81 | Supported | 1k and 16k memory profiles |
| ZX Spectrum 16K | Supported | 16 KB memory profile |
| ZX Spectrum 48K | Supported | Standard 48K model |
| ZX Spectrum 128K | Supported | Memory paging and AY audio |
| ZX Spectrum +2 | Supported | 128K-family paging and AY audio |
| ZX Spectrum +2A | Supported | +3-style paging without the disk drive |
| ZX Spectrum +3 | Supported | Includes two-drive +3 disk support |
| Pentagon 128 | Supported | Includes Beta 128/TR-DOS support |
| Scorpion 256 | Supported | Includes Beta 128/TR-DOS support |

## Supported media

| Type | Extensions |
| --- | --- |
| Snapshots | `.z80`, `.sna` |
| Tape images | `.tap`, `.tzx`, `.csw` |
| +3 disk images | `.dsk` |
| Beta 128/TR-DOS disk images | `.trd`, `.scl` |
| DivMMC storage images | `.img`, `.hdf`, `.sd`, `.bin` |
| ZX80/80 Snapshots | `.o`, `.p`, `.81` |
| Microdrive Images | `.mdr` |

Support can vary between image variants and software that depends on undocumented hardware behaviour.

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows, Linux, or macOS for the Avalonia application
- Windows for the WPF application

## Repository layout

```text
ZedExEss/
|-- ZedExEss/             Windows WPF host and Windows-specific adapters
|-- ZedExEss.Avalonia/    Cross-platform Avalonia desktop host
|-- ZedExEss.Core/        Portable CPU, emulator, media, session, and hosting core
|-- ZedExEss.Headless/    Command-line diagnostics and benchmarks
|-- TEST/                 Test programs, media, and verification assets
`-- ZedExEss.sln          Complete solution
```

## Current status

ZedExEss is under active development. Hardware emulation, media compatibility, and the cross-platform host may continue to change.

## Known Bugs

Scorpion TR-DOS currently fights with kempston joystick if enabled. When using Scorpion emulation, do not use Kempston joystick.
