using Jellyfin.Plugin.Stinger.Sources;
using Xunit;

namespace Jellyfin.Plugin.Stinger.Tests;

public class WikipediaParserTests
{
    [Fact]
    public void ParsesTableRowsIntoTitlesAndYears()
    {
        const string wikitext = """
            {| class="wikitable"
            ! Film !! Year !! Description
            |-
            | ''[[Iron Man (2008 film)|Iron Man]]'' || 2008 || Nick Fury appears.
            |-
            | ''[[Airplane!]]'' || 1980 || The passenger in the taxi.
            |-
            | ''[[Avengers: Endgame]]''
            | 2019
            | Clanging sound.
            |}
            """;

        var titles = WikipediaListSource.ParseWikitext(wikitext);

        Assert.Contains(2008, titles[WikipediaListSource.Normalize("Iron Man")]);
        Assert.Contains(1980, titles[WikipediaListSource.Normalize("Airplane!")]);
        Assert.Contains(2019, titles[WikipediaListSource.Normalize("Avengers: Endgame")]);
    }

    [Fact]
    public void StripsFilmParenthetical()
    {
        var titles = WikipediaListSource.ParseWikitext("|-\n| ''[[The Thing (1982 film)]]'' || 1982 ||");

        Assert.True(titles.ContainsKey(WikipediaListSource.Normalize("The Thing")));
    }

    [Fact]
    public void NormalizeIsCaseAndPunctuationInsensitive()
    {
        Assert.Equal(
            WikipediaListSource.Normalize("Spider-Man: No Way Home"),
            WikipediaListSource.Normalize("spider man no way home"));
    }
}
