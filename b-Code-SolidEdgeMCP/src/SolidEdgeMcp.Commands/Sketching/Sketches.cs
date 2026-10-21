namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>Helpers shared by the sketch commands.</summary>
public static class Sketches
{
    /// <summary>Read-back coordinates carry solver noise; joints are judged a little more loosely.</summary>
    public const double ReadTolerance = 1e-5;

    public static EdgeError Rejected(string message, SketchAnalysis analysis)
    {
        var detail = SketchReport.Json(analysis);
        return new EdgeError($"{message}\n{detail.ToJsonString()}");
    }

    public static void TryDelete(dynamic set)
    {
        try
        {
            set.Delete();
        }
        catch (Exception)
        {
            // The caller is already reporting a failure; a leftover sketch shows up in view_sketch.
        }
    }

    public static int IndexOf(dynamic doc, object set)
    {
        int count = Int(doc.ProfileSets.Count);
        for (var i = count; i >= 1; i--)
        {
            if (Same((object)doc.ProfileSets.Item(i), set))
                return i;
        }
        return 0;
    }

    /// <summary>
    /// All elements of a profile set. With <paramref name="ids"/> (a sketch just drawn), the n-th
    /// element of each kind takes the n-th id of that kind and the caller's order is restored.
    /// </summary>
    public static List<Element> ReadSet(dynamic set, IReadOnlyList<Element>? ids = null)
    {
        var elements = new List<Element>();
        var profiles = set.Profiles;
        int count = Int(profiles.Count);
        for (var i = 1; i <= count; i++)
            elements.AddRange(SketchCom.ReadBack(profiles.Item(i)));
        if (ids is null)
            return elements;
        var renamed = new List<Element>();
        foreach (var group in elements.GroupBy(e => e.Kind))
        {
            var wanted = ids.Where(e => e.Kind == group.Key).ToList();
            renamed.AddRange(group.Select((e, k) => k < wanted.Count ? e with { Id = wanted[k].Id } : e));
        }
        var order = ids.Select((e, k) => (e.Id, k)).ToDictionary(p => p.Id, p => p.k);
        return renamed.OrderBy(e => order.TryGetValue(e.Id, out var k) ? k : int.MaxValue).ToList();
    }
}
