using IdMaker.Mac;

// `--selftest` runs the checks that need a real Mac and no person, prints
// what it found and exits: 0 when all hold. CI runs it on every change.
NSApplication.Init();
if (args.Contains("--selftest"))
{
    var at = Array.IndexOf(args, "--repo");
    return SelfTest.Run(at >= 0 && at + 1 < args.Length ? args[at + 1] : null);
}

NSApplication.SharedApplication.Delegate = new AppDelegate();
NSApplication.Main(args);
return 0;
