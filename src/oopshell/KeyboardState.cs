using System.Text;
using oopshell.Protocol;
using static oopshell.Native.Xkb;

namespace oopshell;

/// <summary>Keyboard modifiers in effect for a key event.</summary>
[Flags]
public enum Modifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Super = 8,
    CapsLock = 16,
    NumLock = 32,
}

/// <summary>
/// The xkbcommon keymap, state and compose state of one keyboard; turns evdev keycodes into keysyms and text.
/// </summary>
/// <remarks>Separate from <see cref="Seat"/> so it can be tested with a fixed keymap.</remarks>
internal sealed unsafe class KeyboardState : IDisposable
{
    private readonly nint _context = xkb_context_new(0);
    private nint _keymap;
    private nint _state;
    private nint _composeTable;
    private nint _composeState;

    public KeyboardState()
    {
        if (_context == 0)
            throw new InvalidOperationException("xkb_context_new failed.");
    }

    /// <summary>Loads a keymap in xkb text format (what the compositor sends).</summary>
    public void SetKeymap(ReadOnlySpan<byte> text)
    {
        // The compositor's buffer usually ends in a NUL that xkbcommon must not see as content.
        int length = text.IndexOf((byte)0) is int nul and >= 0 ? nul : text.Length;
        nint newKeymap;
        fixed (byte* p = text)
            newKeymap = xkb_keymap_new_from_buffer(_context, p, (nuint)length, KeymapFormatTextV1, 0);
        if (newKeymap == 0)
            throw new InvalidOperationException("The compositor sent a keymap xkbcommon cannot parse.");
        ReleaseKeymap();
        _keymap = newKeymap;
        _state = xkb_state_new(_keymap);
    }

    /// <summary>Loads compose sequences for the locale from LC_ALL/LC_CTYPE/LANG.</summary>
    public void LoadComposeFromLocale()
    {
        string locale = FirstNonEmpty("LC_ALL", "LC_CTYPE", "LANG") ?? "C";
        using var localeUtf8 = new NativeUtf8(locale);
        SetComposeTable(xkb_compose_table_new_from_locale(_context, localeUtf8.Pointer, 0));
    }

    /// <summary>Loads compose sequences from Compose-file text (used by tests).</summary>
    public void LoadCompose(string composeText, string locale = "en_US.UTF-8")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(composeText);
        using var localeUtf8 = new NativeUtf8(locale);
        fixed (byte* p = bytes)
            SetComposeTable(xkb_compose_table_new_from_buffer(_context, p, (nuint)bytes.Length, localeUtf8.Pointer, ComposeFormatTextV1, 0));
    }

    private void SetComposeTable(nint table)
    {
        if (_composeState != 0)
            xkb_compose_state_unref(_composeState);
        if (_composeTable != 0)
            xkb_compose_table_unref(_composeTable);
        _composeTable = table;
        _composeState = table == 0 ? 0 : xkb_compose_state_new(table, 0);
    }

    public void UpdateModifiers(uint depressed, uint latched, uint locked, uint group)
    {
        if (_state != 0)
            xkb_state_update_mask(_state, depressed, latched, locked, 0, 0, group);
    }

    /// <summary>Updates state from a raw key press or release; only needed without compositor modifier events (tests).</summary>
    public void UpdateKey(uint evdevKey, bool pressed)
    {
        if (_state != 0)
            xkb_state_update_key(_state, evdevKey + 8, pressed ? KeyDown : KeyUp);
    }

    public Keysym GetKeysym(uint evdevKey) => _state == 0 ? Keysym.NoSymbol : (Keysym)xkb_state_key_get_one_sym(_state, evdevKey + 8);

    public bool KeyRepeats(uint evdevKey) => _keymap != 0 && xkb_keymap_key_repeats(_keymap, evdevKey + 8) != 0;

    /// <summary>
    /// The text a key press produces, running it through compose. Returns null while a compose sequence
    /// is in progress or was cancelled, and for keys that produce only control characters.
    /// </summary>
    public string? GetText(uint evdevKey, bool useCompose = true)
    {
        if (_state == 0)
            return null;
        if (useCompose && _composeState != 0)
        {
            xkb_compose_state_feed(_composeState, (uint)GetKeysym(evdevKey));
            switch (xkb_compose_state_get_status(_composeState))
            {
                case ComposeComposing:
                    return null;
                case ComposeCancelled:
                    xkb_compose_state_reset(_composeState);
                    return null;
                case ComposeComposed:
                    byte* composed = stackalloc byte[64];
                    int composedLength = xkb_compose_state_get_utf8(_composeState, composed, 64);
                    xkb_compose_state_reset(_composeState);
                    return Printable(composed, composedLength);
            }
        }
        byte* text = stackalloc byte[64];
        return Printable(text, xkb_state_key_get_utf8(_state, evdevKey + 8, text, 64));
    }

    public Modifiers Modifiers
    {
        get
        {
            if (_state == 0)
                return Modifiers.None;
            var result = Modifiers.None;
            if (IsActive("Shift")) result |= Modifiers.Shift;
            if (IsActive("Control")) result |= Modifiers.Ctrl;
            if (IsActive("Mod1")) result |= Modifiers.Alt;
            if (IsActive("Mod4")) result |= Modifiers.Super;
            if (IsActive("Lock")) result |= Modifiers.CapsLock;
            if (IsActive("Mod2")) result |= Modifiers.NumLock;
            return result;
        }
    }

    /// <summary>The name of the active layout, e.g. "English (US)".</summary>
    public string? LayoutName =>
        _state == 0 ? null : WlProxy.ReadString(xkb_keymap_layout_get_name(_keymap, xkb_state_serialize_layout(_state, 1 << 7)));

    private bool IsActive(string modifier)
    {
        using var name = new NativeUtf8(modifier);
        return xkb_state_mod_name_is_active(_state, name.Pointer, StateModsEffective) > 0;
    }

    /// <summary>Decodes xkbcommon's UTF-8 output (length excludes the NUL; it may exceed the buffer when truncated).</summary>
    private static string? Printable(byte* utf8, int length)
    {
        if (length <= 0)
            return null;
        string text = Encoding.UTF8.GetString(utf8, Math.Min(length, 63));
        return text.All(char.IsControl) ? null : text;
    }

    private static string? FirstNonEmpty(params string[] variables) =>
        variables.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private void ReleaseKeymap()
    {
        if (_state != 0)
            xkb_state_unref(_state);
        if (_keymap != 0)
            xkb_keymap_unref(_keymap);
        _state = _keymap = 0;
    }

    public void Dispose()
    {
        ReleaseKeymap();
        SetComposeTable(0);
        xkb_context_unref(_context);
    }
}
