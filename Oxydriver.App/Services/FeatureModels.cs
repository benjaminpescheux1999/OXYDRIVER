using System.Collections.Generic;

namespace Oxydriver.Services;

public sealed class FeatureDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SiteUrl { get; set; }
    public List<FeatureResource> Resources { get; set; } = [];
}

public sealed class FeatureResource
{
    public string Database { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public List<FeatureColumnRight> Columns { get; set; } = [];
}

public sealed class FeatureColumnRight
{
    public string Name { get; set; } = string.Empty;
    public List<string> Rights { get; set; } = [];
}

