// InputContext.cs
// Phase 3 (Task 3c) で InputRouter が keymap handler へ渡す入力コンテキスト。
// state を持たない value-record(handler は Host / Caret を読み書きしてよいが、
// InputContext 自体は不変)。
namespace kxEdit.Editor;

/// <summary>Task 3c: InputRouter が keymap handler に渡す入力コンテキスト (value-record)。</summary>
internal readonly record struct InputContext(EditorControl Host, CaretController Caret);
