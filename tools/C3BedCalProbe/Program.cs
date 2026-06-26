using MassiveSlicer.Core.C3Bridge;

const string host = "192.168.0.153";
string[] paths =
[
    "/R1/Program/BED_SCAN_CAL",
    "/R1/PROGRAM/BED_SCAN_CAL",
    "/R1/BED_SCAN_CAL",
    "BED_SCAN_CAL",
];

using var client = new C3BridgeClient();
await client.ConnectAsync(host, 7000);
Console.WriteLine($"Connected to {host}:7000");

// Prove var R/W works (stock KUKAVARPROXY supports this).
foreach (var v in new[] { "$MODE_OP", "$PRO_STATE", "$STOPMESS" })
{
    var val = await client.ReadAsync(v, 2000);
    Console.WriteLine($"{v} = {val.Trim()}");
}

foreach (var path in paths)
{
    var sel = await client.SelectProgramAsync(path);
    Console.WriteLine($"Select {path}: success={sel.Success} err={sel.ErrorCode}");
    if (!sel.Success) continue;

    var run = await client.RunProgramAsync(path);
    Console.WriteLine($"Run    {path}: success={run.Success} err={run.ErrorCode}");
    if (run.Success) break;

    var start = await client.ProgramControlAsync(2);
    Console.WriteLine($"Start  {path}: success={start.Success} err={start.ErrorCode}");
    if (start.Success) break;

    await client.ProgramControlAsync(3); // stop before next path
}

await client.ProgramControlAsync(3);
Console.WriteLine("Sent Stop (cleanup).");