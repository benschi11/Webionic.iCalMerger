using System.Text.RegularExpressions;

namespace Webionic.ICalMerger.Tests.Ui;

public sealed class ComponentClassTests
{
    // Tailwind-Utilities, deren Namen als Komponentenklasse mit der Utility kollidieren würden (Utilities liegen im Layer danach).
    private static readonly string[] UtilityNames =
    [
        "inline", "block", "inline-block", "inline-flex", "flex", "inline-grid", "grid", "hidden", "table", "contents",
        "container", "static", "fixed", "absolute", "relative", "sticky", "truncate", "italic", "underline", "border",
        "shadow", "outline", "ring", "transition", "transform", "visible", "invisible", "collapse", "isolate",
    ];

    private static string StylesPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "Webionic.ICalMerger", "Styles", "app.css");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Styles/app.css nicht gefunden");
    }

    [Fact]
    public void ComponentClasses_DoNotCollideWithTailwindUtilities()
    {
        var css = File.ReadAllText(StylesPath());
        var start = css.IndexOf("@layer components", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var defined = Regex.Matches(css[start..], @"(?<![\w-])\.(-?[A-Za-z_][\w-]*)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        Assert.Empty(defined.Intersect(UtilityNames));
    }
}
