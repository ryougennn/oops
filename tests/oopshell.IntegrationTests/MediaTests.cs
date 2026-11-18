using oopshell.Services.DBus;
using oopshell.Services.Media;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class MediaTests(SwayFixture sway)
{
    /// <summary>A minimal MPRIS player whose state the test edits.</summary>
    sealed class FakePlayer(OopsApp app) : ExportedObject(app, "/org/mpris/MediaPlayer2")
    {
        public string Status = "Playing";
        public string Title = "Blue Hour";
        public string[] Artists = ["Mina", "Kei"];
        public readonly List<string> Calls = new();

        protected override void HandleMethod(MethodContext context)
        {
            string member = context.Request.MemberAsString ?? "";
            if (member != "GetAll")
            {
                Calls.Add(member);
                ReplyEmpty(context);
                return;
            }
            string iface = context.Request.GetBodyReader().ReadString();
            using var writer = context.CreateReplyWriter("a{sv}");
            var dict = writer.WriteDictionaryStart();
            if (iface == "org.mpris.MediaPlayer2")
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("Identity");
                writer.WriteVariantString("Fake Player");
                writer.WriteDictionaryEntryStart();
                writer.WriteString("DesktopEntry");
                writer.WriteVariantString("org.example.fake");
            }
            else
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("PlaybackStatus");
                writer.WriteVariantString(Status);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("CanGoNext");
                writer.WriteVariantBool(true);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("Metadata");
                writer.WriteSignature("a{sv}");
                var meta = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:title");
                writer.WriteVariantString(Title);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:artist");
                writer.WriteSignature("as");
                writer.WriteArray(Artists);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("xesam:album");
                writer.WriteVariantString("Night Drives");
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:length");
                writer.WriteVariantInt64(215_000_000);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:artUrl");
                writer.WriteVariantString("file:///tmp/cover.png");
                writer.WriteDictionaryEnd(meta);
            }
            writer.WriteDictionaryEnd(dict);
            context.Reply(writer.CreateMessage());
        }
    }

    static void EmitChanged(DBusConnection bus) =>
        bus.EmitSignal("/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "PropertiesChanged", "sa{sv}as", (ref MessageWriter w) =>
        {
            w.WriteString("org.mpris.MediaPlayer2.Player");
            w.WriteDictionaryEnd(w.WriteDictionaryStart());
            w.WriteArrayEnd(w.WriteArrayStart(DBusType.String));
        });

    [Fact]
    public void Players_are_found_followed_and_controlled()
    {
        using var bus = new DBusDaemon();
        using var app = sway.Connect();
        var shellBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var media = app.Complete(MediaService.StartAsync(app, shellBus));
        Assert.Empty(media.Players);

        // A player starts after the service.
        var playerBus = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var fake = new FakePlayer(app);
        playerBus.AddMethodHandler(fake);
        app.Complete(playerBus.RequestNameOrThrowAsync("org.mpris.MediaPlayer2.fake").ContinueWith(t => t.Exception is null));
        app.RunUntil(() => media.Players.Count == 1, what: "player appeared");
        var p = media.Active!;
        Assert.Equal("Fake Player", p.Identity);
        Assert.Equal("org.example.fake", p.DesktopEntry);
        Assert.Equal(PlaybackStatus.Playing, p.Status);
        Assert.Equal("Blue Hour", p.Title);
        Assert.Equal("Mina, Kei", p.Artist);
        Assert.Equal("Night Drives", p.Album);
        Assert.Equal(TimeSpan.FromSeconds(215), p.Length);
        Assert.Equal("file:///tmp/cover.png", p.ArtUrl);
        Assert.True(p.CanGoNext);
        Assert.False(p.CanGoPrevious);

        // It changes track and pauses.
        fake.Title = "Second Song";
        fake.Status = "Paused";
        EmitChanged(playerBus);
        app.RunUntil(() => p.Title == "Second Song", what: "track change");
        Assert.Equal(PlaybackStatus.Paused, p.Status);

        // Commands reach it.
        app.Complete(p.PlayPauseAsync().ContinueWith(_ => true));
        app.Complete(p.NextAsync().ContinueWith(_ => true));
        app.RunUntil(() => fake.Calls.Count == 2, what: "calls");
        Assert.Equal(["PlayPause", "Next"], fake.Calls);

        // It quits.
        playerBus.Dispose();
        app.RunUntil(() => media.Players.Count == 0, what: "player gone");
        Assert.Null(media.Active);
    }
}
