using System.Runtime.CompilerServices;

// The WPF host still reaches a small number of internal diagnostic/state APIs.
// Keep that access explicit while the host-facing surface is narrowed in later
// migration phases; portable consumers do not receive this access.
[assembly: InternalsVisibleTo("ZedExEss")]
