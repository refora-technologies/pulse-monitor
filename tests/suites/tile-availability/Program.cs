// Which tiles Pulse offers as switches, and which it explains instead.
//
// The rule being protected: a tile is only switched off for a fact about the machine that will
// still be true in a minute. Never because a reading is missing, which happens at every logon
// and every wake. And a tile that can read must never be switched off, because the user has no
// way to turn it back on.

using Pulse.Models;

int pass = 0, fail = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
    if (ok) pass++; else fail++;
}

// Every tile in the catalogue, by id. Kept here rather than read from the product so that a tile
// added later without a decision about it shows up as a difference someone has to look at.
string[] all =
{
    "cpu_usage", "cpu_temp", "cpu_clock", "cpu_power", "gpu_usage", "gpu_temp", "gpu_clock",
    "gpu_power", "gpu_vram", "ram_used", "fps", "fps_1low", "net_upload", "net_download",
    "disk_activity", "battery", "sys_power",
};

string[] driverTiles = { "cpu_temp", "cpu_power", "cpu_clock", "sys_power" };

Console.WriteLine("Tile availability\n");

var healthy = new MachineFacts(DriverState.Running, HasBattery: true);
foreach (var id in all)
    Check($"everything reads on a healthy laptop: {id}", TileAvailability.Evaluate(id, healthy) == TileStatus.Ready);

var noDriver = new MachineFacts(DriverState.NotInstalled, HasBattery: true);
foreach (var id in all)
{
    var expected = driverTiles.Contains(id) ? TileStatus.NeedsDriver : TileStatus.Ready;
    var got = TileAvailability.Evaluate(id, noDriver);
    Check($"without the driver: {id} is {expected}", got == expected, got.ToString());
}

var notLoading = new MachineFacts(DriverState.NotRunning, HasBattery: true);
foreach (var id in driverTiles)
{
    var got = TileAvailability.Evaluate(id, notLoading);
    Check($"driver installed but not running: {id} says so rather than asking for an install",
          got == TileStatus.DriverNotLoading, got.ToString());
}

// GPU Temp has a Windows fallback, so the driver must not take it away.
Check("GPU Temp never needs the driver", TileAvailability.Evaluate("gpu_temp", noDriver) == TileStatus.Ready);

var desktop = new MachineFacts(DriverState.Running, HasBattery: false);
Check("a desktop has no battery tile", TileAvailability.Evaluate("battery", desktop) == TileStatus.NotOnThisPc);
foreach (var id in all.Where(i => i != "battery"))
    Check($"a desktop still reads {id}", TileAvailability.Evaluate(id, desktop) == TileStatus.Ready);

var bareDesktop = new MachineFacts(DriverState.NotInstalled, HasBattery: false);
Check("no driver and no battery are told apart: battery",
      TileAvailability.Evaluate("battery", bareDesktop) == TileStatus.NotOnThisPc);
Check("no driver and no battery are told apart: CPU Temp",
      TileAvailability.Evaluate("cpu_temp", bareDesktop) == TileStatus.NeedsDriver);

Check("the driver list is exactly the measured four",
      TileAvailability.DriverTiles.OrderBy(x => x).SequenceEqual(driverTiles.OrderBy(x => x)),
      string.Join(",", TileAvailability.DriverTiles));

// An id this build does not know is left alone: settings keep ids from newer versions.
Check("an unknown tile id is not switched off",
      TileAvailability.Evaluate("some_future_tile", bareDesktop) == TileStatus.Ready);

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
