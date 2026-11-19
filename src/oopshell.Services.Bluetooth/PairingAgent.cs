using oopshell.Services.DBus;
using Tmds.DBus.Protocol;

namespace oopshell.Services.Bluetooth;

public enum BluetoothPairingKind
{
    /// <summary>Type the PIN shown on (or printed with) the device: <see cref="BluetoothPairingRequest.Provide(string)"/>.</summary>
    EnterPinCode,
    /// <summary>Type the 6-digit passkey shown on the device: <see cref="BluetoothPairingRequest.Provide(uint)"/>.</summary>
    EnterPasskey,
    /// <summary>Show <see cref="BluetoothPairingRequest.PinCode"/> so the user types it on the device (a keyboard). No answer.</summary>
    ShowPinCode,
    /// <summary>Show <see cref="BluetoothPairingRequest.Passkey"/> so the user types it on the device. No answer.</summary>
    ShowPasskey,
    /// <summary>Does the device show the same <see cref="BluetoothPairingRequest.Passkey"/>? Accept or reject.</summary>
    ConfirmPasskey,
    /// <summary>Allow pairing with this device (no code)? Accept or reject.</summary>
    Authorize,
    /// <summary>Allow the device to use a service (<see cref="BluetoothPairingRequest.ServiceUuid"/>)? Accept or reject.</summary>
    AuthorizeService,
}

/// <summary>
/// BlueZ asks the user something while pairing. Show it, then <see cref="Accept"/>, <see cref="Reject"/> or
/// <see cref="Provide(string)"/>; requests of the Show kinds need no answer and end when pairing does
/// (<see cref="Finished"/>).
/// </summary>
public sealed class BluetoothPairingRequest
{
    readonly MethodContext? context;

    internal BluetoothPairingRequest(MethodContext? context, BluetoothPairingKind kind, string devicePath, BluetoothDevice? device)
    {
        this.context = context;
        Kind = kind;
        DevicePath = devicePath;
        Device = device;
        IsFinished = context is null;
    }

    public BluetoothPairingKind Kind { get; }
    /// <summary>The device, when it is known yet (it may appear in <see cref="BluetoothService.Devices"/> a moment later).</summary>
    public BluetoothDevice? Device { get; }
    public string DevicePath { get; }
    /// <summary>The passkey to show or confirm (six digits: format with "D6").</summary>
    public uint? Passkey { get; internal init; }
    /// <summary>The PIN to show.</summary>
    public string? PinCode { get; internal init; }
    /// <summary>The service a device wants to use (<see cref="BluetoothPairingKind.AuthorizeService"/>).</summary>
    public string? ServiceUuid { get; internal init; }
    public bool NeedsAnswer => context is not null;
    public bool IsFinished { get; private set; }

    /// <summary>The request ended: answered, or cancelled by BlueZ (pairing failed or finished) (UI thread).</summary>
    public event Action? Finished;

    public void Accept() => Reply(null, (ref MessageWriter w) => { });

    public void Reject() => Error("org.bluez.Error.Rejected", "Rejected by the user.");

    public void Provide(string pinCode) => Reply("s", (ref MessageWriter w) => w.WriteString(pinCode));

    public void Provide(uint passkey) => Reply("u", (ref MessageWriter w) => w.WriteUInt32(passkey));

    void Reply(string? signature, BodyWriter write)
    {
        if (IsFinished || context is null)
            return;
        var w = context.CreateReplyWriter(signature);
        try
        {
            write(ref w);
            context.Reply(w.CreateMessage());
        }
        finally
        {
            w.Dispose();
        }
        End();
    }

    void Error(string name, string message)
    {
        if (IsFinished || context is null)
            return;
        context.ReplyError(name, message);
        End();
    }

    void End()
    {
        IsFinished = true;
        context!.Dispose();
        Finished?.Invoke();
    }

    internal void CancelledByBlueZ()
    {
        if (context is null)
        {
            Finished?.Invoke(); // a Show request ends with the pairing
            return;
        }
        Error("org.bluez.Error.Canceled", "Cancelled.");
    }
}

/// <summary>The org.bluez.Agent1 object BlueZ calls while pairing.</summary>
sealed class PairingAgent(OopsApp app, BluetoothService service, string? bluez) : ExportedObject(app, "/org/oopshell/bluez/agent")
{
    const string Interface = "org.bluez.Agent1";
    readonly List<BluetoothPairingRequest> pending = new();

    protected override void HandleMethod(MethodContext context)
    {
        if (context.Request.InterfaceAsString != Interface || RefuseUnlessFrom(context, bluez))
            return;
        var reader = context.Request.GetBodyReader();
        string member = context.Request.MemberAsString ?? "";
        if (member is "Release" or "Cancel")
        {
            foreach (var request in pending.ToList())
                request.CancelledByBlueZ();
            pending.Clear();
            ReplyEmpty(context);
            return;
        }
        string path = reader.ReadObjectPathAsString();
        var device = service.Devices.FirstOrDefault(d => d.Path == path);
        if (!service.HasPairingHandlers)
        {
            ReplyError(context, "org.bluez.Error.Rejected", "No pairing dialog.");
            return;
        }
        BluetoothPairingRequest Ask(BluetoothPairingKind kind, uint? passkey = null, string? pin = null, string? uuid = null)
        {
            Defer(context);
            return new BluetoothPairingRequest(context, kind, path, device) { Passkey = passkey, PinCode = pin, ServiceUuid = uuid };
        }
        BluetoothPairingRequest Show(BluetoothPairingKind kind, uint? passkey = null, string? pin = null)
        {
            ReplyEmpty(context);
            return new BluetoothPairingRequest(null, kind, path, device) { Passkey = passkey, PinCode = pin };
        }
        var created = member switch
        {
            "RequestPinCode" => Ask(BluetoothPairingKind.EnterPinCode),
            "RequestPasskey" => Ask(BluetoothPairingKind.EnterPasskey),
            "DisplayPinCode" => Show(BluetoothPairingKind.ShowPinCode, pin: reader.ReadString()),
            "DisplayPasskey" => Show(BluetoothPairingKind.ShowPasskey, passkey: reader.ReadUInt32()),
            "RequestConfirmation" => Ask(BluetoothPairingKind.ConfirmPasskey, passkey: reader.ReadUInt32()),
            "RequestAuthorization" => Ask(BluetoothPairingKind.Authorize),
            "AuthorizeService" => Ask(BluetoothPairingKind.AuthorizeService, uuid: reader.ReadString()),
            _ => null,
        };
        if (created is null)
            return;
        pending.Add(created);
        created.Finished += () => pending.Remove(created);
        service.RaisePairingRequested(created);
    }

    public void CancelAll()
    {
        foreach (var request in pending.ToList())
            request.CancelledByBlueZ();
        pending.Clear();
    }
}
