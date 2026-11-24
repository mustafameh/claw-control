using ClawControl.Core;
using ClawControl.Hardware;
using HidSharp;

var folder = Directory.CreateTempSubdirectory("ClawControlControllerQA-");
Environment.SetEnvironmentVariable("CLAWCONTROL_DATA_DIR", folder.FullName);
string file = Path.Combine(Log.Dir, "controller-restore.bin");
const int View = 165, Menu = 173, Trigger = 529;
byte[] original = new byte[ClawPad.ProfileLength];
new Random(7621).NextBytes(original); // Non-default buttons, sticks, lights and calibration.
original[View] = 14;
original[Menu] = 15;
original[Trigger] = 19;

static byte[] Panel(byte[] profile)
{
    var mapped = profile.ToArray();
    mapped[View] = mapped[Menu] = 255;
    return mapped;
}
static byte[] PanelWithTriggers(byte[] profile)
{
    var mapped = Panel(profile);
    mapped[Trigger] = 255;
    return mapped;
}
static void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    Console.WriteLine("PASS " + label);
}

HidStream.Rom = original.ToArray();
HidStream.Ram = original.ToArray();
byte[] interrupted;
using (var pad = new ClawPad())
{
    await pad.LoadAsync();
    Directory.CreateDirectory(file + ".tmp");
    await pad.SetOverlay(PanelWithTriggers);
    Check(HidStream.Ram.SequenceEqual(original), "backup creation failure leaves controller untouched");
    Directory.Delete(file + ".tmp");
    await pad.SetOverlay(PanelWithTriggers);
    Check(HidStream.Ram.SequenceEqual(PanelWithTriggers(original)), "panel open suppresses only intended inputs");
    await pad.SetOverlay(Panel);
    Check(HidStream.Ram.SequenceEqual(Panel(original)), "trigger preview restores triggers while panel stays open");
    interrupted = File.ReadAllBytes(file);
    await pad.SetOverlay(null);
    Check(HidStream.Ram.SequenceEqual(original) && !File.Exists(file), "clean close preserves full custom profile");
}
foreach (var fault in new[] { "none", "write", "lost ack", "save" })
{
    // MSI can persist a broader overlay before a later switch to trigger preview.
    HidStream.Rom = PanelWithTriggers(original);
    HidStream.Ram = HidStream.Rom.ToArray();
    File.WriteAllBytes(file, interrupted);
    HidStream.FailWrite = fault == "write";
    HidStream.FailAfterWrite = fault == "lost ack";
    HidStream.FailSave = fault == "save";
    using (var pad = new ClawPad())
    {
        await pad.ReadModeAsync(); // join work queued by startup recovery
        if (fault == "none")
            Check(!File.Exists(file) && HidStream.Rom.SequenceEqual(original), "uninterrupted startup recovery");
        else
        {
            await pad.SetOverlay(Panel);
            Check(File.ReadAllBytes(file).SequenceEqual(interrupted), fault + " retains backup even if panel reopens");
        }
    }
    HidStream.FailWrite = HidStream.FailAfterWrite = HidStream.FailSave = false;
    HidStream.Ram = HidStream.Rom.ToArray();
    HidStream.Ram[810] ^= 4; // unrelated setting changed outside the app
    var expected = original.ToArray();
    expected[810] ^= 4;
    using var retry = new ClawPad();
    await retry.LoadAsync();
    Check(!File.Exists(file) && HidStream.Ram.SequenceEqual(expected), fault + " preserves all unrelated bytes on restart");
    if (fault != "none") Check(HidStream.Rom.SequenceEqual(expected), fault + " retry saves recovered profile");
}

HidStream.Ram = original.ToArray();
using (var pad = new ClawPad())
{
    await pad.LoadAsync();
    await pad.SetOverlay(Panel);
    pad.Patch(View, 12);
    await pad.ReadModeAsync();
    HidStream.FailSave = true;
    // Change a pass-through byte as well to queue a durable save when closing.
    pad.Patch(810, 42);
    await pad.ReadModeAsync();
    await pad.SetOverlay(null);
    Check(File.Exists(file), "failed save on normal panel close retains recovery");
    HidStream.FailSave = false;
    await pad.LoadAsync();
    var expected = original.ToArray();
    expected[View] = 12;
    expected[810] = 42;
    Check(!File.Exists(file) && HidStream.Rom.SequenceEqual(expected), "in-session retry preserves user edits");
}

HidStream.Ram = original.ToArray();
HidStream.Rom = original.ToArray();
using (var pad = new ClawPad())
{
    await pad.LoadAsync();
    await pad.SetOverlay(Panel);
    pad.Patch(Menu, 11);
    await pad.ReadModeAsync();
    HidStream.FailSave = true;
    await pad.SetOverlay(null);
    Check(File.Exists(file), "editing only a suppressed button still requires a confirmed save");
}
HidStream.FailSave = false;
HidStream.Ram = HidStream.Rom.ToArray();
using (var pad = new ClawPad())
{
    await pad.LoadAsync();
    var expected = original.ToArray();
    expected[Menu] = 11;
    Check(!File.Exists(file) && HidStream.Rom.SequenceEqual(expected), "restart restores the user's edited mapping");
}

