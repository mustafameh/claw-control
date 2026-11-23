using ClawControl.Core;
using ClawControl.Features.Library;
using ClawControl.Features.OptiScaler;
using System.IO;
using System.Security.Cryptography;

var root = Directory.CreateTempSubdirectory("ClawControlQA-").FullName;
Environment.SetEnvironmentVariable("CLAWCONTROL_DATA_DIR", Path.Combine(root, "settings"));
var exe = Path.Combine(root,"Game.exe");
File.Copy(Path.Combine(Environment.SystemDirectory,"whoami.exe"),exe);
File.WriteAllText(Path.Combine(root,"nvngx_dlss.dll"),"dummy game file");
File.WriteAllText(Path.Combine(root,"amd_fidelityfx_dx12.dll"),"original game library");
Dictionary<string,string> Snapshot() => Directory.GetFiles(root,"*",SearchOption.AllDirectories)
    .Where(p=> !Path.GetRelativePath(root,p).StartsWith("settings\\")).ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
var original = Snapshot();
var settings = new Settings();
var library = new LibraryService(settings);
var svc = new OptiScalerService(settings,library);
var target = new GameTarget("QA",exe,null);
var scan = await svc.ScanAsync(target) ?? throw new Exception("scan");
static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
var progress = new Progress<string>();
Check(!svc.Enabled && await svc.InstallAsync(target,scan,new(),progress) != null,"upgrade default off");
svc.SetEnabled(true);
Check(await svc.InstallAsync(target,scan,new(){Upscaler="fsr4"},progress) != null,"FSR 4 blocked");
var result=await svc.InstallAsync(target,scan,new(),progress);
Check(result==null,"verified install " + result);
var install = svc.InstallFor(target)!;
Check(File.ReadAllText(Path.Combine(root,"amd_fidelityfx_dx12.dll"))=="original game library","game libraries preserved");
Check(await svc.ToggleLoaderAsync(install)==null && svc.IsDisabled(install),"disable loader");
svc.SetEnabled(false);
Check(await svc.ToggleLoaderAsync(install)!=null,"opt-in required to re-enable");
svc = new OptiScalerService(new Settings(),library);
install = svc.InstallFor(target)!;
Check(svc.IsDisabled(install!),"recovery after restart with settings missing");
svc.SetEnabled(true);
Check(await svc.ToggleLoaderAsync(install!)==null,"enable loader");
Check(await svc.ApplyAsync(install!,new(){Upscaler="xess"})==null &&
 File.ReadAllText(Path.Combine(root,"OptiScaler.ini")).Contains("Dx12Upscaler=xess"),"saved and verified");
var helper=Path.Combine(root,"fakenvapi.dll");
var bytes=File.ReadAllBytes(helper);
File.WriteAllText(helper,"new game content");
svc.SetEnabled(false);
Check((await svc.RemoveAsync(install!))?.Contains("preserved")==true && File.ReadAllText(helper)=="new game content","off-state removal preserves replacement");
File.WriteAllBytes(helper,bytes);
Check(await svc.RemoveAsync(install!)==null && svc.InstallFor(target)==null,"retry removes remaining owned files");
var after=Snapshot();
Check(after.Count==original.Count && original.All(pair=>after.GetValueOrDefault(pair.Key)==pair.Value),"byte-exact restoration");
Console.WriteLine("Disposable game directory: " + root);

// Recreate the old preview layout in a disposable folder with the actual cached release.
var cached = Path.Combine(OptiScalerService.PackageDir,"0.9.5");
const string marker = "OptiScaler - added by Claw Control.txt";
const string helperDir = "D3D12_Optiscaler";
string nestedHelper = Path.Combine(helperDir,"D3D12Core.dll");
var legacy = new OptiScalerInstall { Exe=exe, Name="Legacy QA", Version="0.9.5", Proxy="d3d12.dll",
    Upscaler="fsr4", Files=["d3d12.dll","OptiScaler.ini",helperDir + Path.DirectorySeparatorChar,nestedHelper,marker] };
Directory.CreateDirectory(Path.Combine(root,helperDir));
File.Copy(Path.Combine(cached,nestedHelper),Path.Combine(root,nestedHelper));
File.Copy(Path.Combine(cached,"OptiScaler.dll"),Path.Combine(root,legacy.Proxy));
File.Copy(Path.Combine(cached,"OptiScaler.ini"),Path.Combine(root,"OptiScaler.ini"));
File.WriteAllLines(Path.Combine(root,marker), new[]{"OptiScaler 0.9.5 (github.com/optiscaler/OptiScaler) was added to this game by Claw Control.",
    "Remove it from Claw Control's Graphics tab (OptiScaler), or delete these files:", ""}.Concat(legacy.Files));
settings = new Settings { OptiScalerGames=[legacy] };
svc = new OptiScalerService(settings,new LibraryService(settings));
Check(!svc.Enabled && svc.InstallFor(target)==legacy,"older preview visible with beta off");
result = await svc.ToggleLoaderAsync(legacy);
Check(result==null && !File.Exists(Path.Combine(root,legacy.Proxy)) && svc.IsDisabled(legacy),"disable legacy loader with beta off: " + result);
svc.SetEnabled(true);
Check(await svc.ApplyAsync(legacy,new(){Upscaler="fsr31"})==null && File.ReadAllText(Path.Combine(root,"OptiScaler.ini")).Contains("Fsr4Update=false"),
    "legacy profile migration writes supported config");
Check(await svc.RemoveAsync(legacy)==null && settings.OptiScalerGames.Count==0,"legacy receipt cleanup");
Check(original.All(pair=>Snapshot().GetValueOrDefault(pair.Key)==pair.Value),"original files after migration");
await UiChecks.Run(root,target);

const string epic = "[tester_Settings]\r\nns:id:app_AdditionalCommandsEnabled=True\r\nns:id:app_AdditionalCommands=-example \"a  b\"\r\n";
var adjusted = OptiScalerService.WithEpicOption(epic,"ns:id:app","-use-d3d12",true)!;
Check(adjusted.Contains("-example \"a  b\" -use-d3d12"), "Epic arguments retain quoted spacing");
Check(OptiScalerService.WithEpicOption(adjusted,"ns:id:app","-use-d3d12",false)==epic, "Epic argument round trip");
if (Path.GetDirectoryName(root)?.TrimEnd('\\') == Path.GetTempPath().TrimEnd('\\') && Path.GetFileName(root).StartsWith("ClawControlQA-"))
    Directory.Delete(root,recursive:true);
