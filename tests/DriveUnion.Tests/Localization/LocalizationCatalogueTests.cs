using System.Reflection;
using DriveUnion.Web.Localization;
using FluentAssertions;

namespace DriveUnion.Tests.Localization;

/// <summary>
/// Every entry in <see cref="UiText"/>, exercised in both languages.
///
/// This is the test that stands in for what a <c>.resx</c> mechanism cannot do at all: there, a key
/// present in one culture and missing from the other returns the key's own name and the panel ships
/// <c>Nav.Files</c> to a customer, silently, forever. Here an entry supplies both languages on one
/// line — so "missing" is not expressible — and what is left to catch is the copy-paste that gave
/// both languages the same words, or gave one of them none.
///
/// Every entry is reached by reflection rather than listed, because a list is a second place to
/// forget something. An entry with parameters is invoked with sample values, so a string with a
/// placeholder is covered on exactly the same terms as one without.
/// </summary>
public class LocalizationCatalogueTests
{
    /// <summary>What a parameterised entry is called with. The values only have to be printable.</summary>
    private const int SampleNumber = 7;

    private const string SampleText = "Q3-Report-Final.pdf";

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();

        foreach (var entry in AllEntries()) data.Add(Name(entry));

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_says_something_in_both_languages(string name)
    {
        var entry = Find(name);

        using (CultureScope.Persian())
        {
            Render(entry).Should().NotBeNullOrWhiteSpace($"«{name}» has to say something in Persian");
        }

        using (CultureScope.English())
        {
            Render(entry).Should().NotBeNullOrWhiteSpace($"«{name}» has to say something in English");
        }
    }

    /// <summary>
    /// The forgotten half. An entry whose two languages are identical is almost always a pair where
    /// the second was pasted from the first and never written — and the exemption has to be argued
    /// in the source with a reason, not reached for here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_entry_is_translated_unless_it_says_why_not(string name)
    {
        var entry = Find(name);

        string persian;
        string english;

        using (CultureScope.Persian()) persian = Render(entry);
        using (CultureScope.English()) english = Render(entry);

        if (entry.GetCustomAttribute<VerbatimTextAttribute>() is { } verbatim)
        {
            verbatim.Because.Should().NotBeNullOrWhiteSpace(
                $"«{name}» is exempt from translation and the reason is the whole of the exemption");

            return;
        }

        english.Should().NotBe(
            persian,
            $"«{name}» renders the same words in both languages — either it was never translated, or "
            + "it is deliberately verbatim and wants [VerbatimText] with a reason");
    }

    /// <summary>
    /// The catalogue is not empty and reflection really is reaching it. Without this, a rename that
    /// moved every entry out of <see cref="UiText"/> would leave both theories above passing with
    /// nothing to run.
    /// </summary>
    [Fact]
    public void The_catalogue_is_reachable_and_is_not_empty() =>
        AllEntries().Count.Should().BeGreaterThan(40);

    private static List<MemberInfo> AllEntries()
    {
        var members = new List<MemberInfo>();

        foreach (var section in typeof(UiText).GetNestedTypes(BindingFlags.Public))
        {
            members.AddRange(section
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(string) && p.GetMethod is not null));

            members.AddRange(section
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName));
        }

        return members;
    }

    private static string Name(MemberInfo member) => $"{member.DeclaringType!.Name}.{member.Name}";

    private static MemberInfo Find(string name) =>
        AllEntries().Single(m => Name(m) == name);

    /// <summary>
    /// Invokes an entry in whatever culture is current.
    ///
    /// A method's arguments are supplied by type, which is the reason the catalogue's parameterised
    /// entries take nothing more exotic than a number or a string: an entry this cannot call is an
    /// entry nothing checks, so it fails rather than skips.
    /// </summary>
    private static string Render(MemberInfo member) => member switch
    {
        PropertyInfo property => (string)property.GetValue(null)!,
        MethodInfo method => (string)method.Invoke(null, [.. method.GetParameters().Select(Argument)])!,
        _ => throw new InvalidOperationException($"{Name(member)} is not a catalogue entry."),
    };

    private static object Argument(ParameterInfo parameter) => parameter.ParameterType switch
    {
        var t when t == typeof(int) => SampleNumber,
        var t when t == typeof(long) => (long)SampleNumber,
        var t when t == typeof(string) => SampleText,
        var t => throw new InvalidOperationException(
            $"A catalogue entry takes a {t.Name}, which nothing here can supply — so nothing checks that "
            + "entry in either language. Give it a number or a string, or split it into entries that take one."),
    };
}
