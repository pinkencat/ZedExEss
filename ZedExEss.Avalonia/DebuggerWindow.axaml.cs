using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.AvaloniaHost;

/// <summary>Modeless Avalonia debugger over portable debugger state and projections.</summary>
internal sealed partial class DebuggerWindow : Window
{
    private const int DisassemblyLineCount = 512;
    private const int DisassemblyScrollJumpLines = 128;
    private const int MemoryRows = 256;
    private const int MemoryScrollJumpRows = 128;
    private const int MemoryPageBytes = MemoryScrollJumpRows * 16;

    private readonly SpectrumDebuggerViewService _view;
    private readonly IFileDialogService _fileDialogs;
    private readonly ObservableCollection<Z80DisassemblyLine> _disassembly = [];
    private readonly ListBox _disassemblyList;
    private readonly TextBlock _registersText;
    private readonly TextBlock _statusText;
    private readonly TextBox _disassemblyAddress;
    private readonly TextBox _selectedBytes;
    private readonly TextBox _exportStart;
    private readonly TextBox _exportEnd;
    private readonly TextBlock _exportStatus;
    private readonly TextBox _memoryAddress;
    private readonly TextBox _memoryText;
    private readonly TextBox _memoryPatch;
    private readonly TextBox _stackText;
    private readonly ComboBox _breakpointType;
    private readonly TextBox _breakpointAddress;
    private readonly TextBox _breakpointEnd;
    private readonly TextBox _breakpointMask;
    private readonly ListBox _breakpointsList;
    private readonly TextBox _assemblerAddress;
    private readonly TextBox _assemblerText;
    private readonly TextBox _assemblerOutput;
    private ushort _disassemblyStart;
    private readonly Stack<ushort> _disassemblyStartHistory = [];
    private ushort _memoryStart = 0x4000;
    private bool _suppressDisassemblyScroll;
    private bool _suppressMemoryScroll;
    private bool _suppressSelectionRangeUpdate;

    public event Action? RunRequested;
    public event Action? PauseRequested;
    public event Action? StepIntoRequested;
    public event Action? StepOverRequested;
    public event Action<ushort>? RunToAddressRequested;

    public DebuggerWindow(SpectrumDebuggerViewService view, IFileDialogService fileDialogs)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        AvaloniaXamlLoader.Load(this);
        _disassemblyList = FindRequiredControl<ListBox>("DisassemblyList");
        _registersText = FindRequiredControl<TextBlock>("RegistersText");
        _statusText = FindRequiredControl<TextBlock>("DebuggerStatusText");
        _disassemblyAddress = FindRequiredControl<TextBox>("DisassemblyAddressText");
        _selectedBytes = FindRequiredControl<TextBox>("SelectedBytesText");
        _exportStart = FindRequiredControl<TextBox>("ExportStartText");
        _exportEnd = FindRequiredControl<TextBox>("ExportEndText");
        _exportStatus = FindRequiredControl<TextBlock>("ExportStatusText");
        _memoryAddress = FindRequiredControl<TextBox>("MemoryAddressText");
        _memoryText = FindRequiredControl<TextBox>("MemoryText");
        _memoryPatch = FindRequiredControl<TextBox>("MemoryPatchText");
        _stackText = FindRequiredControl<TextBox>("StackText");
        _breakpointType = FindRequiredControl<ComboBox>("BreakpointTypeCombo");
        _breakpointAddress = FindRequiredControl<TextBox>("BreakpointAddressText");
        _breakpointEnd = FindRequiredControl<TextBox>("BreakpointEndText");
        _breakpointMask = FindRequiredControl<TextBox>("BreakpointMaskText");
        _breakpointsList = FindRequiredControl<ListBox>("BreakpointsList");
        _assemblerAddress = FindRequiredControl<TextBox>("AssemblerAddressText");
        _assemblerText = FindRequiredControl<TextBox>("AssemblerText");
        _assemblerOutput = FindRequiredControl<TextBox>("AssemblerOutputText");

