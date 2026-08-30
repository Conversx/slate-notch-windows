using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Slate.Notch;

public enum NotchPhase
{
    /// <summary>A slim bar at the top of the screen.</summary>
    Closed,
    /// <summary>Cursor is over it: swollen slightly and glowing.</summary>
    Hinted,
    /// <summary>The full dashboard.</summary>
    Open
}

public enum NotchTab { Now, Shelf, Clipboard }

public sealed class NotchState : INotifyPropertyChanged
{
    private NotchPhase _phase = NotchPhase.Closed;
    private NotchTab _tab = NotchTab.Now;
    private bool _isDropTargeted;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<NotchPhase>? PhaseChanged;

    public NotchPhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            Raise();
            Raise(nameof(IsOpen));
            PhaseChanged?.Invoke(value);
        }
    }

    public NotchTab Tab
    {
        get => _tab;
        set { if (_tab != value) { _tab = value; Raise(); } }
    }

    public bool IsDropTargeted
    {
        get => _isDropTargeted;
        private set { if (_isDropTargeted != value) { _isDropTargeted = value; Raise(); } }
    }

    public bool IsOpen => _phase == NotchPhase.Open;

    // MARK: transitions

    public void Hover(bool inside)
    {
        // An open panel ignores the cursor entirely: it closes on a click outside or on
        // the X, never by drifting away from it.
        if (_phase == NotchPhase.Open) return;
        Phase = inside ? NotchPhase.Hinted : NotchPhase.Closed;
    }

    public void Open() => Phase = NotchPhase.Open;

    public void Close()
    {
        IsDropTargeted = false;
        Phase = NotchPhase.Closed;
    }

    /// <summary>Tapping the bar opens it; tapping inside an open panel does nothing.</summary>
    public void TapOnBar()
    {
        if (_phase != NotchPhase.Open) Open();
    }

    public void DropTargeted(bool targeted)
    {
        IsDropTargeted = targeted;
        if (targeted)
        {
            Tab = NotchTab.Shelf;
            Open();
        }
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
