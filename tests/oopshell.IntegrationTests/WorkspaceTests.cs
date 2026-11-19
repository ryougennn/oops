namespace oopshell.IntegrationTests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Workspaces_list_and_switch_on_niri()
    {
        using var niri = new NiriFixture("workspace \"web\"\nworkspace \"chat\"\n");
        using var app = niri.Connect();
        Assert.NotNull(app.Workspaces);
        app.RunUntil(() => app.Workspaces!.Any(w => w.Name == "chat"), what: "named workspaces");

        var web = app.Workspaces!.Single(w => w.Name == "web");
        var chat = app.Workspaces!.Single(w => w.Name == "chat");
        Assert.True(web.IsActive); // the first one is shown
        Assert.False(chat.IsActive);
        Assert.True(app.Workspaces!.IndexOf(web) < app.Workspaces!.IndexOf(chat)); // layout order
        Assert.Equal(app.Outputs.Single(), Assert.Single(chat.Outputs));
        Assert.True(chat.CanActivate);

        int changes = 0;
        chat.Changed += () => changes++;
        chat.Activate();
        app.RunUntil(() => chat.IsActive && !web.IsActive, what: "chat shown");
        Assert.True(changes >= 1);
    }

    [Fact]
    public void Compositors_without_workspaces_say_so()
    {
        using var sway = new SwayFixture();
        using var app = sway.Connect();
        Assert.Null(app.Workspaces); // sway has no ext-workspace
        Assert.False(app.CreateWorkspace("x"));
    }
}
