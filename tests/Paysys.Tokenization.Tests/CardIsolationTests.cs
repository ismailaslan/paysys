using System.Text.RegularExpressions;
using System.Xml.Linq;
using Paysys.DAL.Entities;
using Xunit;

namespace Paysys.Tokenization.Tests;

// Principle P2, enforced by CI: card data stays inside Paysys.Tokenization.
// These read the source tree, so a change that lets the card number leak into another
// project fails the build, instead of relying on a description in a document.
public class CardIsolationTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Paysys.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Paysys.slnx not found above the test output folder.");
    }

    private static IEnumerable<string> SourceFiles(params string[] extensions)
    {
        var root = RepoRoot();
        foreach (var folder in new[] { "src", "gateway" })
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
                continue;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.Contains("/Migrations/"))
                    continue;
                if (extensions.Contains(Path.GetExtension(file)))
                    yield return relative;
            }
        }
    }

    // Every file that names a card number. The request that carries it in, the one-time demo
    // card response, the tokenizer that validates it, the endpoint that hands it over, and the
    // form that sends it. Anything else showing up here is a new place the number can go.
    private static readonly string[] FilesAllowedToHandleTheNumber =
    [
        "src/Paysys.Api/Endpoints/TokenizationEndpoints.cs",
        "src/Paysys.Shared/Tokenization/IssueCardResponse.cs",
        "src/Paysys.Shared/Tokenization/TokenizeCardRequest.cs",
        "src/Paysys.Tokenization/CardTokenizationService.cs",
        "src/Paysys.Web/Pages/Cards.razor",
    ];

    [Fact]
    public void OnlyTheExpectedFiles_HandleTheCardNumber()
    {
        var root = RepoRoot();
        var namingTheNumber = SourceFiles(".cs", ".razor")
            .Where(file => Regex.IsMatch(File.ReadAllText(Path.Combine(root, file)), "cardnumber", RegexOptions.IgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(FilesAllowedToHandleTheNumber, namingTheNumber);
    }

    [Fact]
    public void OnlyTheApi_ReferencesTheTokenizationProject()
    {
        var root = RepoRoot();
        var referencing = SourceFiles(".csproj")
            .Where(file => XDocument.Load(Path.Combine(root, file)).Descendants("ProjectReference")
                .Any(reference => ((string?)reference.Attribute("Include") ?? "").Contains("Paysys.Tokenization")))
            .ToList();

        Assert.Equal(["src/Paysys.Api/Paysys.Api.csproj"], referencing);
    }

    [Fact]
    public void TokenizationProject_DoesNotReferenceTheBllOrTheApi()
    {
        var root = RepoRoot();
        var references = XDocument.Load(Path.Combine(root, "src/Paysys.Tokenization/Paysys.Tokenization.csproj"))
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .ToList();

        Assert.DoesNotContain(references, r => r.Contains("Paysys.BLL") || r.Contains("Paysys.Api") || r.Contains("Paysys.Web"));
    }

    [Fact]
    public void TheCardEntity_HasNoFieldForTheNumberOrTheCvv()
    {
        var fields = typeof(CardToken).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.Contains("lastfourdigits", fields);
        Assert.DoesNotContain(fields, f => f.Contains("cardnumber") || f.Contains("cvv") || f.Contains("cvc") || f is "number" or "pan");
    }
}