File.WriteAllBytes(file, [0, 1, 0, 1, 42, 0]);
HidStream.Ram = original.ToArray();
using (var pad = new ClawPad())
{
    await pad.SetOverlay(Panel);
    Check(!await pad.LoadAsync() && HidStream.Ram.SequenceEqual(original) && File.Exists(file),
        "truncated legacy record cannot partially restore or be overwritten");
}
File.Delete(file);

var native = original.ToArray();
// Build the documented native button layout independently of the repair implementation.
foreach (int code in Enumerable.Range(1, 12).Concat(new[] { 14, 15, 19, 20 }))
{
    bool trigger = code >= 19;
    bool passThrough = code is 14 or 15;
    int block = trigger ? (code == 19 ? 524 : 539) : 58 + 8 * (code - 1);
    int slot = block + (trigger ? 5 : 3);
    native[block] = passThrough ? (byte)0 : (byte)1;
    native[block + 1] &= 0xFA;
    Array.Clear(native, slot, 5);
    native[slot] = passThrough ? (byte)0 : (byte)code;
}
var stuck = native.ToArray();
foreach (var button in PanelButton.All) button.ApplyPanel(stuck);
foreach (bool damaged in new[] { false, true })
{
    if (damaged) File.WriteAllBytes(file, [1, 2, 3]);
    HidStream.Ram = stuck.ToArray();
    HidStream.Rom = stuck.ToArray();
    using var pad = new ClawPad();
    var found = await pad.ScanPanelMappingsAsync();
    Check(found.Length == 16, "scan finds sixteen panel mappings");
    var restored = await pad.RepairPanelMappingsAsync(found);
    Check(restored.SequenceEqual(found) && HidStream.Rom.SequenceEqual(native), $"repair preserves full profile with {(damaged ? "damaged" : "missing")} backup");
    Check((await pad.ScanPanelMappingsAsync()).Length == 0 && (await pad.RepairPanelMappingsAsync(found)).Length == 0,
        "second repair is a no-op");
}
HidStream.Ram = stuck.ToArray();
using (var pad = new ClawPad())
{
    var found = await pad.ScanPanelMappingsAsync();
    var view = found.Single(b => b.Native == 14);
    var menu = found.Single(b => b.Native == 15);
    HidStream.Ram[Menu] = 9; // remapped outside Claw Control following the scan
    var expected = HidStream.Ram.ToArray();
    Array.Copy(native, 162, expected, 162, 8);
    Check((await pad.RepairPanelMappingsAsync([view, menu])).SequenceEqual([view]) && HidStream.Rom.SequenceEqual(expected),
        "only selected buttons still matching at confirmation are restored");
}
HidStream.Ram = native.ToArray();
using (var pad = new ClawPad())
{
    await pad.LoadAsync();
    await pad.SetOverlay(profile =>
    {
        var mapped = profile.ToArray();
        foreach (var button in PanelButton.All) button.ApplyPanel(mapped);
        return mapped;
    });
    Check((await pad.ScanPanelMappingsAsync()).Length == 0, "scan ignores the normal open panel");
}
HidStream.Ram = stuck.ToArray();
HidStream.Rom = stuck.ToArray();
using (var pad = new ClawPad())
{
    var found = await pad.ScanPanelMappingsAsync();
    HidStream.IgnoreWrite = true;
    bool refused = false;
    try { await pad.RepairPanelMappingsAsync(found); }
    catch (IOException) { refused = true; }
    Check(refused && File.Exists(file) && HidStream.Rom.SequenceEqual(stuck),
        "read-back catches an acknowledged write that was ignored");
    Check(!await pad.LoadAsync() && File.Exists(file), "automatic retry also requires matching read-back");
    HidStream.IgnoreWrite = false;
    Check(await pad.LoadAsync() && HidStream.Rom.SequenceEqual(native) && !File.Exists(file),
        "interrupted manual repair completes on retry");
}
HidStream.Ram = native.ToArray();
HidStream.Ram[163] |= 5;
HidStream.Ram[165] = 9;
HidStream.Ram[166] = 10;
HidStream.Ram[173] = 11; // unselected custom mapping
using (var pad = new ClawPad())
{
    var view = PanelButton.All.Single(b => b.Native == 14);
    Check((await pad.ScanPanelMappingsAsync()).Length == 0, "manual restore fixture has no panel matches");
    var expected = HidStream.Ram.ToArray();
    Array.Copy(native, 162, expected, 162, 8);
    Check((await pad.RepairPanelMappingsAsync([view], manual: true)).SequenceEqual([view]) && HidStream.Rom.SequenceEqual(expected),
        "manual restore clears only the selected custom mapping and turbo");
    Check((await pad.RepairPanelMappingsAsync([view], manual: true)).Length == 0, "manual restore skips a default mapping");
}
folder.Delete(recursive: true);
