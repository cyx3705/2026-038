namespace SolidEdgeMcp.Commands;

public sealed class Geometry() : SeCommand("geometry", CommandKind.Read,
    "Measure solid volume (mm^3), face count, and world-coordinate bounding box (mm).",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
        var models = new JsonArray();
        var total = 0.0;
        int count = Int(doc.Models.Count);
        for (var i = 1; i <= count; i++)
        {
            var model = doc.Models.Item(i);
            var body = model.Body;
            // GetRange fills two SAFEARRAY(double) [in,out] arguments.
            Array lo = new double[3], hi = new double[3];
            body.GetRange(ref lo, ref hi);
            double[] minimum = lo.Cast<object>().Select(v => Double(v) * 1000).ToArray();
            double[] maximum = hi.Cast<object>().Select(v => Double(v) * 1000).ToArray();
            double volume = Double(body.Volume) * 1e9;
            total += volume;
            models.Add(new JsonObject
            {
                ["model_index"] = i,
                ["name"] = Str(model.Name),
                ["volume_mm3"] = volume,
                ["face_count"] = Int(body.Faces(1).Count),
                ["min_mm"] = Numbers(minimum),
                ["max_mm"] = Numbers(maximum),
                ["size_mm"] = Numbers(minimum.Zip(maximum, (p, q) => q - p)),
            });
        }
        return new JsonObject { ["models"] = models, ["total_volume_mm3"] = total };
    }
}

public sealed class Features() : SeCommand("features", CommandKind.Read,
    "List ordered features, model indices, and Solid Edge feature status codes.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var doc = c.Edge.Doc(c.Args.Text("document_id"), part: true);
        var result = new JsonArray();
        int models = Int(doc.Models.Count);
        for (var i = 1; i <= models; i++)
        {
            var features = doc.Models.Item(i).Features;
            int count = Int(features.Count);
            for (var j = 1; j <= count; j++)
            {
                var feature = features.Item(j);
                result.Add(new JsonObject
                {
                    ["model_index"] = i,
                    ["index"] = j,
                    ["name"] = Str(feature.Name),
                    ["status"] = Int(feature.Status),
                });
            }
        }
        return new JsonObject { ["features"] = result };
    }
}

public sealed class SetExtrusionDepth() : SeCommand("set_extrusion_depth", CommandKind.Write,
    "Change a finite extrusion/cutout depth by feature name; read back depth and geometry.",
    Text("document_id"), Text("feature_name"), Length("depth_mm"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var documentId = c.Args.Text("document_id");
        var featureName = c.Args.Text("feature_name");
        var depthMm = c.Args.Number("depth_mm");
        var doc = c.Edge.Doc(documentId, part: true);
        var matches = new List<object>();
        int models = Int(doc.Models.Count);
        for (var i = 1; i <= models; i++)
        {
            var model = doc.Models.Item(i);
            foreach (var collection in new[] { model.ExtrudedProtrusions, model.ExtrudedCutouts })
            {
                int count = Int(collection.Count);
                for (var j = 1; j <= count; j++)
                {
                    var feature = collection.Item(j);
                    if (Str(feature.Name) == featureName)
                        matches.Add(feature);
                }
            }
        }
        if (matches.Count != 1)
            throw new EdgeError("Supply a unique extrusion/cutout name from features.");
        dynamic target = matches[0];
        int extentType = 0, side = 0;
        double previous = 0;
        target.GetDirection1Extent(out extentType, out side, out previous);
        if (extentType != 13)
            throw new EdgeError("Only an existing finite extrusion depth can be edited.");
        // KeyPointFlags is declared KeyPointExtentConstants* even though it is an input; the dynamic
        // binder cannot convert this signature, so call through IDispatch with a null IDispatch key point.
        Invoke(matches[0], "ApplyDirection1Extent", [13, side, depthMm / 1000, new DispatchWrapper(null), 0], 4);
        int ignoredType = 0, ignoredSide = 0;
        double actual = 0;
        target.GetDirection1Extent(out ignoredType, out ignoredSide, out actual);
        if (!IsClose(actual * 1000, depthMm))
            throw new EdgeError("Feature depth did not read back as requested. Inspect the part.");
        var document = new JsonObject { ["document_id"] = documentId };
        return new JsonObject
        {
            ["feature"] = featureName,
            ["before_mm"] = previous * 1000,
            ["after_mm"] = actual * 1000,
            ["geometry"] = c.Run("geometry", document),
            ["features"] = c.Run("features", document),
        };
    }
}

public sealed class ListVariables() : SeCommand("list_variables", CommandKind.Read,
    "List numeric variables/dimensions, exact names, formulas, values, and units.",
    Text("document_id"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var variables = c.Edge.Doc(c.Args.Text("document_id"), part: true).Variables;
        var found = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        // Query both dimensions and numeric user variables; their enum values are not 0/1.
        foreach (var kind in new[] { 1661573600, 1560616706 })
        {
            var collection = variables.Query("*", 2, kind, true);
            int count = Int(collection.Count);
            for (var i = 1; i <= count; i++)
            {
                JsonObject entry = VariableInfo(variables, collection.Item(i));
                var name = (string)entry["name"]!;
                if (!found.ContainsKey(name))
                    order.Add(name);
                found[name] = entry;
            }
        }
        return new JsonObject { ["variables"] = new JsonArray(order.Select(n => (JsonNode)found[n]).ToArray()) };
    }
}

public sealed class SetVariable() : SeCommand("set_variable", CommandKind.Write,
    "Set an existing variable and read it back; unit must match list_variables exactly.",
    Text("document_id"), Text("name"), Finite("value"), Choice("unit", "mm", "deg", "raw"))
{
    public override CommandOutput Execute(CommandContext c)
    {
        var documentId = c.Args.Text("document_id");
        var name = c.Args.Text("name");
        var value = c.Args.Number("value");
        var unit = c.Args.Text("unit");
        var variables = c.Edge.Doc(documentId, part: true).Variables;
        var document = new JsonObject { ["document_id"] = documentId };
        var names = c.Run("list_variables", document)["variables"]!.AsArray().Select(v => (string)v!["name"]!).ToHashSet();
        if (!names.Contains(name))
            throw new EdgeError("Variable not found. Use an exact name from list_variables.");
        var variable = variables.Item(name);
        JsonObject before = VariableInfo(variables, variable);
        var expected = (string)before["unit"]!;
        if (unit != expected)
            throw new EdgeError($"Variable requires unit='{expected}'; received '{unit}'.");
        variable.Value = unit switch { "mm" => value / 1000, "deg" => value * Math.PI / 180, _ => value };
        JsonObject after = VariableInfo(variables, variable);
        if (!IsClose((double)after["value"]!, value))
            throw new EdgeError($"Value did not read back as requested: {after.ToJsonString()}. Inspect the model.");
        return new JsonObject { ["before"] = before, ["after"] = after, ["features"] = c.Run("features", document) };
    }
}
