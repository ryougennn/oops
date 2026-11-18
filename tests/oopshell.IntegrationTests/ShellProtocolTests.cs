using System.Drawing;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class ShellProtocolTests(SwayFixture sway)
{
    [Fact]
    public void Foreign_toplevel_list_sees_and_closes_a_window()
    {
        using var app = sway.Connect();
        var list = app.Toplevels;
        Assert.NotNull(list);

        using var other = sway.Connect();
        using var window = new Toplevel(other) { Title = "Taskbar test", AppId = "oopshell.test.taskbar" };
        var frames = Draw.SolidColor(window, 0xFF000000);
        bool closeRequested = false;
        window.CloseRequested += () => closeRequested = true;
        other.RunUntil(() => frames() >= 1, what: "window");

        app.RunUntil(() => list!.Any(t => t.AppId == "oopshell.test.taskbar"), what: "foreign toplevel");
        var entry = list!.Single(t => t.AppId == "oopshell.test.taskbar");
        Assert.Equal("Taskbar test", entry.Title);
        Assert.True(entry.CanActivate);
        app.RunUntil(() => entry.IsActivated, what: "activated state");
        app.RunUntil(() => entry.Outputs.Count == 1, what: "output_enter");
        Assert.Same(app.Outputs[0], entry.Outputs[0]);

        window.Title = "Renamed";
        bool changed = false;
        entry.Changed += () => changed = true;
        other.RunFor(20);
        app.RunUntil(() => changed && entry.Title == "Renamed", what: "title change");

        entry.Close();
        app.Display.Flush();
        other.RunUntil(() => closeRequested, what: "close request");

        bool closed = false;
        entry.Closed += () => closed = true;
        window.Dispose();
        other.RunFor(20);
        app.RunUntil(() => closed && !list!.Contains(entry), what: "removal");
    }

    [Fact]
    public void Session_lock_covers_outputs_and_unlocks()
    {
        using var app = sway.Connect();
        var configured = new List<LockSurface>();
        var lockTask = app.LockAsync(surface =>
        {
            configured.Add(surface);
            Draw.SolidColor(surface, 0xFF000080);
        });
        app.RunUntil(() => lockTask.IsCompleted, what: "locked");
        var session = lockTask.Result;
        Assert.True(session.IsLocked);
        Assert.Single(configured);
        app.RunUntil(() => configured[0].IsConfigured, what: "lock surface configure");
        Assert.Equal(new Size(800, 600), configured[0].LogicalSize);
        app.RunFor(100);
        Assert.Equal((0, 0, 0x80), sway.Screenshot()[400, 300]);

        // An output plugged in while locked gets a lock surface too.
        sway.Swaymsg("create_output");
        app.RunUntil(() => configured.Count == 2 && configured[1].IsConfigured, what: "lock surface on new output");
        string added = configured[1].Output.Name;

        session.Unlock();
        app.Display.Roundtrip();
        Assert.False(session.IsLocked);
        Assert.Empty(session.Surfaces);
        sway.Swaymsg("output", added, "unplug");
        app.RunUntil(() => app.Outputs.Count == 1, what: "unplug");
    }

    [Fact]
    public void Idle_notification_fires_and_resumes_on_input()
    {
        using var app = sway.Connect();
        using var input = new VirtualInput(app);
        Assert.True(app.Idle.IsSupported);
        bool idle = false, resumed = false;
        using var watch = app.Idle.Watch(TimeSpan.FromMilliseconds(200), () => idle = true, () => resumed = true);
        app.RunUntil(() => idle, what: "idle");

        input.MoveTo(5, 5);
        input.MoveTo(6, 6);
        app.RunUntil(() => resumed, what: "resume");
    }
}
