namespace ReviewDojo.Core.Scoring;

public class FindingMatcher
{
    private readonly int _proximity;
    public FindingMatcher(int proximity = 3) => _proximity = proximity;

    private int Distance(ManifestBug b, ReviewFinding f)
    {
        if (f.Line < b.LineStart) return b.LineStart - f.Line;
        if (f.Line > b.LineEnd) return f.Line - b.LineEnd;
        return 0;
    }

    public List<MatchResult> Match(IEnumerable<ManifestBug> bugs, IEnumerable<ReviewFinding> findings)
    {
        var bugList = bugs.ToList();
        var findList = findings.ToList();

        var candidates =
            (from bi in Enumerable.Range(0, bugList.Count)
             from fi in Enumerable.Range(0, findList.Count)
             let b = bugList[bi]
             let f = findList[fi]
             where string.Equals(b.FilePath, f.FilePath, StringComparison.OrdinalIgnoreCase)
             let dist = Distance(b, f)
             where dist <= _proximity
             let catOk = b.Category == f.Category
             select new { bi, fi, dist, catOk, credit = catOk ? 1.0 : 0.5 })
            .OrderByDescending(x => x.credit).ThenBy(x => x.dist)
            .ToList();

        var results = new List<MatchResult>();
        var usedBugs = new HashSet<int>();
        var usedFinds = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (usedBugs.Contains(c.bi) || usedFinds.Contains(c.fi)) continue;
            usedBugs.Add(c.bi); usedFinds.Add(c.fi);
            results.Add(new MatchResult(bugList[c.bi], findList[c.fi], c.credit, c.catOk));
        }
        for (int bi = 0; bi < bugList.Count; bi++)
            if (!usedBugs.Contains(bi)) results.Add(new MatchResult(bugList[bi], null, 0, false));
        for (int fi = 0; fi < findList.Count; fi++)
            if (!usedFinds.Contains(fi)) results.Add(new MatchResult(null, findList[fi], 0, false));
        return results;
    }
}
