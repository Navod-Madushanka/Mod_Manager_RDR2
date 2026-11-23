namespace Saddlebag.Core.Bisect;

public enum BisectPhase
{
    /// <summary>Halving the candidates: each launch tests half of them.</summary>
    Search,
    /// <summary>One candidate left: launch it alone (plus kept mods) to confirm it crashes by itself.</summary>
    Confirm,
    /// <summary>The culprit crashes on its own.</summary>
    Found,
    /// <summary>No single mod crashes on its own; the crash needs a combination inside <see cref="BisectSession.CrashSet"/>.</summary>
    Interaction,
    /// <summary>No suspect reproduced the crash: it's in the kept mods, the game, or needs a combination.</summary>
    NotFound,
}

public sealed record BisectSnapshot(List<string> Candidates, List<string> Testing, BisectPhase Phase, bool? Answer);

/// <summary>
/// The pure part of crash bisect: which mods to test next and what an answer means. No file I/O, so the search
/// itself is easy to test. Assumes one mod causes the crash; the confirm launch catches the case where it doesn't.
/// </summary>
public sealed class BisectSession
{
    public string OriginalProfile { get; set; } = "";
    public string BisectProfile { get; set; } = "";
    public string? OriginalMode { get; set; }
    /// <summary>Always enabled during the bisect (hooks and loaders other mods need).</summary>
    public List<string> Kept { get; set; } = [];
    /// <summary>Every mod under suspicion at the start, in priority order.</summary>
    public List<string> Suspects { get; set; } = [];
    /// <summary>Mods that could still be the culprit.</summary>
    public List<string> Candidates { get; set; } = [];
    /// <summary>Suspects enabled for the current launch.</summary>
    public List<string> Testing { get; set; } = [];
    public BisectPhase Phase { get; set; } = BisectPhase.Search;
    public List<BisectSnapshot> History { get; set; } = [];

    public bool IsDone => Phase is BisectPhase.Found or BisectPhase.Interaction or BisectPhase.NotFound;
    public string? Culprit => Phase == BisectPhase.Found ? Candidates.Single() : null;

    /// <summary>
    /// For an interaction: the smallest set of suspects that was seen to crash. Binary search can split the mods that
    /// crash together, so it can't name one culprit, but the crash is somewhere in this set.
    /// </summary>
    public List<string> CrashSet => Phase == BisectPhase.Interaction
        ? History.Where(h => h.Phase == BisectPhase.Search && h.Answer == true).Select(h => h.Testing).MinBy(t => t.Count) ?? []
        : [];
    public int Launches => History.Count;

    /// <summary>Launches still needed in the worst case, including the confirm launch.</summary>
    public int RemainingEstimate => IsDone ? 0 : Phase == BisectPhase.Confirm ? 1 : (int)Math.Ceiling(Math.Log2(Candidates.Count)) + 1;

    /// <summary>Mods enabled for the current launch.</summary>
    public IEnumerable<string> Enabled => Kept.Concat(IsDone ? [] : Testing);

    public static BisectSession Start(IReadOnlyList<string> suspects, IReadOnlyList<string> kept)
    {
        if (suspects.Count == 0) throw new InvalidOperationException("No mods to bisect: every enabled mod is kept on. Use --suspect to include one.");
        var s = new BisectSession { Suspects = suspects.ToList(), Kept = kept.ToList(), Candidates = suspects.ToList() };
        s.PlanNext();
        return s;
    }

    private void PlanNext()
    {
        if (Candidates.Count == 1)
        {
            Phase = BisectPhase.Confirm;
            Testing = [.. Candidates];
        }
        else
        {
            Phase = BisectPhase.Search;
            Testing = Candidates.Take(Candidates.Count / 2).ToList();
        }
    }

    /// <summary>Record the result of the current launch: did the game crash?</summary>
    public void Answer(bool crashed)
    {
        if (IsDone) throw new InvalidOperationException("The bisect is finished. Run `saddlebag bisect reset`.");
        History.Add(new BisectSnapshot([.. Candidates], [.. Testing], Phase, crashed));

        if (Phase == BisectPhase.Confirm)
        {
            if (crashed) Phase = BisectPhase.Found;
            else Phase = History.Any(h => h.Phase == BisectPhase.Search && h.Answer == true) ? BisectPhase.Interaction : BisectPhase.NotFound;
            return;
        }
        Candidates = crashed ? [.. Testing] : Candidates.Except(Testing).ToList();
        PlanNext();
    }

    /// <summary>Take back the last answer (e.g. you mixed up good and bad).</summary>
    public void Undo()
    {
        if (History.Count == 0) throw new InvalidOperationException("Nothing to undo.");
        var last = History[^1];
        History.RemoveAt(History.Count - 1);
        Candidates = last.Candidates;
        Testing = last.Testing;
        Phase = last.Phase;
    }
}
