using oopshell.Protocol;

namespace oopshell;

public abstract partial class Surface
{
    private bool _inhibitIdle, _inhibitShortcuts;
    private ZwpIdleInhibitorV1? _idleInhibitor;
    private ZwpKeyboardShortcutsInhibitorV1? _shortcutsInhibitor;

    /// <summary>
    /// While true and the surface is visible, the screen does not blank, dim or lock for inactivity (a video
    /// player, a presentation, a "keep awake" toggle). Ignored where unsupported (<see cref="OopsApp.CanInhibitIdle"/>).
    /// </summary>
    public bool InhibitIdle
    {
        get => _inhibitIdle;
        set
        {
            if (value == _inhibitIdle || IsDisposed)
                return;
            _inhibitIdle = value;
            if (value && App.TryGet<ZwpIdleInhibitManagerV1>() is { } manager)
                _idleInhibitor = manager.CreateInhibitor(Proxy);
            else if (!value)
            {
                _idleInhibitor?.Dispose();
                _idleInhibitor = null;
            }
        }
    }

    /// <summary>
    /// Asks the compositor to pass its own key bindings (Super+…, Alt+Tab) to this surface while it has the
    /// keyboard: for kiosks, games, remote desktops, virtual machines. The compositor decides, and may let the
    /// user override; <see cref="ShortcutsInhibited"/> says whether it is in effect. Uses the default seat.
    /// </summary>
    public bool InhibitShortcuts
    {
        get => _inhibitShortcuts;
        set
        {
            if (value == _inhibitShortcuts || IsDisposed)
                return;
            _inhibitShortcuts = value;
            if (value && App.TryGet<ZwpKeyboardShortcutsInhibitManagerV1>() is { } manager && App.DefaultSeat is { } seat)
            {
                _shortcutsInhibitor = manager.InhibitShortcuts(Proxy, seat.Proxy);
                _shortcutsInhibitor.Active += () => SetShortcutsInhibited(true);
                _shortcutsInhibitor.Inactive += () => SetShortcutsInhibited(false);
            }
            else if (!value)
            {
                _shortcutsInhibitor?.Dispose();
                _shortcutsInhibitor = null;
                SetShortcutsInhibited(false);
            }
        }
    }

    /// <summary>The compositor's shortcuts currently go to this surface (see <see cref="InhibitShortcuts"/>).</summary>
    public bool ShortcutsInhibited { get; private set; }

    /// <summary><see cref="ShortcutsInhibited"/> changed: e.g. the surface lost focus, or the user overrode it.</summary>
    public event Action? ShortcutsInhibitedChanged;

    private void SetShortcutsInhibited(bool active)
    {
        if (active == ShortcutsInhibited)
            return;
        ShortcutsInhibited = active;
        ShortcutsInhibitedChanged?.Invoke();
    }

    private void DisposeInhibitors()
    {
        _idleInhibitor?.Dispose();
        _idleInhibitor = null;
        _shortcutsInhibitor?.Dispose();
        _shortcutsInhibitor = null;
    }
}

public sealed partial class OopsApp
{
    /// <summary>The compositor honors <see cref="Surface.InhibitIdle"/>.</summary>
    public bool CanInhibitIdle => Has<ZwpIdleInhibitManagerV1>();

    /// <summary>The compositor honors <see cref="Surface.InhibitShortcuts"/>.</summary>
    public bool CanInhibitShortcuts => Has<ZwpKeyboardShortcutsInhibitManagerV1>();
}
