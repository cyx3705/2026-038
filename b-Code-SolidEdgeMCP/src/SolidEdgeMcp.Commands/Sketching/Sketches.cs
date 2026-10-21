using System.Text.Json;

namespace SolidEdgeMcp.Commands.Sketching;

/// <summary>
/// The sketch pipeline shared by the commands: plan, draw, read back, verify, and remember the
/// description so a sketch can be edited and redrawn later.
/// </summary>
public static class Sketches
{
    /// <summary>Read-back coordinates carry solver noise; joints are judged a little more loosely.</summary>
    public const double ReadTolerance = 1e-5;

    /// <summary>JSON inside error text stays readable: no \u escapes for quotes, angle brackets or CJK.</summary>
    public static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static EdgeError Rejected(string message, SketchAnalysis analysis)
    {
        var detail = SketchReport.Json(analysis);
        return new EdgeError($"{message}\n{detail.ToJsonString(Plain)}");
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
    /// All elements of a profile set (End() splits nested loops into sibling profiles). Names in
    /// <paramref name="ids"/> become the description's ids, and its order is restored.
    /// </summary>
    public static List<Element> ReadSet(dynamic set, IReadOnlyDictionary<string, string>? ids = null, SketchSpec? spec = null)
    {
        var elements = new List<Element>();
        var profiles = set.Profiles;
        int count = Int(profiles.Count);
        for (var i = 1; i <= count; i++)
            elements.AddRange(SketchCom.ReadBack(profiles.Item(i), ids));
        if (spec is null)
            return elements;
        var references = spec.References.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        // The revolve axis is ordinary geometry to Solid Edge; to the description it never forms a region.
        elements = elements.Select(e => e.Id == spec.Axis ? e with { Construction = true }
            : references.Contains(e.Id) ? e with { External = true } : e).ToList();
        var order = spec.Elements.Select((e, k) => (e.Id, k)).ToDictionary(p => p.Id, p => p.k);
        var groups = spec.Elements.ToDictionary(e => e.Id, e => e.Group);
        return elements
            .Select(e => groups.TryGetValue(e.Id, out var group) ? e with { Group = group } : e)
            .OrderBy(e => order.TryGetValue(e.Id, out var k) ? k : int.MaxValue).ToList();
    }

    // ---- remembered descriptions --------------------------------------------------------------

    private static string Key(string profileId) => $"sketch:{profileId}";

    private static string SetKey(string profileId) => $"sketch-set:{profileId}";

    public static string AxisKey(string profileId) => $"sketch-axis:{profileId}";

    /// <summary>A sketch drawn by this server: its description, plane, Solid Edge names and set.</summary>
    public sealed record Stored(string ProfileId, string DocumentId, SketchSpec Spec, int PlaneIndex, Dictionary<string, string> Ids,
        List<Element> References, List<string> DimensionNames, object Set);

    private static void Save(SolidEdgeBackend edge, string profileId, string documentId, SketchSpec spec, int planeIndex, Drawing drawing,
        IEnumerable<Element> references, object set)
    {
        edge.State[Key(profileId)] = new JsonObject
        {
            ["document_id"] = documentId,
            ["plane_index"] = planeIndex,
            ["spec"] = spec.ToJson(),
            ["names"] = new JsonObject(drawing.Ids.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            // Datum and reference geometry as it was when drawn, so a later view can judge relations to it.
            ["references"] = new JsonArray(references.Select(r => (JsonNode)SketchSpec.ElementJson(r)).ToArray()),
            ["dimension_names"] = new JsonArray(drawing.Dimensions.Select(d => (JsonNode)Str(((dynamic)d.Com).SystemName)).ToArray()),
        };
        edge.State[SetKey(profileId)] = set;
        if (drawing.Axis is not null)
            edge.State[AxisKey(profileId)] = drawing.Axis;
        else
            edge.State.Remove(AxisKey(profileId));
    }

    public static Stored? Load(SolidEdgeBackend edge, string profileId)
    {
        if (!edge.State.TryGetValue(Key(profileId), out var stored) || stored is not JsonObject json
            || !edge.State.TryGetValue(SetKey(profileId), out var set))
            return null;
        var spec = SketchSpec.Read(JsonSerializer.SerializeToElement(json["spec"]));
        var ids = json["names"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!, StringComparer.Ordinal);
        var references = json["references"]!.AsArray()
            .SelectMany((r, i) => SketchShapes.Expand(JsonSerializer.SerializeToElement(r), $"references[{i}]").Elements)
            .Select(e => e with { External = true, Construction = true }).ToList();
        var names = json["dimension_names"]!.AsArray().Select(n => (string)n!).ToList();
        return new Stored(profileId, (string)json["document_id"]!, spec, (int)json["plane_index"]!, ids, references, names, set);
    }

    /// <summary>The remembered description of a profile set, if this server drew it.</summary>
    public static Stored? Find(SolidEdgeBackend edge, string documentId, object set)
    {
        foreach (var key in edge.State.Keys.Where(k => k.StartsWith("sketch-set:", StringComparison.Ordinal)).ToList())
        {
            if (!Same(edge.State[key], set))
                continue;
            var stored = Load(edge, key["sketch-set:".Length..]);
            if (stored?.DocumentId == documentId)
                return stored;
        }
        return null;
    }

    /// <summary>Replace a remembered description (after a dimension edit moved the geometry).</summary>
    public static void Update(SolidEdgeBackend edge, Stored stored, SketchSpec spec)
    {
        if (edge.State[Key(stored.ProfileId)] is JsonObject json)
            json["spec"] = spec.ToJson();
    }

    public static void Forget(SolidEdgeBackend edge, string profileId)
    {
        edge.State.Remove(Key(profileId));
        edge.State.Remove(SetKey(profileId));
        edge.State.Remove(AxisKey(profileId));
    }

    // ---- the pipeline -------------------------------------------------------------------------

    /// <summary>Check a description without Solid Edge (datums stand at the plane's own axes).</summary>
    public static SketchAnalysis Plan(SketchSpec spec, IEnumerable<Element>? included = null, IEnumerable<Element>? references = null) =>
        new([.. spec.Elements.Where(e => !e.External), .. included ?? []], Sketch.Tolerance, spec,
            references ?? (spec.UsesDatums ? SketchAnalysis.DefaultDatums() : null));

    /// <summary>The plan of a description as a result, without touching Solid Edge.</summary>
    public static JsonObject DryRun(SketchSpec spec)
    {
        var plan = Plan(spec);
        var result = new JsonObject { ["dry_run"] = true, ["valid"] = plan.Valid };
        if (spec.NeedsModel)
        {
            result["note"] = "Checked without the model: @x_axis/@y_axis stand at the plane's own axes and references are not "
                + "resolved, so relations to them are checked when the sketch is drawn.";
        }
        return Merge(result, SketchReport.Json(plan));
    }

    /// <summary>
    /// Draw a description as a new profile set and verify it: geometry, relations and dimensions
    /// read back from Solid Edge must match the plan, or the set is removed again. With
    /// <paramref name="profileId"/> the new set takes over that id (an edit); the caller removes the old set.
    /// </summary>
    public static JsonObject Build(SolidEdgeBackend edge, string documentId, int planeIndex, SketchSpec spec, string? profileId = null)
    {
        var planned = Plan(spec);
        if (!planned.Valid && !spec.NeedsModel)
            throw Rejected("Sketch description has defects; nothing was drawn.", planned);
        if (!planned.Elements.Any(e => e.Profile))
            throw new EdgeError("A sketch needs at least one closed loop; construction geometry and points alone cannot make a profile.");
        var doc = edge.Doc(documentId, part: true);
        // Naming a dimension after an existing variable makes Solid Edge raise a dialog mid-call;
        // refuse before anything is drawn.
        var taken = SketchCom.VariableNames((object)doc);
        var clash = spec.Dimensions.Select(d => d.Name).FirstOrDefault(n => n is not null && taken.Contains(n));
        if (clash is not null)
            throw new EdgeError($"A variable named {clash} already exists in this part; choose another dimension name.");
        var profile = EdgeOps.NewProfile(edge, documentId, planeIndex);
        dynamic set = profile.Parent;
        var drawing = new Drawing();
        SketchAnalysis drawn;
        int code;
        double deviation;
        bool underDefined;
        var stage = "references";
        try
        {
            if (spec.NeedsModel)
            {
                List<Element> external = SketchCom.Resolve(doc, profile, spec, drawing);
                planned = Plan(spec, external.Where(e => !e.Construction), external.Where(e => e.Construction));
                if (!planned.Valid)
                    throw Rejected("Sketch description has defects against the model geometry; nothing was kept.", planned);
            }
            stage = "geometry";
            SketchCom.Draw(profile, spec, drawing);
            stage = "relations";
            SketchCom.Relate(profile, planned, drawing);
            stage = "dimensions";
            SketchCom.Dimension(doc, profile, planned, drawing);
            stage = "end";
            code = Int(profile.End(SketchCom.Validation));
            stage = "read-back";
            List<Element> actual = ReadSet(set, drawing.Ids, spec);
            drawn = new SketchAnalysis(actual, ReadTolerance, spec, planned.References);
            deviation = SketchCom.Deviation(planned.Elements, actual);
            underDefined = Convert.ToBoolean(set.IsUnderDefined);
        }
        catch (EdgeError)
        {
            TryDelete(set);
            throw;
        }
        catch (Exception exc)
        {
            TryDelete(set);
            throw new EdgeError($"Drawing failed while adding {stage} and the partial sketch was removed: {exc.Message}");
        }
        var areaDrift = Math.Abs(drawn.NetArea - planned.NetArea);
        var areaLimit = Math.Max(1e-6, Math.Abs(planned.NetArea) * (planned.Approximate ? 1e-2 : 1e-6));
        if (code != 0 || !drawn.Valid || deviation > 1e-3 || areaDrift > areaLimit)
        {
            TryDelete(set);
            throw Rejected(
                $"Solid Edge result differs from the description (validation code {code}, deviation {Sketch.Format(deviation)} mm, "
                + $"area drift {Sketch.Format(areaDrift)} mm2); the sketch was removed.", drawn);
        }

        profileId ??= SolidEdgeBackend.NewId();
        edge.Profiles[profileId] = (documentId, (object)profile);
        Save(edge, profileId, documentId, spec, planeIndex, drawing, planned.References, set);
        var result = new JsonObject
        {
            ["document_id"] = documentId,
            ["profile_id"] = profileId,
            ["sketch_index"] = IndexOf(doc, set),
            ["plane_index"] = planeIndex,
            ["validation_code"] = code,
            ["max_deviation_mm"] = Sketch.Round(deviation),
            ["fully_constrained"] = !underDefined,
            ["plane_frame"] = SketchCom.FrameJson((object)profile),
        };
        var report = SketchReport.Json(drawn);
        AttachVariables(report, drawing.Dimensions.Select(d => d.Com).ToList());
        return Merge(result, report);
    }

    /// <summary>Add each dimension's Solid Edge variable name to the report entries, in order.</summary>
    public static void AttachVariables(JsonObject report, IReadOnlyList<object> dimensions)
    {
        if (report["dimensions"] is not JsonArray entries)
            return;
        for (var i = 0; i < entries.Count && i < dimensions.Count; i++)
        {
            dynamic d = dimensions[i];
            entries[i]!["variable"] = Str(d.VariableTableName);
        }
    }
}
