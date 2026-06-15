namespace WinShot.Editor;

/// <summary>
/// A single undoable edit. The closures capture the affected canvas elements
/// or bitmaps; replaying actions strictly in stack order keeps annotation,
/// blur and crop state consistent with each other.
/// </summary>
public sealed class EditorAction
{
    private readonly Action _undo;
    private readonly Action _redo;
    private readonly Action? _onDiscard;

    /// <param name="onDiscard">
    /// Invoked when this action is permanently dropped from the redo stack because a
    /// new edit superseded it — the place to release resources (e.g. a blur backup
    /// bitmap) that can never be replayed again.
    /// </param>
    public EditorAction(Action undo, Action redo, Action? onDiscard = null)
    {
        _undo = undo;
        _redo = redo;
        _onDiscard = onDiscard;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
    public void Discard() => _onDiscard?.Invoke();
}
