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

    public EditorAction(Action undo, Action redo)
    {
        _undo = undo;
        _redo = redo;
    }

    public void Undo() => _undo();
    public void Redo() => _redo();
}
