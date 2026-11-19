using System.Text;
using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Network;

/// <summary>
/// NetworkManager needs a secret, e.g. the password of a Wi-Fi network that has none saved, or whose saved
/// one was refused (<see cref="IsRetry"/>). Answer with <see cref="Provide"/> or <see cref="Cancel"/>.
/// </summary>
public sealed class NetworkSecretRequest
{
    private readonly MethodContext _context;
    private readonly string _key;

    internal NetworkSecretRequest(MethodContext context, string connectionName, string? ssid, string settingName, string key, bool isRetry)
    {
        _context = context;
        _key = key;
        ConnectionName = connectionName;
        Ssid = ssid;
        SettingName = settingName;
        IsRetry = isRetry;
    }

    /// <summary>The connection's name (for Wi-Fi, usually the network name).</summary>
    public string ConnectionName { get; }
    /// <summary>The Wi-Fi network, for Wi-Fi connections.</summary>
    public string? Ssid { get; }
    /// <summary>NetworkManager's setting the secret belongs to, e.g. <c>802-11-wireless-security</c>.</summary>
    public string SettingName { get; }
    /// <summary>The previous secret was wrong: say so in the dialog.</summary>
    public bool IsRetry { get; }
    public bool IsFinished { get; private set; }

    /// <summary>NetworkManager stopped waiting (connection cancelled or timed out) (UI thread).</summary>
    public event Action? Cancelled;

    /// <summary>Hands the password to NetworkManager, which stores it with the connection.</summary>
    public void Provide(string secret)
    {
        if (IsFinished)
            return;
        IsFinished = true;
        using (var w = _context.CreateReplyWriter("a{sa{sv}}"))
        {
            var all = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString(SettingName);
            w.WriteDictionary(new Dictionary<string, VariantValue> { [_key] = secret });
            w.WriteDictionaryEnd(all);
            _context.Reply(w.CreateMessage());
        }
        _context.Dispose();
    }

    /// <summary>The user gave up; the connection attempt fails.</summary>
    public void Cancel()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        _context.ReplyError("org.freedesktop.NetworkManager.SecretAgent.UserCanceled", "Cancelled by the user.");
        _context.Dispose();
    }

    internal void CancelledByNetworkManager()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        _context.ReplyError("org.freedesktop.NetworkManager.SecretAgent.AgentCanceled", "Cancelled.");
        _context.Dispose();
        Cancelled?.Invoke();
    }

    internal string ConnectionPath { get; init; } = "";
}

/// <summary>The secret agent object NetworkManager calls (org.freedesktop.NetworkManager.SecretAgent).</summary>
internal sealed class SecretAgent(OopsApp app, NetworkService service, string? networkManager) : ExportedObject(app, "/org/freedesktop/NetworkManager/SecretAgent")
{
    private const string Interface = "org.freedesktop.NetworkManager.SecretAgent";
    private readonly List<NetworkSecretRequest> _pending = new();

    protected override void HandleMethod(MethodContext context)
    {
        // Only NetworkManager itself: anyone else asking would be collecting the password the user types.
        if (context.Request.InterfaceAsString != Interface || RefuseUnlessFrom(context, networkManager))
            return;
        var reader = context.Request.GetBodyReader();
        switch (context.Request.MemberAsString)
        {
            case "GetSecrets":
                var settings = ReadSettings(ref reader);
                string path = reader.ReadObjectPathAsString(), settingName = reader.ReadString();
                reader.ReadArrayOfString(); // hints
                uint flags = reader.ReadUInt32();
                GetSecrets(context, settings, path, settingName, flags);
                break;
            case "CancelGetSecrets":
                string cancelPath = reader.ReadObjectPathAsString();
                foreach (var request in _pending.Where(r => r.ConnectionPath == cancelPath).ToList())
                {
                    _pending.Remove(request);
                    request.CancelledByNetworkManager();
                }
                ReplyEmpty(context);
                break;
            case "SaveSecrets" or "DeleteSecrets":
                ReplyEmpty(context); // secrets given to NetworkManager are system-owned: it stores them itself
                break;
        }
    }

    private void GetSecrets(MethodContext context, Dictionary<string, Dictionary<string, VariantValue>> settings, string path, string settingName, uint flags)
    {
        const uint AllowInteraction = 0x1, RequestNew = 0x2;
        // Wi-Fi passwords only: other secrets (VPN, enterprise 802.1X) are left to dedicated agents.
        if (settingName != "802-11-wireless-security" || (flags & AllowInteraction) == 0 || !service.HasSecretHandlers)
        {
            ReplyError(context, "org.freedesktop.NetworkManager.SecretAgent.NoSecrets", "No secrets available.");
            return;
        }
        string keyManagement = settings.TryGetValue(settingName, out var security) && security.TryGetValue("key-mgmt", out var k) ? k.AsString() ?? "wpa-psk" : "wpa-psk";
        string key = keyManagement == "none" ? "wep-key0" : "psk";
        string name = settings.TryGetValue("connection", out var c) && c.TryGetValue("id", out var id) ? id.AsString() ?? "" : "";
        string? ssid = settings.TryGetValue("802-11-wireless", out var w) && w.TryGetValue("ssid", out var s) && s.AsBytes() is { } ssidBytes ? Encoding.UTF8.GetString(ssidBytes) : null;
        Defer(context);
        var request = new NetworkSecretRequest(context, name, ssid, settingName, key, (flags & RequestNew) != 0) { ConnectionPath = path };
        _pending.Add(request);
        request.Cancelled += () => _pending.Remove(request);
        service.RaiseSecretsRequested(request);
    }

    private static Dictionary<string, Dictionary<string, VariantValue>> ReadSettings(ref Reader reader)
    {
        var result = new Dictionary<string, Dictionary<string, VariantValue>>();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
            result[reader.ReadString()] = reader.ReadDictionaryOfStringToVariantValue();
        return result;
    }

    public void CancelAll()
    {
        foreach (var request in _pending.ToList())
            request.Cancel();
        _pending.Clear();
    }
}
