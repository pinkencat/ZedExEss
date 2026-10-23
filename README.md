
# ZedExEss

ZedExEss is a ZX Spectrum / ZX80 / ZX81 / Spectrum Next emulator written in C# and .NET 9. Primarily built as a Windows WPF application, there is also an Avalonia version implemented for cross-platform support. The CPU core passes ZEXALL and all Raxoft Z80 CPU instruction tests.

For binary releases for various platforms, see [Releases](https://github.com/pinkencat/ZedExEss/releases)

## Features

- Z80 CPU emulation with Spectrum-specific memory, port, contention, and timing models
- ZX80, ZX81, Spectrum 16K, 48K, 128K, +2, +2A, +3, Scorpion 256, Pentagon 128 and Spectrum Next machine profiles
- Beeper and AY-3-8912 audio, with a multi-channel audio oscilloscope covering the
  beeper, every PSG channel, and any fitted expansion
- TAP, TZX, and CSW tape playback with a block browser, seeking, autoloading, and optional loader acceleration
- Z80 and SNA snapshot loading
- Tape recording: what the machine saves is captured back into a TAP or TZX
- RZX input recording and replay, with a five-second rewind history for retakes
- Two-drive +3 disk support using DSK images
- Two-drive Beta 128/TR-DOS support using TRD and SCL images
- DivMMC support using storage images or a host folder
- Multiface One, Multiface 128, and Multiface 3 snapshot interfaces
- Kempston, Sinclair 1, Sinclair 2, and Cursor/Protek joystick mappings
-- Joysticks are mapped to cursor keys and left-alt for fire
- Interface 1 and Microdrive emulation
- Optional Covox DAC and General Sound co-processor audio expansions
- Scorpion 256 7 MHz turbo mode
- Gigascreen frame blending and integer zoom controls
- Integrated Z80 debugger with stepping, breakpoints, watchpoints, disassembly,
  memory editing, and inline assembly, including Spectrum Next support with Z80N
  disassembly, an MMU mapping view, a NextReg inspector, and NextReg breakpoints
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
| Scorpion 256 | Supported | Beta 128/TR-DOS, 7 MHz turbo, optional Covox and General Sound |
| ZX Spectrum Next | Experimental preview | User-supplied System/Next assets; ULA/Layer 2/sprite display, beeper/TurboSound audio, keyboard and NextReg-routed joystick input, debugger and oscilloscope |

### ZX Spectrum Next experimental preview

Both desktop applications expose **ZX Spectrum Next (experimental)** in the machine
menu and model selector. On first use, select:

1. the extracted `System/Next` directory from a NextZXOS distribution; and
2. either the compiled 8 KiB loader ROM or the official FPGA source tree's
   `cores/zxnext/src/rom/bootrom.vhd`.

The System/Next directory is projected as a writable, disposable
SD snapshot with its VFAT long names preserved for Browser metadata and BASIC asset
loading. Guest metadata and temporary-file writes last for the current session, but
the source directory is never modified.

The Next core models both five-button joystick connectors and their NextReg `$05`
Kempston 1 (`$1F/$DF`), Kempston 2 (`$37`), Sinclair and Cursor routing. The desktop
keyboard attachment currently feeds connector 1.

This mode is a progress preview and remains under development. Firmware and system
assets are not distributed with ZedExEss.

## Audio expansions

Both are optional and off by default. Each is fitted from the **Hardware** menu in
either desktop host, which rebuilds the machine because fitting one changes the port
map.

| Expansion | Ports | Notes |
| --- | --- | --- |
| Covox | `$DD` | Eight-bit DAC. Integrated per T-state rather than sampled once per output sample, so fast sample players keep their waveform. |
| General Sound | `$BB`, `$B3` | Four-channel sample card built around its own 12 MHz Z80, 512 KB of RAM and its own firmware. |

General Sound needs a `gs.rom` firmware image in the `ROMs` folder. Without it the card
is not fitted at all, so software cannot detect a card that would never answer. The card
runs its own Z80 independently of the host, and keeps its own clock when the Scorpion is
switched to 7 MHz.

Both expansions appear as their own oscilloscope groups alongside the beeper and PSG
channels.

## Scorpion turbo

Scorpion 256 supports its 7 MHz clock, toggled from the **Hardware** menu. On the real
machine this is a hardware latch driven by a front-panel button, so it takes effect
without interrupting running software; the menu item is that button. The ULA keeps its
own rate, so the display and interrupt timing are unchanged while the CPU runs at twice
the speed.

Switching turbo from the Scorpion's Shadow Service Monitor is not yet wired up.

## Multiface

A Multiface is fitted from the **Hardware** menu. It is 8 KiB of ROM and 8 KiB of
battery-backed RAM that page over the machine's low 16 KiB when its magic button is
pressed, which is **F6** or the toolbar's NMI button in either host.

| Variant | Machines | ROM |
| --- | --- | --- |
| Multiface One | 16K, 48K | `mf1.rom` |
| Multiface 128 | 128K, +2 | `mf128.rom` |
| Multiface 3 | +2A, +3 | `mf3.rom` |

Each variant only offers itself on the machines it was built for, and the ROM must be
present in the `ROMs` folder or the interface is not fitted. Pressing the button asserts
NMI but pages nothing in; the overlay appears only when the CPU fetches the vector at
`$0066`. Both paging in and paging out are port *reads* - a write to the same port is the
visibility lock, not a paging control - which is what the firmware does around every call
into the machine ROM — to print its menu, and to let the machine's own
interrupt handler scan the keyboard. That is also why a Multiface One cannot share a
machine with a Kempston joystick: the joystick reads the same `$1F` that pages the
interface out. The One and the 128 hide themselves from their own port once paged out, so
software cannot detect an interface that is not in use; Multiface 3 always answers, which
is how its software finds it.

A Multiface and DivMMC overlay the same memory and their control ports overlap, so
fitting one unfits the other, as do the three variants between themselves.

## Supported media

| Type | Extensions |
| --- | --- |
| Snapshots | `.z80`, `.sna`, `.szx` |
| Tape images | `.tap`, `.tzx`, `.csw` (`.tap` and `.tzx` write) |
| Input recordings | `.rzx` (read and write) |
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

## License

The ZedExEss source code is licensed under the [GNU General Public License,
version 3 or later](LICENSE) (`GPL-3.0-or-later`). ROM images, firmware, test
media, and other third-party material retain their respective licence terms;
see [NOTICE](NOTICE) for details.

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

## Known bugs

None currently outstanding. The previously documented Scorpion TR-DOS conflict with the
Kempston joystick is fixed, so that combination is now safe to use.
