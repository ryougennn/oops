using System.Drawing;

namespace oopshell;

/// <summary>
/// The damage of recent frames, so a renderer given a buffer of age N knows what to redraw:
/// this frame's damage plus the damage of the N-1 frames presented since that buffer's contents were made.
/// </summary>
public sealed class DamageHistory
{
    private readonly Rectangle[] _history;
    private int _count;

    public DamageHistory(int length = 8) => _history = new Rectangle[length];

    /// <summary>Records the damage of a frame that was just presented.</summary>
    public void Add(Rectangle damage)
    {
        Array.Copy(_history, 0, _history, 1, _history.Length - 1);
        _history[0] = damage;
        _count = Math.Min(_count + 1, _history.Length);
    }

    /// <summary>Forgets everything (after a resize, all buffers are new).</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// What to redraw in a buffer of <paramref name="age"/> (0 = undefined contents) for a frame with
    /// <paramref name="frameDamage"/>, clipped to <paramref name="size"/>.
    /// </summary>
    public Rectangle RepairArea(int age, Rectangle frameDamage, Size size)
    {
        var full = new Rectangle(Point.Empty, size);
        if (age <= 0 || age - 1 > _count)
            return full;
        var result = frameDamage;
        for (int i = 0; i < age - 1; i++)
            result = result.IsEmpty ? _history[i] : _history[i].IsEmpty ? result : Rectangle.Union(result, _history[i]);
        return Rectangle.Intersect(result, full);
    }
}
