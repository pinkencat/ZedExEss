using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss
{
    /// <summary>Modeless debugger UI over <see cref="SpectrumDebuggerController"/>.</summary>
    /// <remarks>
    /// The disassembly and memory panes expose a rolling window rather than materialising all
    /// 64 KiB. Reaching either scroll boundary changes the base address and repopulates that
    /// window, which gives continuous navigation while keeping WPF item counts bounded.
    /// </remarks>
    public partial class DebuggerWindow : Window
    {
        private readonly SpectrumDebuggerController _debugger;
        private readonly Z80Disassembler _disassembler;
        private readonly Z80InlineAssembler _assembler;
        private readonly IFileDialogService _fileDialogs;
        private readonly IClipboardService _clipboard;
        private readonly IUiDispatcher _uiDispatcher;
        private readonly ObservableCollection<Z80DisassemblyLine> _disassembly = [];
        private const int DisassemblyLineCount = 512;
        private const int DisassemblyScrollJumpLines = 128;
        private const int MemoryRows = 256;
        private const int MemoryScrollJumpRows = 128;
        private ushort _disassemblyStart;
        private readonly Stack<ushort> _disassemblyStartHistory = [];
        private ushort _memoryStart = 0x4000;
        private bool _suppressDisassemblyScroll;
        private bool _suppressMemoryScroll;
        private bool _suppressSelectionRangeUpdate;
        private bool _closedByOwner;

        public event Action? RunRequested;
        public event Action? PauseRequested;
        public event Action? StepIntoRequested;
        public event Action? StepOverRequested;
        public event Action<ushort>? RunToAddressRequested;

        public DebuggerWindow(
            SpectrumDebuggerController debugger,
            Z80Disassembler disassembler,
            Z80InlineAssembler assembler,
            IFileDialogService fileDialogs,
            IClipboardService clipboard,
            IUiDispatcher uiDispatcher)
        {
            _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
            _disassembler = disassembler ?? throw new ArgumentNullException(nameof(disassembler));
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
            _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
            _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

            InitializeComponent();
            DisassemblyGrid.ItemsSource = _disassembly;
            DisassemblyGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnDisassemblyScrollChanged));
            MemoryText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnMemoryScrollChanged));
            BreakpointsList.ItemsSource = _debugger.Breakpoints;
            BreakpointTypeCombo.ItemsSource = Enum.GetValues(typeof(DebuggerBreakType));
            BreakpointTypeCombo.SelectedItem = DebuggerBreakType.Execute;
            _debugger.BreakHit += OnDebuggerBreakHit;
            RefreshAll(followPc: true);
        }
        public void OwnerClosing()
        {
            _closedByOwner = true;
            Close();
        }
        protected override void OnClosed(EventArgs e)
        {
            _debugger.BreakHit -= OnDebuggerBreakHit;
            if (!_closedByOwner)
            {
                PauseRequested = null;
                RunRequested = null;
                StepIntoRequested = null;
                StepOverRequested = null;
                RunToAddressRequested = null;
            }

            base.OnClosed(e);
        }
        public void RefreshAll(bool followPc)
        {
            Z80? cpu = _debugger.Cpu;
            SpectrumMemory? memory = _debugger.Memory;
            if (cpu == null || memory == null)
            {
                StatusText.Text = "No machine attached";
                return;
            }

            if (followPc)
            {
                _disassemblyStart = cpu.PC;
                _disassemblyStartHistory.Clear();
            }

            RefreshRegisters(cpu);
            RefreshDisassembly(memory, cpu.PC, followPc);
            RefreshMemory();
            RefreshStack(cpu, memory);
            StatusText.Text = _debugger.LastHit?.Reason ?? _debugger.Mode.ToString();
        }
        private void RefreshRegisters(Z80 cpu)
        {
            byte f = cpu.GetFlags();
            string flags =
                $"{Flag(f, 0x80, 'S')}{Flag(f, 0x40, 'Z')}{Flag(f, 0x20, '5')}{Flag(f, 0x10, 'H')}" +
                $"{Flag(f, 0x08, '3')}{Flag(f, 0x04, 'P')}{Flag(f, 0x02, 'N')}{Flag(f, 0x01, 'C')}";
            RegistersText.Text =
                $"AF {cpu.AF:X4}  BC {cpu.BC:X4}  DE {cpu.DE:X4}  HL {cpu.HL:X4}\n" +
                $"AF' {cpu.AF_:X4} BC' {cpu.BC_:X4} DE' {cpu.DE_:X4} HL' {cpu.HL_:X4}\n" +
                $"IX {cpu.IX:X4}  IY {cpu.IY:X4}  SP {cpu.SP:X4}  PC {cpu.PC:X4}\n" +
                $"I {cpu.I:X2}  R {cpu.R:X2}  IM {cpu.InterruptModeValue}  IFF {Bool(cpu.Iff1)}/{Bool(cpu.Iff2)}  HALT {Bool(cpu.IsHalted)}\n" +
                $"F {flags}  T {cpu.Cyc}  frame {_debugger.CurrentFrameTstate}  line {_debugger.CurrentLine}:{_debugger.CurrentLineTstate}";
        }
        private void RefreshDisassembly(SpectrumMemory memory, ushort currentPc, bool followCurrent)
        {
            DisassemblyAddressText.Text = _disassemblyStart.ToString("X4", CultureInfo.InvariantCulture);
            _suppressSelectionRangeUpdate = true;
            _disassembly.Clear();
            foreach (Z80DisassemblyLine line in _disassembler.DisassembleWindow(memory, _disassemblyStart, currentPc, DisassemblyLineCount, _debugger))
            {
                _disassembly.Add(line);
            }
            _suppressSelectionRangeUpdate = false;

            if (followCurrent)
            {
                Z80DisassemblyLine? current = _disassembly.FirstOrDefault(line => line.IsCurrent);
                if (current != null)
                {
                    DisassemblyGrid.SelectedItem = current;
                    ScrollDisassemblyToIndex(_disassembly.IndexOf(current));
                }
            }
        }
        private void NavigateDisassembly(ushort address)
        {
            SpectrumMemory? memory = _debugger.Memory;
            Z80? cpu = _debugger.Cpu;
            if (memory == null || cpu == null)
            {
                return;
            }

            _disassemblyStart = address;
            _disassemblyStartHistory.Clear();
            RefreshDisassembly(memory, cpu.PC, followCurrent: false);
            if (_disassembly.Count > 0)
            {
                DisassemblyGrid.SelectedItem = _disassembly[0];
                ScrollDisassemblyToIndex(0);
            }
        }
        private void RefreshMemory()
        {
            SpectrumMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                return;
            }

            MemoryAddressText.Text = _memoryStart.ToString("X4", CultureInfo.InvariantCulture);
            var builder = new StringBuilder();
            for (int row = 0; row < MemoryRows; row++)
            {
                ushort rowAddress = unchecked((ushort)(_memoryStart + (row * 16)));
                builder.Append(rowAddress.ToString("X4", CultureInfo.InvariantCulture)).Append(": ");
                var ascii = new StringBuilder(16);
                for (int col = 0; col < 16; col++)
                {
                    byte value = memory.ReadDirect(unchecked((ushort)(rowAddress + col)));
                    builder.Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    ascii.Append(value is >= 32 and < 127 ? (char)value : '.');
                }

                builder.Append(' ').Append(ascii).AppendLine();
            }

            MemoryText.Text = builder.ToString();
        }
        private void RefreshStack(Z80 cpu, SpectrumMemory memory)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                ushort address = unchecked((ushort)(cpu.SP + (i * 2)));
                ushort value = (ushort)(memory.ReadDirect(address) | (memory.ReadDirect(unchecked((ushort)(address + 1))) << 8));
                builder.Append(address.ToString("X4", CultureInfo.InvariantCulture))
                    .Append(": ")
                    .Append(value.ToString("X4", CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            StackText.Text = builder.ToString();
        }
        private void OnRun(object sender, RoutedEventArgs e) => RunRequested?.Invoke();
        private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
        private void OnStepInto(object sender, RoutedEventArgs e) => StepIntoRequested?.Invoke();
        private void OnStepOver(object sender, RoutedEventArgs e) => StepOverRequested?.Invoke();
        private void OnRefresh(object sender, RoutedEventArgs e) => RefreshAll(followPc: true);
        private void OnRunToCursor(object sender, RoutedEventArgs e)
        {
            if (DisassemblyGrid.SelectedItem is Z80DisassemblyLine line)
            {
                RunToAddressRequested?.Invoke(line.Address);
            }
        }
        private void OnDisassemblyGo(object sender, RoutedEventArgs e)
        {
            if (!TryParseWord(DisassemblyAddressText.Text, out ushort address))
            {
                MessageBox.Show(this, "Invalid disassembly address.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NavigateDisassembly(address);
        }
        private void OnDisassemblyAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnDisassemblyGo(sender, e);
                e.Handled = true;
            }
        }
        private void OnMemoryGo(object sender, RoutedEventArgs e)
        {
            if (TryParseWord(MemoryAddressText.Text, out ushort address))
            {
                _memoryStart = address;
            }

            RefreshMemory();
        }
        private void OnMemoryApply(object sender, RoutedEventArgs e)
        {
            SpectrumMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                MessageBox.Show(this, "Invalid memory address.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseMemoryEditorBytes(MemoryText.Text, out byte[] bytes, out string error))
            {
                MessageBox.Show(this, error, "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                ushort target = unchecked((ushort)(_memoryStart + i));
                if (!memory.CanWriteDirect(target))
                {
                    MessageBox.Show(this, $"Address {target:X4} is not writable RAM.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(_memoryStart + i)), bytes[i]);
            }

            RefreshAll(followPc: false);
        }
        private void OnAddBreakpoint(object sender, RoutedEventArgs e)
        {
            if (BreakpointTypeCombo.SelectedItem is not DebuggerBreakType type)
            {
                return;
            }

            if (!TryParseWord(BreakpointAddressText.Text, out ushort value))
            {
                MessageBox.Show(this, "Invalid breakpoint address/port.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (type is DebuggerBreakType.PortRead or DebuggerBreakType.PortWrite)
            {
                ushort mask = TryParseWord(BreakpointMaskText.Text, out ushort parsedMask) ? parsedMask : (ushort)0xFFFF;
                _debugger.AddPortBreakpoint(type, value, mask);
            }
            else if (type == DebuggerBreakType.Execute)
            {
                _debugger.AddExecuteBreakpoint(value);
            }
            else
            {
                ushort end = TryParseWord(BreakpointEndText.Text, out ushort parsedEnd) ? parsedEnd : value;
                _debugger.AddMemoryBreakpoint(type, value, end);
            }

            RefreshAll(followPc: false);
        }
        private void OnRemoveBreakpoint(object sender, RoutedEventArgs e)
        {
            if (BreakpointsList.SelectedItem is DebuggerBreakpoint breakpoint)
            {
                _debugger.RemoveBreakpoint(breakpoint);
                RefreshAll(followPc: false);
            }
        }
        private void OnToggleBreakpoint(object sender, RoutedEventArgs e)
        {
            if (BreakpointsList.SelectedItem is DebuggerBreakpoint breakpoint)
            {
                breakpoint.Enabled = !breakpoint.Enabled;
                _debugger.RebuildHookState();
                BreakpointsList.Items.Refresh();
                RefreshAll(followPc: false);
            }
        }
        private void OnAssemblerPreview(object sender, RoutedEventArgs e)
        {
            PreviewAssembler(showMessage: true);
        }
        private void OnAssemblerApply(object sender, RoutedEventArgs e)
        {
            if (!TryParseWord(AssemblerAddressText.Text, out ushort address))
            {
                MessageBox.Show(this, "Invalid assembler address.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Z80AssemblyResult result = _assembler.Assemble(address, AssemblerText.Text);
            if (!result.Success)
            {
                AssemblerOutputText.Text = result.Error;
                return;
            }

            SpectrumMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                return;
            }

            foreach (Z80AssemblyPatch patch in result.Patches)
            {
                for (int i = 0; i < patch.Bytes.Length; i++)
                {
                    ushort target = unchecked((ushort)(patch.Address + i));
                    if (!memory.CanWriteDirect(target))
                    {
                        AssemblerOutputText.Text = $"Address {target:X4} is not writable RAM.";
                        return;
                    }
                }
            }

            foreach (Z80AssemblyPatch patch in result.Patches)
            {
                for (int i = 0; i < patch.Bytes.Length; i++)
                {
                    memory.WriteDirect(unchecked((ushort)(patch.Address + i)), patch.Bytes[i]);
                }
            }

            PreviewAssembler(showMessage: false);
            RefreshAll(followPc: false);
        }
        private void OnDisassemblyDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DisassemblyGrid.CurrentColumn?.Header?.ToString() != "BP")
            {
                return;
            }

            if (DisassemblyGrid.SelectedItem is not Z80DisassemblyLine line)
            {
                return;
            }

            DebuggerBreakpoint? existing = _debugger.Breakpoints.FirstOrDefault(bp => bp.Type == DebuggerBreakType.Execute && bp.Address == line.Address);
            if (existing != null)
            {
                _debugger.RemoveBreakpoint(existing);
            }
            else
            {
                _debugger.AddExecuteBreakpoint(line.Address);
            }

            RefreshAll(followPc: false);
        }
        private void OnDisassemblyCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Column.Header?.ToString() != "Bytes")
            {
                return;
            }

            if (e.Row.Item is not Z80DisassemblyLine line || e.EditingElement is not TextBox textBox)
            {
                return;
            }

            string edited = textBox.Text.Trim();
            if (!TryParseByteList(edited, out byte[] bytes, out string error))
            {
                MessageBox.Show(this, error, "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }

            SpectrumMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                return;
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                ushort target = unchecked((ushort)(line.Address + i));
                if (!memory.CanWriteDirect(target))
                {
                    MessageBox.Show(this, $"Address {target:X4} is not writable RAM.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Cancel = true;
                    return;
                }
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(line.Address + i)), bytes[i]);
            }

            _uiDispatcher.TryPost(() => RefreshAll(followPc: false));
        }
        private void OnDisassemblySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionRangeUpdate)
            {
                return;
            }

            UpdateExportRangeFromSelection();
        }
        private void OnUseSelectedDisassemblyRange(object sender, RoutedEventArgs e)
        {
            if (!UpdateExportRangeFromSelection())
            {
                MessageBox.Show(this, "Select one or more disassembly rows first.", "Debugger", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        private void OnCopyDisassemblyRange(object sender, RoutedEventArgs e)
        {
            if (!TryBuildDisassemblyExport(out string text, out string error))
            {
                MessageBox.Show(this, error, "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _clipboard.SetText(text);
            ExportStatusText.Text = $"Copied {text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} line(s)";
        }
        private async void OnSaveDisassemblyRange(object sender, RoutedEventArgs e)
        {
            if (!TryBuildDisassemblyExport(out string text, out string error))
            {
                MessageBox.Show(this, error, "Debugger", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = "Export Disassembly",
                SuggestedFileName = $"disassembly-{ExportStartText.Text}-{ExportEndText.Text}.txt",
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

            File.WriteAllText(path, text, Encoding.UTF8);
            ExportStatusText.Text = $"Saved {Path.GetFileName(path)}";
        }
        private bool UpdateExportRangeFromSelection()
        {
            List<Z80DisassemblyLine> selected = DisassemblyGrid.SelectedItems
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
            ExportStartText.Text = first.Address.ToString("X4", CultureInfo.InvariantCulture);
            ExportEndText.Text = end.ToString("X4", CultureInfo.InvariantCulture);
            return true;
        }
        private bool TryBuildDisassemblyExport(out string text, out string error)
        {
            text = string.Empty;
            error = string.Empty;
            SpectrumMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                error = "No memory is attached.";
                return false;
            }

            if (!TryParseWord(ExportStartText.Text, out ushort start) || !TryParseWord(ExportEndText.Text, out ushort end))
            {
                error = "Invalid export range.";
                return false;
            }

            if (end < start)
            {
                error = "Export end address must be greater than or equal to the start address.";
                return false;
            }

            var builder = new StringBuilder();
            ushort pc = start;
            int guard = 0;
            while (pc <= end && guard++ < 0x10000)
            {
                Z80DisassembledInstruction instruction = _disassembler.Disassemble(memory, pc);
                builder.Append(FormatDisassemblyLine(instruction)).AppendLine();
                int length = Math.Max(1, instruction.Length);
                if (pc > 0xFFFF - length)
                {
                    break;
                }

                pc = (ushort)(pc + length);
            }

            text = builder.ToString();
            if (text.Length == 0)
            {
                error = "The export range did not produce any disassembly.";
                return false;
            }

            return true;
        }
        private static string FormatDisassemblyLine(Z80DisassembledInstruction instruction)
        {
            string bytes = BitConverter.ToString(instruction.Bytes).Replace("-", " ", StringComparison.Ordinal);
            return $"{instruction.Address:X4}: {bytes,-14} {instruction.Text}";
        }
        private void OnDisassemblyScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressDisassemblyScroll || e.VerticalChange == 0)
            {
                return;
            }

            SpectrumMemory? memory = _debugger.Memory;
            Z80? cpu = _debugger.Cpu;
            if (memory == null || cpu == null || _disassembly.Count == 0)
            {
                return;
            }

            if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            {
                int anchorIndex = Math.Min(DisassemblyScrollJumpLines, _disassembly.Count - 1);
                _disassemblyStartHistory.Push(_disassemblyStart);
                _disassemblyStart = _disassembly[anchorIndex].Address;
                _suppressDisassemblyScroll = true;
                RefreshDisassembly(memory, cpu.PC, followCurrent: false);
                ScrollDisassemblyToIndex(Math.Max(0, _disassembly.Count - DisassemblyScrollJumpLines - 1));
            }
            else if (e.VerticalChange < 0 && e.VerticalOffset <= 2)
            {
                _disassemblyStart = _disassemblyStartHistory.Count > 0
                    ? _disassemblyStartHistory.Pop()
                    : FindPreviousDisassemblyStart(memory, _disassemblyStart, DisassemblyScrollJumpLines);
                _suppressDisassemblyScroll = true;
                RefreshDisassembly(memory, cpu.PC, followCurrent: false);
                ScrollDisassemblyToIndex(Math.Min(DisassemblyScrollJumpLines, _disassembly.Count - 1));
            }
        }
        private void OnMemoryScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_suppressMemoryScroll || e.VerticalChange == 0 || _debugger.Memory == null)
            {
                return;
            }

            if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            {
                _memoryStart = unchecked((ushort)(_memoryStart + (MemoryScrollJumpRows * 16)));
                _suppressMemoryScroll = true;
                RefreshMemory();
                ScrollMemoryToLine(MemoryRows - MemoryScrollJumpRows);
            }
            else if (e.VerticalChange < 0 && e.VerticalOffset <= 2)
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
                return;
            }

            index = Math.Clamp(index, 0, _disassembly.Count - 1);
            _suppressDisassemblyScroll = true;
            DisassemblyGrid.ScrollIntoView(_disassembly[index]);
            _uiDispatcher.TryPost(() => _suppressDisassemblyScroll = false);
        }
        private void ScrollMemoryToLine(int line)
        {
            _suppressMemoryScroll = true;
            MemoryText.ScrollToLine(Math.Clamp(line, 0, MemoryRows - 1));
            _uiDispatcher.TryPost(() => _suppressMemoryScroll = false);
        }
        private ushort FindPreviousDisassemblyStart(SpectrumMemory memory, ushort currentStart, int linesBack)
        {
            const int searchBytes = 2048;
            ushort scan = unchecked((ushort)(currentStart - searchBytes));
            var addresses = new List<ushort>(DisassemblyLineCount);
            ushort pc = scan;
            int targetDistance = unchecked((ushort)(currentStart - scan));

            for (int i = 0; i < DisassemblyLineCount * 6; i++)
            {
                int distance = unchecked((ushort)(pc - scan));
                if (distance >= targetDistance)
                {
                    break;
                }

                addresses.Add(pc);
                int length = Math.Max(1, _disassembler.GetInstructionLength(memory, pc));
                pc = unchecked((ushort)(pc + length));
            }

            if (addresses.Count == 0)
            {
                return unchecked((ushort)(currentStart - 0x0100));
            }

            return addresses[Math.Max(0, addresses.Count - linesBack)];
        }
        private void OnDebuggerBreakHit(DebuggerBreakHit hit)
        {
            _uiDispatcher.TryPost(() => RefreshAll(followPc: true));
        }
        private bool PreviewAssembler(bool showMessage)
        {
            if (!TryParseWord(AssemblerAddressText.Text, out ushort address))
            {
                AssemblerOutputText.Text = "Invalid assembler address.";
                return false;
            }

            Z80AssemblyResult result = _assembler.Assemble(address, AssemblerText.Text);
            if (!result.Success)
            {
                AssemblerOutputText.Text = result.Error;
                return false;
            }

            AssemblerOutputText.Text = FormatAssemblyPreview(result);
            if (showMessage && result.Bytes.Length == 0)
            {
                AssemblerOutputText.Text = "No bytes generated.";
            }

            return true;
        }
        private static string FormatAssemblyPreview(Z80AssemblyResult result)
        {
            if (result.Patches.Count == 0)
            {
                return "0 byte(s)";
            }

            var builder = new StringBuilder();
            builder.Append(result.Bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" byte(s)");
            for (int i = 0; i < result.Patches.Count; i++)
            {
                Z80AssemblyPatch patch = result.Patches[i];
                builder.AppendLine()
                    .Append(patch.Address.ToString("X4", CultureInfo.InvariantCulture))
                    .Append(": ")
                    .Append(BitConverter.ToString(patch.Bytes).Replace("-", " ", StringComparison.Ordinal));
            }

            return builder.ToString();
        }
        private static bool TryParseWord(string text, out ushort value)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            if (text.StartsWith("$", StringComparison.Ordinal) || text.StartsWith("#", StringComparison.Ordinal))
            {
                return ushort.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            if (text.EndsWith("H", StringComparison.OrdinalIgnoreCase))
            {
                return ushort.TryParse(text[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                || ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
        private static bool TryParseByteList(string text, out byte[] bytes, out string error)
        {
            bytes = [];
            error = string.Empty;
            string[] tokens = text.Split([' ', '\t', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                error = "No bytes were entered.";
                return false;
            }

            bytes = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    token = token[2..];
                }

                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    error = $"Invalid byte '{tokens[i]}'.";
                    return false;
                }
            }

            return true;
        }
        private static bool TryParseMemoryEditorBytes(string text, out byte[] bytes, out string error)
        {
            List<byte> result = [];
            string[] lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                {
                    continue;
                }

                string byteArea = line[(colon + 1)..];
                string[] tokens = byteArea.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                int parsedOnLine = 0;
                foreach (string token in tokens)
                {
                    if (parsedOnLine >= 16)
                    {
                        break;
                    }

                    if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    {
                        break;
                    }

                    result.Add(value);
                    parsedOnLine++;
                }
            }

            if (result.Count == 0)
            {
                bytes = [];
                error = "No editable hex bytes were found in the memory view.";
                return false;
            }

            bytes = result.ToArray();
            error = string.Empty;
            return true;
        }
        private static char Flag(byte flags, byte mask, char name) => (flags & mask) != 0 ? name : '-';
        private static string Bool(bool value) => value ? "1" : "0";
    }
}
