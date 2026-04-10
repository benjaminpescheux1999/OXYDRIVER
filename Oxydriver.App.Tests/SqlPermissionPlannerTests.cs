using Oxydriver.Services;
using Xunit;

namespace Oxydriver.App.Tests;

public sealed class SqlPermissionPlannerTests
{
    [Fact]
    public void ParseFromCatalogJson_ReturnsReadAndWritePermissions()
    {
        var raw = """
        [
          {
            "name":"Espace client",
            "code":"espace_client",
            "description":"x",
            "endpoints":[],
            "resources":[
              {
                "database":"SA_GAZSRV",
                "table":"CLIEN",
                "columns":[
                  {"name":"EMAIL","rights":["read","write"]},
                  {"name":"TELDO","rights":["write"]}
                ]
              }
            ]
          }
        ]
        """;

        var map = SqlPermissionPlanner.ParseFromCatalogJson(raw);
        Assert.True(map.ContainsKey("SA_GAZSRV|CLIEN"));
        var entry = map["SA_GAZSRV|CLIEN"];
        Assert.False(entry.AllowSelectAllColumns);
        Assert.Contains("EMAIL", entry.ReadColumns);
        Assert.Contains("EMAIL", entry.WriteColumns);
        Assert.Contains("TELDO", entry.WriteColumns);
    }

    [Fact]
    public void ParseFromCatalogJson_InvalidJson_ReturnsEmpty()
    {
        var map = SqlPermissionPlanner.ParseFromCatalogJson("{bad json");
        Assert.Empty(map);
    }
}
