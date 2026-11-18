using IdMaker.Core;

namespace IdMaker.Mac;

/// <summary>The checks that need a real Mac: run with `--selftest`.</summary>
internal static class SelfTest
{
    public static int Run()
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

        Console.WriteLine(failed == 0 ? "selftest passed" : $"selftest FAILED: {failed}");
        return failed == 0 ? 0 : 1;
    }
}
