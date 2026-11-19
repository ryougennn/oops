using oopshell.Protocol;

namespace oopshell;

public abstract partial class Surface
{
    bool inhibitIdle, inhibitShortcuts;
    ZwpIdleInhibitorV1? idleInhibitor;
    ZwpKeyboardShortcutsInhibitorV1? shortcutsInhibitor;

    /// <summary>
    /// While true and the surface is visible, the screen does not blank, dim or lock for inactivity (a video
    /// player, a presentation, a "keep awake" toggle). Ignored where unsupported (<see cref="OopsApp.CanInhibitIdle"/>).
    /// </summary>
    public bool InhibitIdle
    {
        get => inhibitIdle;
        set
        {
            if (value == inhibitIdle || IsDisposed)
                return;
            inhibitIdle = value;
            if (value && App.TryGet<ZwpIdleInhibitManagerV1>() is { } manager)
                idleInhibitor = manager.CreateInhibitor(Proxy);
            else if (!value)
            {
                idleInhibitor?.Dispose();
                idleInhibitor = null;
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
        get => inhibitShortcuts;
        set
        {
            if (value == inhibitShortcuts || IsDisposed)
                return;
            inhibitShortcuts = value;
            if (value && App.TryGet<ZwpKeyboardShortcutsInhibitManagerV1>() is { } manager && App.DefaultSeat is { } seat)
            {
                shortcutsInhibitor = manager.InhibitShortcuts(Proxy, seat.Proxy);
                shortcutsInhibitor.Active += () => SetShortcutsInhibited(true);
                shortcutsInhibitor.Inactive += () => SetShortcutsInhibited(false);
            }
            else if (!value)
            {
                shortcutsInhibitor?.Dispose();
                shortcutsInhibitor = null;
                SetShortcutsInhibited(false);
            }
        }
    }

    /// <summary>The compositor's shortcuts currently go to this surface (see <see cref="InhibitShortcuts"/>).</summary>
    public bool ShortcutsInhibited { get; private set; }

    /// <summary><see cref="ShortcutsInhibited"/> changed: e.g. the surface lost focus, or the user overrode it.</summary>
    public event Action? ShortcutsInhibitedChanged;

    void SetShortcutsInhibited(bool active)
    {
        if (active == ShortcutsInhibited)
            return;
        ShortcutsInhibited = active;
        ShortcutsInhibitedChanged?.Invoke();
    }

    void DisposeInhibitors()
    {
        idleInhibitor?.Dispose();
        idleInhibitor = null;
        shortcutsInhibitor?.Dispose();
        shortcutsInhibitor = null;
    }
}

public sealed partial class OopsApp
{
    /// <summary>The compositor honors <see cref="Surface.InhibitIdle"/>.</summary>
    public bool CanInhibitIdle => Has<ZwpIdleInhibitManagerV1>();

    /// <summary>The compositor honors <see cref="Surface.InhibitShortcuts"/>.</summary>
    public bool CanInhibitShortcuts => Has<ZwpKeyboardShortcutsInhibitManagerV1>();
}
