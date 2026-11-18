using System.Text;

namespace oopshell.Tests;

public class KeyboardStateTests
{
    // evdev codes from linux/input-event-codes.h
    const uint KEY_A = 30, KEY_E = 18, KEY_LEFTSHIFT = 42, KEY_APOSTROPHE = 40, KEY_ENTER = 28, KEY_RIGHTALT = 100;

    static KeyboardState Create()
    {
        var state = new KeyboardState();
        string keymap = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/us-compose-ralt.xkb"));
        state.SetKeymap(Encoding.UTF8.GetBytes(keymap + "\0"));
        state.LoadCompose("<Multi_key> <apostrophe> <e> : \"é\" eacute\n");
        return state;
    }

    [Fact]
    public void Plain_key_gives_keysym_and_text()
    {
        using var state = Create();
        Assert.Equal(Keysym.a, state.GetKeysym(KEY_A));
        Assert.Equal("a", state.GetText(KEY_A));
        Assert.Equal(Modifiers.None, state.Modifiers);
    }

    [Fact]
    public void Shift_changes_keysym_text_and_modifiers()
    {
        using var state = Create();
        state.UpdateKey(KEY_LEFTSHIFT, pressed: true);
        Assert.Equal(Keysym.A, state.GetKeysym(KEY_A));
        Assert.Equal("A", state.GetText(KEY_A));
        Assert.Equal(Modifiers.Shift, state.Modifiers);
    }

    [Fact]
    public void Compose_sequence_produces_e_acute()
    {
        using var state = Create();
        Assert.Equal(Keysym.Multi_key, state.GetKeysym(KEY_RIGHTALT));
        Assert.Null(state.GetText(KEY_RIGHTALT));
        Assert.Null(state.GetText(KEY_APOSTROPHE));
        Assert.Equal("é", state.GetText(KEY_E));
        Assert.Equal("e", state.GetText(KEY_E)); // compose state was reset
    }

    [Fact]
    public void Control_keys_produce_no_text()
    {
        using var state = Create();
        Assert.Equal(Keysym.Return, state.GetKeysym(KEY_ENTER));
        Assert.Null(state.GetText(KEY_ENTER));
    }

    [Fact]
    public void Layout_name_and_repeat_come_from_the_keymap()
    {
        using var state = Create();
        Assert.Equal("English (US)", state.LayoutName);
        Assert.True(state.KeyRepeats(KEY_A));
        Assert.False(state.KeyRepeats(KEY_LEFTSHIFT));
    }
}
