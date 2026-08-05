using System;
using System.Windows;
using System.Windows.Controls;
using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;

namespace ZedExEss
{
    /// <summary>Modal editor for the BASIC program owned by a suspended emulated machine.</summary>
    public partial class BasicProgramDialog : Window
    {
        private readonly SpectrumBasicMemoryService _service;
        private readonly SpectrumModel _model;
        private SpectrumBasicProgramSnapshot? _snapshot;
        private bool _loading;

        public BasicProgramDialog(SpectrumBasicMemoryService service, SpectrumModel model)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _model = model;
            InitializeComponent();
            ReloadFromMemory();
            BasicInput.Focus();
        }
        private void OnReload(object sender, RoutedEventArgs e)
        {
            ReloadFromMemory();
        }
        private void OnInject(object sender, RoutedEventArgs e)
        {
            if (!_service.TryInjectProgram(BasicInput.Text, out SpectrumBasicProgramSnapshot snapshot, out string error))
            {
                StatusText.Text = BuildStatus(error, tokenizedSize: null);
                MessageBox.Show(this, error, "BASIC Program", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _snapshot = snapshot;
            StatusText.Text = BuildStatus("Program injected into BASIC memory.", snapshot.ProgramSize);
        }
        private void OnBasicTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            UpdateValidationStatus();
        }
        private void ReloadFromMemory()
        {
            _loading = true;
            bool loaded = false;
            try
            {
                if (_service.TryReadProgram(out SpectrumBasicProgramSnapshot snapshot, out string error))
                {
                    _snapshot = snapshot;
                    BasicInput.Text = snapshot.Source;
                    StatusText.Text = BuildStatus("Loaded BASIC program from memory.", snapshot.ProgramSize);
                    loaded = true;
                }
                else
                {
                    _snapshot = null;
                    BasicInput.Text = string.Empty;
                    StatusText.Text = BuildStatus($"No editable BASIC program could be read: {error}", tokenizedSize: null);
                }
            }
            finally
            {
                _loading = false;
            }

            if (loaded)
            {
                UpdateValidationStatus();
            }
        }
        private void UpdateValidationStatus()
        {
            if (_service.TryValidateSource(BasicInput.Text, out int tokenizedSize, out string error))
            {
                StatusText.Text = BuildStatus("Ready.", tokenizedSize);
            }
            else
            {
                StatusText.Text = BuildStatus(error, tokenizedSize: null);
            }
        }
        private string BuildStatus(string message, int? tokenizedSize)
        {
            string layout = _snapshot.HasValue
                ? $"PROG: 0x{_snapshot.Value.Prog:X4}, current size: {_snapshot.Value.ProgramSize} bytes, RAMTOP: 0x{_snapshot.Value.Ramtop:X4}"
                : "PROG: unavailable";
            string tokenized = tokenizedSize.HasValue ? $", tokenized size: {tokenizedSize.Value} bytes" : string.Empty;
            string tokenMode = _service.Allow128BasicTokens ? "128 BASIC tokens" : "48 BASIC tokens";
            return $"Model: {_model} ({tokenMode}) | {layout}{tokenized}{Environment.NewLine}{message}";
        }
    }
}
