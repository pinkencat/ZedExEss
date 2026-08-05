# ZedExEss Avalonia Host

This is the first cross-platform desktop host. It currently provides:

- Live 16K, 48K, 128K, +2, +2A, +3, Pentagon and Scorpion machines built by
  `ZedExEss.Core`.
- Nearest-neighbour framebuffer presentation.
- Physical Spectrum keyboard-matrix input, including compound Backspace/Delete
  and Escape mappings.
- Model selection, reset and pause/resume controls.
- TAP, TZX and CSW attachment with play, stop, rewind and eject controls. Tape
  position is preserved safely when changing model and rewound on reset.
- Full tape block browser with block metadata, elapsed/total time, progress,
  current-block following, and double-click seeking.
- Two-drive +3 DSK and Beta 128 TRD/SCL management with insert, save/export,
  eject, write protection, blank +3 disk creation, and controller activity.
- BASIC program editing with detokenisation, live syntax checking and safe
  injection into a suspended machine.
- A modeless Z80 debugger with stepping, breakpoints/watchpoints, visible
  breakpoint and current-PC gutter markers, continuously scrolling logical
  memory/disassembly views, range export, byte patching, and inline assembly.
- Native Avalonia storage-provider dialogs rather than Windows-only file APIs.
- Low-latency, audio-clock-driven SDL3 output using packaged Windows, Linux and
  macOS native runtimes. If no playback device can be opened, the host reports
  the error and falls back to its silent realtime frame runner.

Run the desktop preview from the repository root:

```powershell
dotnet run --project ZedExEss.Avalonia/ZedExEss.Avalonia.csproj
```

Run the non-UI startup smoke test:

```powershell
dotnet run --project ZedExEss.Avalonia/ZedExEss.Avalonia.csproj -- --smoke-test
```

Run the real playback-device/audio-clock smoke test:

```powershell
dotnet run --project ZedExEss.Avalonia/ZedExEss.Avalonia.csproj -- --audio-smoke-test
```

The debugger and BASIC editor now cover the primary WPF tooling workflows. The
next migration step is to move linked core sources physically into their owning
project before platform packaging work begins.
