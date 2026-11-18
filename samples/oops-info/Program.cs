// Prints the compositor's globals, outputs and seats, like wayland-info.
using oopshell;

using var app = OopsApp.Connect();

Console.WriteLine("Globals:");
foreach (var global in app.Globals.OrderBy(g => g.Name))
    Console.WriteLine($"  interface: '{global.Interface}', version: {global.Version}, name: {global.Name}");

Console.WriteLine("Outputs:");
foreach (var output in app.Outputs)
{
    Console.WriteLine($"  {output.Name}: {output.Description}");
    Console.WriteLine($"    make: '{output.Make}', model: '{output.Model}'");
    Console.WriteLine($"    mode: {output.PixelSize.Width}x{output.PixelSize.Height} @ {output.RefreshMilliHz / 1000.0:0.###} Hz");
    Console.WriteLine($"    scale: {output.Scale}, transform: {output.Transform}");
    Console.WriteLine($"    logical: {output.LogicalSize.Width}x{output.LogicalSize.Height} at {output.LogicalPosition.X},{output.LogicalPosition.Y}");
}

Console.WriteLine("Seats:");
foreach (var seat in app.Seats)
{
    Console.WriteLine($"  {seat.Name}: capabilities: {seat.Capabilities}");
    if (seat.KeymapLayoutName is { } layout)
        Console.WriteLine($"    keymap layout: {layout}");
}
