using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Oxydriver.Services;

internal static class SqlServerCatalog
{
    /// <summary>
    /// Liste les noms de bases sur l'instance (requête valide depuis master ou via préfixe master).
    /// </summary>
    internal static async Task<HashSet<string>> ListDatabaseNamesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM master.sys.databases";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            set.Add(reader.GetString(0));
        return set;
    }
}
