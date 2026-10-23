using System;

namespace ZedExEss.Hosting;

/// <summary>Everything the TAS window shows, read in one go.</summary>
/// <remarks>
/// A snapshot rather than a set of properties on the host, so the window can redraw from
/// a single consistent reading instead of sampling a machine that is still moving.
/// </remarks>
public readonly record struct TasStatus(
    int Frame,
    int RecordedFrames,
    int ReRecords,
    bool IsRecording,
    bool IsPlaying,
    bool IsPaused,
    int HistoryStates,
    double HistorySeconds,
    long HistoryBytes,
    long HistoryBudgetBytes,
    long HistoryUnsharedBytes,
    int OldestFrame)
{
    /// <summary>True when a recording is being made or played, so the counters mean something.</summary>
    public bool HasRun => IsRecording || IsPlaying || RecordedFrames > 0;
}

/// <summary>
/// The controls a run is built with, as the TAS window needs them.
/// </summary>
/// <remarks>
/// The window is deliberately a view and nothing more: pausing, presenting and owning the
/// machine differ between the two hosts, so the hosts implement this and the window stays
/// identical in both. It also means the commands are the same ones the menus and keys
/// already call, rather than a second path that can drift from them.
/// </remarks>
public interface ITasControls
{
    /// <summary>Raised when anything the window displays may have changed.</summary>
    event Action? StatusChanged;

    TasStatus Status { get; }

    /// <summary>How many state slots there are, numbered from zero.</summary>
    int SlotCount { get; }

    /// <summary>
    /// How much memory the rollback history may occupy.
    /// </summary>
    /// <remarks>
    /// Exposed because it is the one thing about the history a user has an opinion on:
    /// how much of their machine it is allowed to take. Lowering it thins the oldest part
    /// of the run rather than cutting the recent end off.
    /// </remarks>
    long HistoryBudgetBytes { get; set; }

    bool IsSlotOccupied(int slot);

    /// <summary>When a slot was saved, for showing which are recent.</summary>
    DateTimeOffset? SlotSavedAt(int slot);

    void AdvanceFrame();

    /// <summary>Back exactly one frame: the inverse of <see cref="AdvanceFrame"/>.</summary>
    void StepBackFrame();

    /// <summary>Back a tenth of a second, for covering ground.</summary>
    void StepBackTenth();

    void SaveState(int slot);

    void LoadState(int slot);

    void ToggleRecording();

    void TogglePause();
}
