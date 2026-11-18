using oopshell.Services.DBus;
using oopshell.Services.Tray;
using Tmds.DBus.Protocol;

namespace oopshell.IntegrationTests;

[Collection("sway")]
public class TrayTests(SwayFixture sway)
{
    /// <summary>A minimal tray app: a StatusNotifierItem with a two-entry dbusmenu.</summary>
    sealed class FakeItem(OopsApp app) : ExportedObject(app, "/StatusNotifierItem")
    {
        public int Activations;
        public string Title = "Fake app";

        protected override void HandleMethod(MethodContext context)
        {
            var request = context.Request;
            switch ((request.InterfaceAsString, request.MemberAsString))
            {
                case ("org.kde.StatusNotifierItem", "Activate"):
                    Activations++;
                    ReplyEmpty(context);
                    break;
                case ("org.freedesktop.DBus.Properties", "GetAll"):
                {
                    var writer = context.CreateReplyWriter("a{sv}");
                    try
                    {
                        var dict = writer.WriteDictionaryStart();
                        Entry(ref writer, "Id", "fake");
                        Entry(ref writer, "Title", Title);
                        Entry(ref writer, "Status", "Active");
                        Entry(ref writer, "IconName", "dialog-information");
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString("Menu");
                        writer.WriteVariantObjectPath("/Menu");
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString("IconPixmap");
                        writer.WriteSignature("a(iiay)");
                        var pixmaps = writer.WriteArrayStart(DBusType.Struct);
                        writer.WriteStructureStart();
                        writer.WriteInt32(2);
                        writer.WriteInt32(2);
                        writer.WriteArray(new byte[16]);
                        writer.WriteArrayEnd(pixmaps);
                        writer.WriteDictionaryEnd(dict);
                        context.Reply(writer.CreateMessage());
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                    break;
                }
            }
        }

        static void Entry(ref MessageWriter writer, string key, string value)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(key);
            writer.WriteVariantString(value);
        }
    }

    sealed class FakeMenu(OopsApp app) : ExportedObject(app, "/Menu")
    {
        public readonly List<int> Clicked = new();

        protected override void HandleMethod(MethodContext context)
        {
            switch (context.Request.MemberAsString)
            {
                case "GetLayout":
                {
                    var writer = context.CreateReplyWriter("u(ia{sv}av)");
                    try
                    {
                        writer.WriteUInt32(1);
                        WriteNode(ref writer, 0, ("children-display", "submenu"), [(1, "_Open window", null), (2, null, "separator"), (3, "_Quit", null)]);
                        context.Reply(writer.CreateMessage());
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                    break;
                }
                case "Event":
                    Clicked.Add(context.Request.GetBodyReader().ReadInt32());
                    ReplyEmpty(context);
                    break;
            }
        }

        /// <summary>Writes an (ia{sv}av) node whose children are leaves with a label or a type.</summary>
        static void WriteNode(ref MessageWriter writer, int id, (string Key, string Value) property, (int Id, string? Label, string? Type)[] children)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(id);
            var props = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString(property.Key);
            writer.WriteVariantString(property.Value);
            writer.WriteDictionaryEnd(props);
            var list = writer.WriteArrayStart(DBusType.Variant);
            foreach (var child in children)
            {
                writer.WriteSignature("(ia{sv}av)");
                writer.WriteStructureStart();
                writer.WriteInt32(child.Id);
                var childProps = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString(child.Label is null ? "type" : "label");
                writer.WriteVariantString(child.Label ?? child.Type!);
                writer.WriteDictionaryEnd(childProps);
                var empty = writer.WriteArrayStart(DBusType.Variant);
                writer.WriteArrayEnd(empty);
            }
            writer.WriteArrayEnd(list);
        }
    }

    [Fact]
    public void Host_sees_items_reads_menus_and_forwards_clicks()
    {
        using var bus = new DBusDaemon();
        using var app = sway.Connect();
        var hostConnection = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        using var host = app.Complete(TrayHost.StartAsync(app, hostConnection));
        Assert.True(host.IsWatcher);

        var itemConnection = app.Complete(OopsDBus.ConnectAsync(app, bus.Address));
        var fake = new FakeItem(app);
        var fakeMenu = new FakeMenu(app);
        itemConnection.AddMethodHandler(fake);
        itemConnection.AddMethodHandler(fakeMenu);
        app.Complete(itemConnection.CallAsync(TrayHost.WatcherName, TrayHost.WatcherPath, TrayHost.WatcherName, "RegisterStatusNotifierItem",
            (ref MessageWriter w) => w.WriteString("/StatusNotifierItem"), "s").ContinueWith(_ => true));

        app.RunUntil(() => host.Items.Count == 1, what: "tray item");
        var item = host.Items[0];
        Assert.Equal("fake", item.Id);
        Assert.Equal("Fake app", item.Title);
        Assert.Equal("dialog-information", item.IconName);
        Assert.Equal("/Menu", item.MenuPath);
        Assert.Equal(2, Assert.Single(item.IconPixmaps).Width);

        app.Complete(item.ActivateAsync().ContinueWith(_ => true));
        app.RunUntil(() => fake.Activations == 1, what: "activate");

        using var menu = app.Complete(item.GetMenuAsync())!;
        var entries = menu.Root.Children;
        Assert.Equal(["Open window", "", "Quit"], entries.Select(e => e.Label));
        Assert.Equal('O', entries[0].Mnemonic);
        Assert.True(entries[1].IsSeparator);
        app.Complete(menu.ClickAsync(entries[2]).ContinueWith(_ => true));
        app.RunUntil(() => fakeMenu.Clicked.Contains(3), what: "menu click");

        // The item's app exits: the watcher notices its bus name vanish.
        itemConnection.Dispose();
        app.RunUntil(() => host.Items.Count == 0, what: "item removal");
    }
}
