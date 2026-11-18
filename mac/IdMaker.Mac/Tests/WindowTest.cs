// The window tests: the real window, opened on a real Mac, doing what an
// operator does. One test per thing an operator does, as on Windows
// (IdMaker.App.Tests). The register behind it is the pretend project
// (FakeFirebase), so nothing here can reach the real one.
//
// Compiled only with -p:WindowTest=true; run as `AlphaPremierIdMaker
// --windowtest --repo <path>`. Prints what it found and exits 0 when all
// of it holds.

using IdMaker.Core;
using IdMaker.Tests;

namespace IdMaker.Mac;

[Register("WindowTestDelegate")]
public sealed class WindowTestDelegate : NSApplicationDelegate
{
    private readonly string? _repo;

    public WindowTestDelegate(string? repo) { _repo = repo; }

    public override void DidFinishLaunching(NSNotification notification) => _ = WindowTest.Run(_repo);
}

internal static class WindowTest
{
    private static int _failed;
    private static string? _report;

    private static void Check(string what, bool holds)
    {
        Console.WriteLine($"{(holds ? "ok    " : "FAILED")}  {what}");
        if (!holds) _failed++;
    }

    public static async Task Run(string? repo)
    {
        // A window waiting on a question nobody is there to answer must not hold the run until CI gives up.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(4));
            Console.WriteLine("FAILED  the window tests did not finish in four minutes");
            Console.Out.Flush();
            Environment.Exit(2);
        });

        _report = Environment.GetEnvironmentVariable("IDMAKER_REPORT_DIR");
        if (_report is { Length: > 0 }) Directory.CreateDirectory(_report);
        try
        {
            if (repo is null) Check("the window tests were given the repository (--repo <path>)", false);
            else
            {
                await A_card_can_be_made_from_start_to_saved_pdf(repo);
                await Signing_in_under_issued_ids_connects_this_computer_to_the_shared_register();
                await A_card_the_register_refused_is_put_in_front_of_the_operator_until_it_is_dealt_with();
                await The_background_remover_runs_on_this_mac();
            }
        }
        // Whatever a test throws is a failed test, reported with the rest; this is the one place that has to hear all of it.
        catch (Exception e)
        {
            Check($"no test threw ({e.GetType().Name}: {e.Message})", false);
            Console.WriteLine(e.StackTrace);
        }
        Console.WriteLine(_failed == 0 ? "The window tests passed." : $"{_failed} window check(s) failed.");
        Console.Out.Flush();
        Environment.Exit(_failed == 0 ? 0 : 1);
    }

    /* ---- helpers --------------------------------------------------- */

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "idmaker-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // Press a control the way AppKit does when it is clicked.
    private static void Press(NSControl control)
    {
        var action = control.Action;
        if (action is null || !control.Enabled || control.Hidden || !control.SendAction(action, control.Target))
        {
            Check($"“{(control as NSButton)?.Title ?? control.AccessibilityLabel}” could be pressed", false);
        }
    }

    private static void TypeName(MainWindow window, Field part, string text)
    {
        var box = part == Field.Last ? window.LastBox : window.FirstBox;
        box.StringValue = text;
        window.OnNameChanged(part);
    }

    private static async Task<bool> Until(Func<bool> holds)
    {
        for (var i = 0; i < 200; i++)
        {
            if (holds()) return true;
            await Task.Delay(25);
        }
        return holds();
    }

    private static void Shoot(MainWindow window, string name)
    {
        if (_report is not { Length: > 0 }) return;
        // SAFETY: a window made with a content rectangle always has a content view.
        Ui.Snapshot(window.Window.ContentView!, Path.Combine(_report, name + ".png"));
    }

    private static MainWindow Open(string folder, Register register)
    {
        var window = new MainWindow(new FileStore(folder), register)
        {
            OpenSigningOnArrival = false,
            Confirm = (_, _) => true,
            AskForSignature = (_, _) => Session.ExampleSignature(),
            AskForFile = _ => null,
            AskWhereToSave = (_, _) => null,
        };
        window.Window.MakeKeyAndOrderFront(null);
        return window;
    }

    /* ---- the tests ------------------------------------------------- */

    private static async Task A_card_can_be_made_from_start_to_saved_pdf(string repo)
    {
        Console.WriteLine("A card can be made from start to a saved PDF");
        var folder = TempFolder();
        var files = new FileStore(folder);
        var window = Open(folder, new Register(files) { Project = null });
        var s = window.Session;

        Check("the card and its fonts loaded", window.NextButton.Enabled);

        // an empty form does not move on, and says why
        Press(window.NextButton);
        Check("Next on an empty name stays on the name", s.StepIndex == (int)Step.Name);
        Check($"and says what is missing (“{window.BlockedLine}”)", window.BlockedLine.Length > 0);
        Shoot(window, "1-name-refused");

        TypeName(window, Field.Last, "Reyes");
        TypeName(window, Field.First, "Ana");
        Press(window.NextButton);
        Check("with a name, Next opens the job title", s.StepIndex == (int)Step.Title);

        // the Intern / Employee choice has to be made
        window.TitleBox.SelectItem(1);
        Press(window.TitleBox);
        Press(window.NextButton);
        Check("without saying who the ID is for, it stays", s.StepIndex == (int)Step.Title);
        window.KindBox.SelectItem(2);
        Press(window.KindBox);
        Check("choosing “An intern” is taken", s.Kind == PersonKind.Intern);
        Press(window.NextButton);
        Check("then Next opens the number", s.StepIndex == (int)Step.Number);

        window.SeqBox.StringValue = "7";
        window.OnSeqChanged();
        await s.DuplicateCheck;
        Press(window.NextButton);
        Check("a free number opens the photo", s.StepIndex == (int)Step.Photo);

        await window.AcceptPhotoFile(Path.Combine(repo, "assets", "example-cut-out.png"));
        Check($"the example cut-out is accepted{(s.PhotoError.Length > 0 ? $" ({s.PhotoError})" : "")}", s.Photo is not null);
        Press(window.NextButton);
        Check("then Next opens the signature", s.StepIndex == (int)Step.Sign);

        Press(window.NextButton);
        Check("unsigned, it stays on the signature", s.StepIndex == (int)Step.Sign);
        Press(window.SignButton);
        Check("signing puts the signature on the card", s.Signature is { HasInk: true });
        Shoot(window, "2-signed");
        Press(window.NextButton);
        Check("then Next opens the check", s.StepIndex == Session.LastStep);
        Shoot(window, "3-check");

        // a print that cannot be saved issues nothing
        await window.SavePrint(Path.Combine(folder, "no-such-folder", "card.pdf"));
        Check($"a save that fails says so (“{window.ReviewErrorLine}”)", window.ReviewErrorLine.Length > 0);
        Check("and issues nothing", window.Register.All().Count == 0 && !window.Saved);

        var pdf = Path.Combine(folder, "card.pdf");
        await window.SavePrint(pdf);
        var head = File.Exists(pdf) ? File.ReadAllBytes(pdf) : Array.Empty<byte>();
        Check($"the PDF is saved ({head.Length:N0} bytes)", head.Length > 10_000 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F');
        Check("no half-written file is left beside it", !File.Exists(pdf + ".part"));
        var id = Naming.EmployeeId(s.Year, 7);
        var issued = window.Register.All();
        Check($"and only then is {id} issued, to an intern", issued.Count == 1 && issued[0].Id == id && issued[0].Name.Contains("Reyes") && issued[0].Kind == PersonKind.Intern);
        Check($"the window says where it went (“{window.SavedLine}”)", window.SavedLine.Contains("card.pdf"));
        Shoot(window, "4-saved");

        // starting again: the same number is now refused
        Press(window.NewIdButton);
        Check("“Start a new ID” empties the form", s.StepIndex == (int)Step.Name && s.Last.Length == 0 && s.Photo is null);
        TypeName(window, Field.Last, "Cruz");
        TypeName(window, Field.First, "Juan");
        Press(window.NextButton);
        window.KindBox.SelectItem(1);
        Press(window.KindBox);
        window.TitleBox.SelectItem(1);
        Press(window.TitleBox);
        Press(window.NextButton);
        window.SeqBox.StringValue = "7";
        window.OnSeqChanged();
        await s.DuplicateCheck;
        Press(window.NextButton);
        Check("the number just issued is refused for somebody else", s.StepIndex == (int)Step.Number);
        Check($"and the line says who has it (“{window.SeqLine}”)", window.SeqLine.Contains(id) && window.SeqLine.Contains("Reyes"));
        Shoot(window, "5-duplicate");
        window.Window.Close();
    }

    private static async Task Signing_in_under_issued_ids_connects_this_computer_to_the_shared_register()
    {
        Console.WriteLine("Signing in under Issued IDs connects this computer to the shared register");
        var folder = TempFolder();
        var firebase = new FakeFirebase();
        var window = Open(folder, new Register(new FileStore(folder), firebase.Http, new FakeKeeper()) { Project = FakeFirebase.Project });

        window.ToggleTool("register");
        Check("Issued IDs shows the sign-in form", window.SignInFormShown);

        window.SignInEmailBox.StringValue = FakeFirebase.Email;
        window.SignInPasswordBox.StringValue = "not the password";
        await window.SignIn();
        Check($"a wrong password is refused (“{window.RegisterMessage}”)", !window.Register.SignedIn && window.RegisterMessage.Length > 0 && window.SignInFormShown);
        Check("and the password box is emptied", window.SignInPasswordBox.StringValue.Length == 0);

        window.SignInPasswordBox.StringValue = FakeFirebase.Password;
        await window.SignIn();
        Check("the right one signs in", window.Register.SignedIn && !window.SignInFormShown);
        Check($"and the window says as whom (“{window.AccountLine}”)", window.AccountLine == $"Signed in as {FakeFirebase.Email}.");
        var kept = string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText));
        Check("the password is in no file this app keeps", !kept.Contains(FakeFirebase.Password));
        Shoot(window, "6-signed-in");
        window.Window.Close();
    }

    private static async Task A_card_the_register_refused_is_put_in_front_of_the_operator_until_it_is_dealt_with()
    {
        Console.WriteLine("A card the register refused is put in front of the operator until it is dealt with");
        var folder = TempFolder();
        var year = DateTime.Now.Year;
        var number = Naming.EmployeeId(year, 200);
        var firebase = new FakeFirebase();
        var keeper = new FakeKeeper();

        // yesterday: this computer, signed in but offline, issued a card that is still waiting to be written…
        var before = new Register(new FileStore(folder), firebase.Http, keeper) { Project = FakeFirebase.Project };
        Check("signed in beforehand", (await before.SignIn(FakeFirebase.Email, FakeFirebase.Password)).Ok);
        firebase.Offline = true;
        var mine = new RegisterEntry { Id = number, Year = year, Seq = 200, Name = "Ana Reyes", Title = "HR Associate", IssuedAt = "2026-11-16T08:00:00.000Z" };
        Check("a card issued offline waits", (await before.RecordShared(mine)).Outcome == SharedOutcome.Waiting);
        // …and meanwhile another computer issued the same number
        firebase.Offline = false;
        firebase.Put(new RegisterEntry { Id = number, Year = year, Seq = 200, Name = "Juan dela Cruz", Title = "Driver", IssuedAt = "2026-11-16T08:30:00.000Z" });

        // today the app opens with the register in reach: the waiting card is sent, refused, and shown without being asked for
        var window = Open(folder, new Register(new FileStore(folder), firebase.Http, keeper) { Project = FakeFirebase.Project });
        Check("the refused card is shown without being asked for", await Until(() => window.RefusedShown));
        Check($"it names who holds the number (“{window.RefusedLine}”)", window.RefusedLine.Contains($"{number} belongs to Juan dela Cruz"));
        Check("and whose card is the duplicate", window.RefusedLine.Contains("The card made here for Ana Reyes is a duplicate"));
        Check("the first card stands in the register", firebase.Get(number)?.Name == "Juan dela Cruz");
        Shoot(window, "7-refused");

        // it stays until somebody says it has been dealt with
        Press(window.RefusedDoneButton);
        Check("“These cards are dealt with” clears it", !window.RefusedShown && window.Register.Refused.Count == 0);
        window.Window.Close();
    }

    private static async Task The_background_remover_runs_on_this_mac()
    {
        Console.WriteLine("The background remover runs on this Mac");
        var folder = TempFolder();
        var window = Open(folder, new Register(new FileStore(folder)) { Project = null });
        var ready = await Until(() => window.RemoverReady || window.RemoverProblem.Length > 0);
        Check($"the model loads from inside the app{(window.RemoverProblem.Length > 0 ? $" ({window.RemoverProblem})" : "")}", ready && window.RemoverReady);
        if (window.Remover is { } remover)
        {
            // Not a photograph: a plain picture, to see the model answer at the picture's own size.
            var picture = new RgbaImage(480, 640);
            for (var i = 0; i < picture.Data.Length; i += 4)
            {
                picture.Data[i] = 90; picture.Data[i + 1] = 140; picture.Data[i + 2] = 200; picture.Data[i + 3] = 255;
            }
            var matte = await Task.Run(() => remover.Matte(picture));
            Check("and answers with one value per pixel", matte is not null && matte.Length == 480 * 640);
        }
        window.Window.Close();
    }
}
