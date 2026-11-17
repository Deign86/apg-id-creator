// The shared register's address is a credential. These keep it out of the
// source: the release build supplies it (register/README.md).

using System.Text.RegularExpressions;
using IdMaker.Core;

namespace IdMaker.Tests;

public partial class SecretTests
{
    // A real Apps Script deployment id is long; the docs' "AKfy…" example is not.
    [GeneratedRegex(@"script\.google\.com/macros/s/[A-Za-z0-9_-]{20,}")] private static partial Regex Deployment();

    private static readonly string[] Skip = { ".git", "bin", "obj", "node_modules", "dist" };

    [Fact]
    public void No_register_address_is_written_in_the_repository()
    {
        var hits = new List<string>();
        var stack = new Stack<DirectoryInfo>(new[] { new DirectoryInfo(Repo.Root) });
        var looked = 0;
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in dir.EnumerateDirectories())
                if (!Skip.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
            foreach (var file in dir.EnumerateFiles())
            {
                if (file.Extension is not (".cs" or ".xaml" or ".md" or ".json" or ".yml" or ".ps1" or ".psd1" or ".iss" or ".gs" or ".csproj" or ".txt")) continue;
                looked++;
                if (Deployment().IsMatch(File.ReadAllText(file.FullName)))
                    hits.Add(Path.GetRelativePath(Repo.Root, file.FullName));
            }
        }
        Assert.True(looked > 50, $"only {looked} files were read; the scan is not looking at the repository");
        Assert.True(hits.Count == 0, "a register address is written in: " + string.Join(", ", hits));
    }

    [Fact]
    public void A_build_without_the_secret_has_no_built_in_register()
    {
        // CI for pull requests and developers' builds never get REGISTER_URL
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDMAKER_REGISTER_URL"))) return;
        Assert.Equal("", CardConfig.RegisterUrl);
        Assert.False(new Register(new MemoryStore()).Shared);
    }
}
