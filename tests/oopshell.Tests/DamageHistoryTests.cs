using System.Drawing;

namespace oopshell.Tests;

public class DamageHistoryTests
{
    static readonly Size BufferSize = new(100, 100);
    static readonly Rectangle Full = new(Point.Empty, BufferSize);

    [Fact]
    public void Age_zero_means_full_redraw()
    {
        var history = new DamageHistory();
        Assert.Equal(Full, history.RepairArea(0, new Rectangle(0, 0, 5, 5), BufferSize));
    }

    [Fact]
    public void Age_one_needs_only_this_frames_damage()
    {
        var history = new DamageHistory();
        history.Add(new Rectangle(50, 50, 10, 10));
        Assert.Equal(new Rectangle(0, 0, 5, 5), history.RepairArea(1, new Rectangle(0, 0, 5, 5), BufferSize));
    }

    [Fact]
    public void Older_buffers_also_repair_frames_presented_since()
    {
        var history = new DamageHistory();
        history.Add(new Rectangle(90, 90, 10, 10)); // two frames ago
        history.Add(new Rectangle(10, 0, 10, 10));  // previous frame
        // Age 2: contents from two frames ago, so the previous frame's damage must be redrawn too.
        Assert.Equal(new Rectangle(0, 0, 20, 10), history.RepairArea(2, new Rectangle(0, 0, 5, 5), BufferSize));
        // Age 3: both earlier frames.
        Assert.Equal(new Rectangle(0, 0, 100, 100), history.RepairArea(3, new Rectangle(0, 0, 5, 5), BufferSize));
    }

    [Fact]
    public void Unknown_history_means_full_redraw()
    {
        var history = new DamageHistory(length: 2);
        history.Add(Rectangle.Empty);
        Assert.Equal(Full, history.RepairArea(3, new Rectangle(0, 0, 5, 5), BufferSize));
        history.Clear();
        Assert.Equal(Full, history.RepairArea(2, new Rectangle(0, 0, 5, 5), BufferSize));
    }
}