        _disassemblyList.ItemsSource = _disassembly;
        _disassemblyList.AddHandler(ScrollViewer.ScrollChangedEvent, OnDisassemblyScrollChanged);
        _memoryText.AddHandler(ScrollViewer.ScrollChangedEvent, OnMemoryScrollChanged);
        _breakpointsList.ItemsSource = _view.Debugger.Breakpoints;
        _breakpointType.ItemsSource = Enum.GetValues<DebuggerBreakType>();
        _breakpointType.SelectedItem = DebuggerBreakType.Execute;
        WireEvents();
        RefreshAll(followPc: true);
    }

    public void RefreshAll(bool followPc)
    {
        if (_view.Debugger.Cpu == null || _view.Debugger.Memory == null)
        {
            _statusText.Text = "No machine attached";
            return;
        }

        if (followPc)
        {
            _disassemblyStart = _view.Debugger.Cpu.PC;
            _disassemblyStartHistory.Clear();
        }

        _registersText.Text = _view.GetRegistersText();
        RefreshDisassembly(followPc);
        RefreshMemory();
        _stackText.Text = _view.GetStackText();
        _statusText.Text = _view.Debugger.LastHit?.Reason ?? _view.Debugger.Mode.ToString();
        RefreshBreakpointList();
    }

    private void WireEvents()
    {
        FindRequiredControl<Button>("RunButton").Click += (_, _) => RunRequested?.Invoke();
        FindRequiredControl<Button>("PauseButton").Click += (_, _) => PauseRequested?.Invoke();
        FindRequiredControl<Button>("StepIntoButton").Click += (_, _) => StepIntoRequested?.Invoke();
        FindRequiredControl<Button>("StepOverButton").Click += (_, _) => StepOverRequested?.Invoke();
        FindRequiredControl<Button>("RunToButton").Click += (_, _) => RunToSelected();
        FindRequiredControl<Button>("RefreshButton").Click += (_, _) => RefreshAll(followPc: true);
        FindRequiredControl<Button>("DisassemblyGoButton").Click += (_, _) => NavigateDisassembly();
        FindRequiredControl<Button>("DisassemblyPreviousButton").Click += (_, _) =>
        {
            SpectrumMemory? memory = _view.Debugger.Memory;
            if (memory == null)
            {
                return;
            }

            _disassemblyStart = _disassemblyStartHistory.Count > 0
                ? _disassemblyStartHistory.Pop()
                : FindPreviousDisassemblyStart(memory, _disassemblyStart, DisassemblyScrollJumpLines);
            RefreshDisassembly(followPc: false);
        };
        FindRequiredControl<Button>("DisassemblyNextButton").Click += (_, _) =>
        {
            if (_disassembly.Count > 0)
            {
                _disassemblyStartHistory.Push(_disassemblyStart);
                Z80DisassemblyLine last = _disassembly[^1];
                _disassemblyStart = unchecked((ushort)(last.Address + Math.Max(1, last.Length)));
                RefreshDisassembly(followPc: false);
            }
        };
        FindRequiredControl<Button>("ToggleBreakpointButton").Click += (_, _) => ToggleSelectedExecuteBreakpoint();
        FindRequiredControl<Button>("ApplySelectedBytesButton").Click += (_, _) => ApplySelectedBytes();
        FindRequiredControl<Button>("UseSelectionButton").Click += (_, _) => UseSelectedExportRange();
        FindRequiredControl<Button>("CopyDisassemblyButton").Click += OnCopyDisassembly;
        FindRequiredControl<Button>("SaveDisassemblyButton").Click += OnSaveDisassembly;
        _disassemblyList.SelectionChanged += OnDisassemblySelectionChanged;
        _disassemblyList.DoubleTapped += (_, _) => ToggleSelectedExecuteBreakpoint();
        _disassemblyAddress.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                NavigateDisassembly();
                args.Handled = true;
            }
        };

        FindRequiredControl<Button>("MemoryGoButton").Click += (_, _) => NavigateMemory();
        FindRequiredControl<Button>("MemoryPreviousButton").Click += (_, _) =>
        {
            _memoryStart = unchecked((ushort)(_memoryStart - MemoryPageBytes));
            RefreshMemory();
        };
        FindRequiredControl<Button>("MemoryNextButton").Click += (_, _) =>
        {
            _memoryStart = unchecked((ushort)(_memoryStart + MemoryPageBytes));
            RefreshMemory();
        };
        FindRequiredControl<Button>("MemoryApplyButton").Click += (_, _) => ApplyMemoryPatch();

        FindRequiredControl<Button>("AddBreakpointButton").Click += (_, _) => AddBreakpoint();
        FindRequiredControl<Button>("EnableBreakpointButton").Click += (_, _) => ToggleBreakpointEnabled();
        FindRequiredControl<Button>("RemoveBreakpointButton").Click += (_, _) => RemoveBreakpoint();
        FindRequiredControl<Button>("AssemblerPreviewButton").Click += (_, _) => PreviewAssembly();
        FindRequiredControl<Button>("AssemblerApplyButton").Click += (_, _) => ApplyAssembly();
    }

    private void RefreshDisassembly(bool followPc)
    {
        _disassemblyAddress.Text = _disassemblyStart.ToString("X4");
        _suppressSelectionRangeUpdate = true;
        _disassembly.Clear();
        foreach (Z80DisassemblyLine line in _view.GetDisassembly(_disassemblyStart, DisassemblyLineCount))
        {
            _disassembly.Add(line);
        }
        _suppressSelectionRangeUpdate = false;

        if (followPc)
        {
            Z80DisassemblyLine? current = _disassembly.FirstOrDefault(line => line.IsCurrent);
            if (current != null)
            {
                _disassemblyList.SelectedItem = current;
                ScrollDisassemblyToIndex(_disassembly.IndexOf(current));
            }
        }
    }

    private void RefreshMemory()
    {
        _memoryAddress.Text = _memoryStart.ToString("X4");
        _memoryText.Text = _view.GetMemoryText(_memoryStart, MemoryRows);
    }

    private void NavigateDisassembly()
    {
        if (!SpectrumDebuggerViewService.TryParseWord(_disassemblyAddress.Text, out ushort address))
        {
            SetStatus("Invalid disassembly address.");
            return;
        }

        _disassemblyStart = address;
        _disassemblyStartHistory.Clear();
        RefreshDisassembly(followPc: false);
        if (_disassembly.Count > 0)
        {
            _disassemblyList.SelectedIndex = 0;
            ScrollDisassemblyToIndex(0);
        }
    }

    private void NavigateMemory()
    {
        if (!SpectrumDebuggerViewService.TryParseWord(_memoryAddress.Text, out ushort address))
        {
            SetStatus("Invalid memory address.");
            return;
        }

        _memoryStart = address;
        RefreshMemory();
    }

    private void UseSelectedExportRange()
    {
        if (!UpdateExportRangeFromSelection())
        {
            _exportStatus.Text = "Select one or more disassembly rows first.";
        }
    }

    private async void OnCopyDisassembly(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildDisassemblyExport(out string text, out string error))
        {
            _exportStatus.Text = error;
            return;
        }

        if (Clipboard == null)
        {
            _exportStatus.Text = "The platform clipboard is unavailable.";
            return;
        }

        await Clipboard.SetTextAsync(text);
        _exportStatus.Text = $"Copied {text.Count(static character => character == '\n')} line(s).";
    }

    private async void OnSaveDisassembly(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildDisassemblyExport(out string text, out string error))
        {
            _exportStatus.Text = error;
            return;
        }

        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = "Export Disassembly",
            SuggestedFileName = $"disassembly-{_exportStart.Text}-{_exportEnd.Text}.txt",
            DefaultExtension = ".txt",
            Filters =
            [
                new FileDialogFilter("Text files", "*.txt"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, text, Encoding.UTF8);
            _exportStatus.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _exportStatus.Text = exception.Message;
        }
    }

    private bool UpdateExportRangeFromSelection()
    {
        if (_disassemblyList.SelectedItems is not { } selectedItems)
        {
            return false;
        }

        List<Z80DisassemblyLine> selected = selectedItems
            .OfType<Z80DisassemblyLine>()
            .OrderBy(static line => line.Address)
            .ToList();
        if (selected.Count == 0)
        {
            return false;
        }

        Z80DisassemblyLine first = selected[0];
        Z80DisassemblyLine last = selected[^1];
        ushort end = unchecked((ushort)(last.Address + Math.Max(1, last.Length) - 1));
        _exportStart.Text = first.Address.ToString("X4");
        _exportEnd.Text = end.ToString("X4");
        return true;
    }

    private bool TryBuildDisassemblyExport(out string text, out string error)
    {
        if (!SpectrumDebuggerViewService.TryParseWord(_exportStart.Text, out ushort start)
            || !SpectrumDebuggerViewService.TryParseWord(_exportEnd.Text, out ushort end))
        {
            text = string.Empty;
            error = "Invalid export range.";
            return false;
        }

        return _view.TryBuildDisassemblyExport(start, end, out text, out error);
    }

    private void OnDisassemblySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_disassemblyList.SelectedItem is Z80DisassemblyLine line)
        {
            _selectedBytes.Text = line.EditableBytesText;
        }

        if (!_suppressSelectionRangeUpdate)
        {
            UpdateExportRangeFromSelection();
        }
    }

    private void ApplySelectedBytes()
    {
        if (_disassemblyList.SelectedItem is not Z80DisassemblyLine line)
        {
            return;
        }

        if (!_view.TryPatchBytes(line.Address, _selectedBytes.Text ?? string.Empty, out string error))
        {
            SetStatus(error);
            return;
        }

        SetStatus($"Patched memory at {line.Address:X4}.");
        RefreshAll(followPc: false);
    }

    private void ApplyMemoryPatch()
    {
        if (!_view.TryPatchBytes(_memoryStart, _memoryPatch.Text ?? string.Empty, out string error))
        {
            SetStatus(error);
            return;
        }

        SetStatus($"Patched memory at {_memoryStart:X4}.");
        RefreshAll(followPc: false);
    }

    private void RunToSelected()
    {
        if (_disassemblyList.SelectedItem is Z80DisassemblyLine line)
        {
            RunToAddressRequested?.Invoke(line.Address);
        }
    }

    private void ToggleSelectedExecuteBreakpoint()
    {
        if (_disassemblyList.SelectedItem is not Z80DisassemblyLine line)
        {
            return;
        }

        DebuggerBreakpoint? existing = _view.Debugger.Breakpoints.FirstOrDefault(
            breakpoint => breakpoint.Type == DebuggerBreakType.Execute
                && breakpoint.Address == line.Address
                && breakpoint.EndAddress == line.Address);
        if (existing == null)
        {
            _view.Debugger.AddExecuteBreakpoint(line.Address);
        }
        else
        {
            _view.Debugger.RemoveBreakpoint(existing);
        }

        RefreshAll(followPc: false);
    }

    private void AddBreakpoint()
    {
        if (_breakpointType.SelectedItem is not DebuggerBreakType type
            || !SpectrumDebuggerViewService.TryParseWord(_breakpointAddress.Text, out ushort address))
        {
            SetStatus("Invalid breakpoint type or address.");
            return;
        }

        if (type is DebuggerBreakType.PortRead or DebuggerBreakType.PortWrite)
        {
            ushort mask = SpectrumDebuggerViewService.TryParseWord(_breakpointMask.Text, out ushort parsedMask)
                ? parsedMask
                : (ushort)0xFFFF;
            _view.Debugger.AddPortBreakpoint(type, address, mask);
        }
        else if (type == DebuggerBreakType.Execute)
        {
            _view.Debugger.AddExecuteBreakpoint(address);
        }
        else
        {
            ushort end = SpectrumDebuggerViewService.TryParseWord(_breakpointEnd.Text, out ushort parsedEnd)
                ? parsedEnd
                : address;
            _view.Debugger.AddMemoryBreakpoint(type, address, end);
        }

        RefreshAll(followPc: false);
    }

    private void ToggleBreakpointEnabled()
    {
        if (_breakpointsList.SelectedItem is not DebuggerBreakpoint breakpoint)
        {
            return;
        }

        breakpoint.Enabled = !breakpoint.Enabled;
        _view.Debugger.RebuildHookState();
        RefreshAll(followPc: false);
    }

    private void RemoveBreakpoint()
    {
        if (_breakpointsList.SelectedItem is DebuggerBreakpoint breakpoint)
        {
            _view.Debugger.RemoveBreakpoint(breakpoint);
            RefreshAll(followPc: false);
        }
    }

    private void RefreshBreakpointList()
    {
        object? selected = _breakpointsList.SelectedItem;
        _breakpointsList.ItemsSource = null;
        _breakpointsList.ItemsSource = _view.Debugger.Breakpoints;
        _breakpointsList.SelectedItem = selected;
    }

    /// <summary>
    /// Rebases the bounded disassembly window when its real scrollbar reaches either end.
    /// Instruction boundaries remain authoritative, so scrolling never invents byte-aligned rows.
    /// </summary>
    private void OnDisassemblyScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressDisassemblyScroll || e.OffsetDelta.Y == 0 || e.Source is not ScrollViewer scrollViewer)
        {
            return;
        }

        SpectrumMemory? memory = _view.Debugger.Memory;
        if (memory == null || _disassembly.Count == 0)
        {
            return;
        }

        if (e.OffsetDelta.Y > 0
            && scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 2)
        {
            int anchorIndex = Math.Min(DisassemblyScrollJumpLines, _disassembly.Count - 1);
            _disassemblyStartHistory.Push(_disassemblyStart);
            _disassemblyStart = _disassembly[anchorIndex].Address;
            _suppressDisassemblyScroll = true;
            RefreshDisassembly(followPc: false);
            ScrollDisassemblyToIndex(Math.Max(0, _disassembly.Count - DisassemblyScrollJumpLines - 1));
        }
        else if (e.OffsetDelta.Y < 0 && scrollViewer.Offset.Y <= 2)
        {
            _disassemblyStart = _disassemblyStartHistory.Count > 0
                ? _disassemblyStartHistory.Pop()
                : FindPreviousDisassemblyStart(memory, _disassemblyStart, DisassemblyScrollJumpLines);
            _suppressDisassemblyScroll = true;
            RefreshDisassembly(followPc: false);
            ScrollDisassemblyToIndex(Math.Min(DisassemblyScrollJumpLines, _disassembly.Count - 1));
        }
    }

    /// <summary>Uses the same rolling-window strategy for the 64K memory text view.</summary>
    private void OnMemoryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressMemoryScroll
            || e.OffsetDelta.Y == 0
            || _view.Debugger.Memory == null
            || e.Source is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (e.OffsetDelta.Y > 0
            && scrollViewer.Offset.Y + scrollViewer.Viewport.Height >= scrollViewer.Extent.Height - 2)
        {
            _memoryStart = unchecked((ushort)(_memoryStart + (MemoryScrollJumpRows * 16)));
            _suppressMemoryScroll = true;
            RefreshMemory();
            ScrollMemoryToLine(MemoryRows - MemoryScrollJumpRows);
        }
        else if (e.OffsetDelta.Y < 0 && scrollViewer.Offset.Y <= 2)
        {
            _memoryStart = unchecked((ushort)(_memoryStart - (MemoryScrollJumpRows * 16)));
            _suppressMemoryScroll = true;
            RefreshMemory();
            ScrollMemoryToLine(MemoryScrollJumpRows);
        }
    }

    private void ScrollDisassemblyToIndex(int index)
    {
        if (_disassembly.Count == 0)
        {
            _suppressDisassemblyScroll = false;
            return;
        }

        index = Math.Clamp(index, 0, _disassembly.Count - 1);
        Z80DisassemblyLine target = _disassembly[index];
        _suppressDisassemblyScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            _disassemblyList.ScrollIntoView(target);
            Dispatcher.UIThread.Post(
                () => _suppressDisassemblyScroll = false,
                DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollMemoryToLine(int line)
    {
        _suppressMemoryScroll = true;
        Dispatcher.UIThread.Post(() =>
        {
            _memoryText.ScrollToLine(Math.Clamp(line, 0, MemoryRows - 1));
            Dispatcher.UIThread.Post(
                () => _suppressMemoryScroll = false,
                DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    private ushort FindPreviousDisassemblyStart(SpectrumMemory memory, ushort currentStart, int linesBack)
    {
        const int searchBytes = 2048;
        ushort scan = unchecked((ushort)(currentStart - searchBytes));
        var addresses = new List<ushort>(DisassemblyLineCount);
        ushort address = scan;
        int targetDistance = unchecked((ushort)(currentStart - scan));

        for (int index = 0; index < DisassemblyLineCount * 6; index++)
        {
            int distance = unchecked((ushort)(address - scan));
            if (distance >= targetDistance)
            {
                break;
            }

            addresses.Add(address);
            int length = Math.Max(1, _view.Disassembler.GetInstructionLength(memory, address));
            address = unchecked((ushort)(address + length));
        }

        if (addresses.Count == 0)
        {
            return unchecked((ushort)(currentStart - 0x0100));
        }

        return addresses[Math.Max(0, addresses.Count - linesBack)];
    }

    private void PreviewAssembly()
    {
        if (!TryGetAssemblerAddress(out ushort address))
        {
            return;
        }

        Z80AssemblyResult result = _view.Assemble(address, _assemblerText.Text ?? string.Empty);
        _assemblerOutput.Text = FormatAssemblyResult(result);
        SetStatus(result.Success ? "Assembly preview complete." : result.Error ?? "Assembly failed.");
    }

    private void ApplyAssembly()
    {
        if (!TryGetAssemblerAddress(out ushort address))
        {
            return;
        }

        if (!_view.TryApplyAssembly(address, _assemblerText.Text ?? string.Empty, out Z80AssemblyResult result, out string error))
        {
            _assemblerOutput.Text = FormatAssemblyResult(result);
            SetStatus(error);
            return;
        }

        _assemblerOutput.Text = FormatAssemblyResult(result);
        SetStatus($"Applied {result.Bytes.Length} assembled bytes.");
        RefreshAll(followPc: false);
    }

    private bool TryGetAssemblerAddress(out ushort address)
    {
        if (SpectrumDebuggerViewService.TryParseWord(_assemblerAddress.Text, out address))
        {
            return true;
        }

        SetStatus("Invalid assembler address.");
        return false;
    }

    private static string FormatAssemblyResult(Z80AssemblyResult result)
    {
        if (!result.Success)
        {
            return result.Error ?? "Assembly failed.";
        }

        var builder = new StringBuilder();
        foreach (Z80AssemblyPatch patch in result.Patches)
        {
            builder.Append(patch.Address.ToString("X4"))
                .Append(": ")
                .Append(BitConverter.ToString(patch.Bytes).Replace('-', ' '))
                .AppendLine();
        }

        return builder.ToString();
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
    }

    private T FindRequiredControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"{name} was not created by XAML.");
    }
}
