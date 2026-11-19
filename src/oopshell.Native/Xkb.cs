using System.Runtime.InteropServices;

namespace oopshell.Native;

/// <summary>P/Invoke surface of <c>libxkbcommon.so.0</c> (keymaps, state, compose).</summary>
public static unsafe partial class Xkb
{
    private const string Lib = "libxkbcommon.so.0";

    public const int KeymapFormatTextV1 = 1;
    public const int ComposeFormatTextV1 = 1;
    public const int StateModsEffective = 1 << 3;
    public const int KeyUp = 0;
    public const int KeyDown = 1;

    public const int ComposeNothing = 0;
    public const int ComposeComposing = 1;
    public const int ComposeComposed = 2;
    public const int ComposeCancelled = 3;

    [LibraryImport(Lib)] public static partial nint xkb_context_new(int flags);
    [LibraryImport(Lib)] public static partial void xkb_context_unref(nint context);

    [LibraryImport(Lib)] public static partial nint xkb_keymap_new_from_buffer(nint context, byte* buffer, nuint length, int format, int flags);
    [LibraryImport(Lib)] public static partial void xkb_keymap_unref(nint keymap);
    [LibraryImport(Lib)] public static partial int xkb_keymap_key_repeats(nint keymap, uint key);
    [LibraryImport(Lib)] public static partial uint xkb_keymap_num_layouts(nint keymap);
    [LibraryImport(Lib)] public static partial byte* xkb_keymap_layout_get_name(nint keymap, uint layout);

    [LibraryImport(Lib)] public static partial nint xkb_state_new(nint keymap);
    [LibraryImport(Lib)] public static partial void xkb_state_unref(nint state);
    [LibraryImport(Lib)] public static partial int xkb_state_update_mask(nint state, uint depressed, uint latched, uint locked, uint depressedLayout, uint latchedLayout, uint lockedLayout);
    [LibraryImport(Lib)] public static partial int xkb_state_update_key(nint state, uint key, int direction);
    [LibraryImport(Lib)] public static partial uint xkb_state_key_get_one_sym(nint state, uint key);
    [LibraryImport(Lib)] public static partial int xkb_state_key_get_utf8(nint state, uint key, byte* buffer, nuint size);
    [LibraryImport(Lib)] public static partial int xkb_state_mod_name_is_active(nint state, byte* name, int type);
    [LibraryImport(Lib)] public static partial uint xkb_state_serialize_layout(nint state, int components);

    [LibraryImport(Lib)] public static partial nint xkb_compose_table_new_from_locale(nint context, byte* locale, int flags);
    [LibraryImport(Lib)] public static partial nint xkb_compose_table_new_from_buffer(nint context, byte* buffer, nuint length, byte* locale, int format, int flags);
    [LibraryImport(Lib)] public static partial void xkb_compose_table_unref(nint table);
    [LibraryImport(Lib)] public static partial nint xkb_compose_state_new(nint table, int flags);
    [LibraryImport(Lib)] public static partial void xkb_compose_state_unref(nint state);
    [LibraryImport(Lib)] public static partial int xkb_compose_state_feed(nint state, uint keysym);
    [LibraryImport(Lib)] public static partial void xkb_compose_state_reset(nint state);
    [LibraryImport(Lib)] public static partial int xkb_compose_state_get_status(nint state);
    [LibraryImport(Lib)] public static partial int xkb_compose_state_get_utf8(nint state, byte* buffer, nuint size);
}
