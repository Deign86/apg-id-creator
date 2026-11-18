using IdMaker.Mac;

// `--selftest` runs the checks that need a real Mac and no person, prints
// what it found and exits: 0 when all hold. CI runs it on every change.
NSApplication.Init();
if (args.Contains("--selftest")) return SelfTest.Run();

NSApplication.SharedApplication.Delegate = new AppDelegate();
NSApplication.Main(args);
return 0;
