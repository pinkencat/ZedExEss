using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;

namespace ZedExEss.Hosting;

/// <summary>
/// Toolkit-neutral state and commands for editing the BASIC program in a suspended machine.
/// </summary>
/// <remarks>
/// The host owns suspension because it owns the execution driver. This class deliberately does
/// not start or stop threads; it only validates source and delegates direct memory operations to
/// <see cref="SpectrumBasicMemoryService"/> while the host guarantees exclusive machine access.
/// </remarks>
public sealed class SpectrumBasicEditorSession
{
    private readonly SpectrumBasicMemoryService _service;
    private SpectrumBasicProgramSnapshot? _snapshot;

    public SpectrumBasicEditorSession(SpectrumBasicMemoryService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public SpectrumModel Model => _service.Model;
    public string Source { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public bool IsSourceValid { get; private set; }
    public int? TokenizedSize { get; private set; }
    public SpectrumBasicProgramSnapshot? Snapshot => _snapshot;

    public bool Reload()
    {
        if (_service.TryReadProgram(out SpectrumBasicProgramSnapshot snapshot, out string error))
        {
            _snapshot = snapshot;
            Source = snapshot.Source;
            return ValidateSource("Loaded BASIC program from memory.");
        }

        _snapshot = null;
        Source = string.Empty;
        IsSourceValid = false;
        TokenizedSize = null;
        Status = BuildStatus($"No editable BASIC program could be read: {error}");
        return false;
    }

    public bool SetSource(string source)
    {
        Source = source ?? string.Empty;
        return ValidateSource("Ready.");
    }

    public bool Inject(out string error)
    {
        if (!_service.TryInjectProgram(Source, out SpectrumBasicProgramSnapshot snapshot, out error))
        {
            IsSourceValid = false;
            TokenizedSize = null;
            Status = BuildStatus(error);
            return false;
        }

        _snapshot = snapshot;
        IsSourceValid = true;
        TokenizedSize = snapshot.ProgramSize;
        Status = BuildStatus("Program injected into BASIC memory.");
        return true;
    }

    private bool ValidateSource(string validMessage)
    {
        IsSourceValid = _service.TryValidateSource(Source, out int tokenizedSize, out string error);
        TokenizedSize = IsSourceValid ? tokenizedSize : null;
        Status = BuildStatus(IsSourceValid ? validMessage : error);
        return IsSourceValid;
    }

    private string BuildStatus(string message)
    {
        string layout = _snapshot.HasValue
            ? $"PROG: 0x{_snapshot.Value.Prog:X4}, current size: {_snapshot.Value.ProgramSize} bytes, RAMTOP: 0x{_snapshot.Value.Ramtop:X4}"
            : "PROG: unavailable";
        string tokenized = TokenizedSize.HasValue ? $", tokenized size: {TokenizedSize.Value} bytes" : string.Empty;
        string tokenMode = _service.Allow128BasicTokens ? "128 BASIC tokens" : "48 BASIC tokens";
        return $"Model: {Model} ({tokenMode}) | {layout}{tokenized}{Environment.NewLine}{message}";
    }
}
