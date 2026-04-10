using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Oxydriver.Services;

public static class SqlPermissionPlanner
{
    public static Dictionary<string, SqlPermissionEntry> ParseFromCatalogJson(string rawCatalog)
    {
        var result = new Dictionary<string, SqlPermissionEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawCatalog)) return result;
        try
        {
            var features = JsonSerializer.Deserialize<FeatureDefinition[]>(
                rawCatalog,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            foreach (var feature in features)
            {
                foreach (var resource in feature.Resources)
                {
                    var key = $"{resource.Database}|{resource.Table}";
                    if (!result.TryGetValue(key, out var entry))
                    {
                        entry = new SqlPermissionEntry();
                        result[key] = entry;
                    }

                    foreach (var column in resource.Columns)
                    {
                        if (column.Rights.Any(x => string.Equals(x, "read", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (string.Equals(column.Name, "*", StringComparison.OrdinalIgnoreCase))
                                entry.AllowSelectAllColumns = true;
                            else
                                entry.ReadColumns.Add(column.Name);
                        }
                        if (column.Rights.Any(x => string.Equals(x, "write", StringComparison.OrdinalIgnoreCase)))
                            entry.WriteColumns.Add(column.Name);
                    }
                }
            }
        }
        catch
        {
            // Invalid catalog JSON -> return empty map.
        }
        return result;
    }
}

public sealed class SqlPermissionEntry
{
    public bool AllowSelectAllColumns { get; set; }
    public HashSet<string> ReadColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> WriteColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
}
