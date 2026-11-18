using System.Text.Json;
using IdMaker.Core;

namespace IdMaker.Mac;

/// <summary>The checks that need a real Mac: run with `--selftest`.</summary>
internal static class SelfTest
{
    public static int Run(string? repo)
    {
        var failed = 0;
        void Check(string what, bool holds)
        {
            Console.WriteLine($"{(holds ? "ok  " : "FAIL")} {what}");
            if (!holds) failed++;
        }

        Check($"shares the Windows app's rules (version {CardConfig.AppVersion})", CardConfig.AppVersion.Length > 0);

        MacKeeper.Forget();
        var keeper = new MacKeeper();
        var wrapped = keeper.Protect("a sign-in token");
        Check("the Keychain keeper wraps a sign-in", wrapped is not null && !wrapped.Contains("a sign-in token"));
        Check("and opens it again", wrapped is not null && keeper.Unprotect(wrapped) == "a sign-in token");
        Check("a damaged one does not open", wrapped is not null && keeper.Unprotect(string.Concat(wrapped.AsSpan(0, wrapped.Length - 4), "AAA=")) is null);
        Check("nor does something that was never wrapped", keeper.Unprotect("not wrapped") is null);
        MacKeeper.Forget();
        Check("with the key gone it no longer opens", wrapped is not null && keeper.Unprotect(wrapped) is null);

        if (repo is null)
        {
            Check("the card checks were given the repository (--repo <path>)", false);
        }
        else
        {
            var problems = FontsAgainstTheWebApp(repo, out var worst);
            foreach (var problem in problems) Check(problem, false);
            Check($"the card fonts measure as they did in the web app (worst width difference at 100 px: {worst:0.0000})", worst < 0.05);
        }

        Console.WriteLine(failed == 0 ? "selftest passed" : $"selftest FAILED: {failed}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The same check the Windows app's RenderTests make: cap height and
    /// descents equal to the web app's, every measured width within a
    /// twentieth of a pixel at 100 px.
    /// </summary>
    private static List<string> FontsAgainstTheWebApp(string repo, out double worst)
    {
        var wrong = new List<string>();
        worst = 0;
        var measurer = MacTextMeasurer.Load(Path.Combine(repo, "windows", "IdMaker.App", "Fonts"));
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "windows", "IdMaker.Tests", "Fixtures", "web-reference.json")));
        foreach (var (font, key) in new[] { (CardFont.Name, "name"), (CardFont.Title, "title"), (CardFont.Number, "employeeNo") })
        {
            var web = reference.RootElement.GetProperty("metrics").GetProperty(key);
            void Height(string what, double want, double got)
            {
                if (want != got) wrong.Add($"{key} {what}: web {want:0.000}, here {got:0.000}");
            }
            Height("cap height", web.GetProperty("capH").GetDouble(), measurer.CapHeight(font, 100));
            Height("descent of Q,", web.GetProperty("descQ").GetDouble(), measurer.Descent(font, 100, uppercase: true));
            Height("descent of gjpqy", web.GetProperty("descg").GetDouble(), measurer.Descent(font, 100, uppercase: false));
            foreach (var width in web.GetProperty("widths").EnumerateObject())
            {
                var got = measurer.Width(font, 100, width.Name);
                var off = Math.Abs(got - width.Value.GetDouble());
                worst = Math.Max(worst, off);
                if (off >= 0.05) wrong.Add($"{key} \"{width.Name}\": web {width.Value.GetDouble():0.000}, here {got:0.000}");
            }
        }
        return wrong;
    }
}
