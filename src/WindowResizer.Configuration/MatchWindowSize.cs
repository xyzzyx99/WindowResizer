using System.Collections.Generic;

namespace WindowResizer.Configuration;

public class MatchWindowSize
{
    public WindowSize? FullMatch { get; set; }

    public WindowSize? PrefixMatch { get; set; }

    public WindowSize? SuffixMatch { get; set; }

    public WindowSize? WildcardMatch { get; set; }

    public WindowSize? BestMatch => FullMatch ?? PrefixMatch ?? SuffixMatch ?? WildcardMatch;

    public bool NoMatch => BestMatch == null;

    public List<WindowSize?> All => new()
    {
        FullMatch, PrefixMatch, SuffixMatch, WildcardMatch
    };
}
