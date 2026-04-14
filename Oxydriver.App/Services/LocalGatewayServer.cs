using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Oxydriver.Services;

// MVP: serveur HTTP local minimal (HttpListener) exposé via cloudflared.
// Pour la suite: migrer vers ASP.NET Core Kestrel + auth + routing + middleware.
public sealed class LocalGatewayServer
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ILogger<LocalGatewayServer> _logger;
    private HttpListener? _listener;
    private int? _boundPort;
    private CancellationTokenSource? _cts;
    private FeatureDefinition[] _catalog = [];
    private HashSet<string> _enabledFeatures = [];
    private Dictionary<string, HashSet<string>> _featureFolderSelections = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Noms de bases réellement présentes sur l'instance (null = filtre désactivé si liste impossible).</summary>
    private HashSet<string>? _existingSqlDatabaseNames;
    public event Action<ClientRequestLogEntry>? ClientRequestLogged;

    public LocalGatewayServer(AppSettingsStore settingsStore, ILogger<LocalGatewayServer> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        UpdateAuthorizationPolicy(_settingsStore.Load());
    }

    public Task<int> EnsureStartedAsync(int port)
    {
        if (_listener is not null && _listener.IsListening)
            return Task.FromResult(_boundPort ?? port);

        Exception? lastError = null;
        for (var offset = 0; offset < 20; offset++)
        {
            var candidatePort = port + offset;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                listener.Start();
                _listener = listener;
                _boundPort = candidatePort;
                _logger.LogInformation("Local gateway started on {Port}", candidatePort);
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(listener, _cts.Token));
                return Task.FromResult(candidatePort);
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { listener.Close(); } catch { /* ignore */ }
            }
        }

        throw new InvalidOperationException(
            "Impossible de demarrer la gateway locale: aucun port libre entre " + port + " et " + (port + 19) + ".",
            lastError
        );
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gateway loop error");
                await Task.Delay(500, ct);
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.Url?.AbsolutePath == "/health")
            {
                await WriteJson(ctx, 200, new { ok = true, name = "OXYDRIVER" });
                EmitClientRequestLog(ctx.Request, 200, "health");
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/authorize" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadBodyAsync(ctx.Request);
                var check = JsonSerializer.Deserialize<AccessRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new AccessRequest();
                var result = IsAllowed(check);
                await WriteJson(ctx, result.Allowed ? 200 : 403, result);
                EmitClientRequestLog(
                    ctx.Request,
                    result.Allowed ? 200 : 403,
                    result.Reason,
                    body,
                    JsonSerializer.Serialize(result),
                    null
                );
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/client-summary" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var clientId = ctx.Request.QueryString["clientId"];
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    var response = new { error = "missing_client_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_client_id", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var summary = await GetClientSummaryAsync(clientId.Trim());
                if (summary is null)
                {
                    var response = new { error = "client_not_found" };
                    await WriteJson(ctx, 404, response);
                    EmitClientRequestLog(ctx.Request, 404, "client_not_found", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var okResponse = new { ok = true, data = summary };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "client_summary_ok", null, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/client-update" && ctx.Request.HttpMethod == "POST")
            {
                var body = await ReadBodyAsync(ctx.Request);
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", body, JsonSerializer.Serialize(response), null);
                    return;
                }

                var payload = JsonSerializer.Deserialize<ClientUpdateRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (payload is null || string.IsNullOrWhiteSpace(payload.ClientId))
                {
                    var response = new { error = "missing_client_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_client_id", body, JsonSerializer.Serialize(response), null);
                    return;
                }

                var updated = await UpdateClientSummaryAsync(payload);
                var okResponse = new { ok = true, updated };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "client_update_ok", body, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/factures" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var clientId = ctx.Request.QueryString["clientId"];
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    var response = new { error = "missing_client_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_client_id", null, JsonSerializer.Serialize(response), null);
                    return;
                }
                var factureTypeRaw = (ctx.Request.QueryString["type"] ?? string.Empty).Trim();
                var factureType = NormalizeFactureType(factureTypeRaw);
                if (!string.IsNullOrWhiteSpace(factureTypeRaw) && string.IsNullOrWhiteSpace(factureType))
                {
                    var response = new { error = "invalid_facture_type", expected = new[] { "F", "D", "I" } };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "invalid_facture_type", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var factures = await GetClientFacturesAsync(clientId.Trim(), factureType);
                var okResponse = new { ok = true, data = factures };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "client_factures_ok", null, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/facture-summary" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var factureId = ctx.Request.QueryString["factureId"];
                if (string.IsNullOrWhiteSpace(factureId))
                {
                    var response = new { error = "missing_facture_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_facture_id", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var facture = await GetFactureSummaryAsync(factureId.Trim());
                if (facture is null)
                {
                    var response = new { error = "facture_not_found" };
                    await WriteJson(ctx, 404, response);
                    EmitClientRequestLog(ctx.Request, 404, "facture_not_found", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var okResponse = new { ok = true, data = facture };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "facture_summary_ok", null, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/appareils" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var clientId = ctx.Request.QueryString["clientId"];
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    var response = new { error = "missing_client_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_client_id", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var appareils = await GetClientAppareilsAsync(clientId.Trim());
                var okResponse = new { ok = true, data = appareils };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "client_appareils_ok", null, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/espace-client/reglements" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsUtilityCallAuthorized(ctx.Request))
                {
                    var response = new { error = "unauthorized_utility_call" };
                    await WriteJson(ctx, 401, response);
                    EmitClientRequestLog(ctx.Request, 401, "unauthorized_utility_call", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var clientId = ctx.Request.QueryString["clientId"];
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    var response = new { error = "missing_client_id" };
                    await WriteJson(ctx, 400, response);
                    EmitClientRequestLog(ctx.Request, 400, "missing_client_id", null, JsonSerializer.Serialize(response), null);
                    return;
                }

                var reglements = await GetClientReglementsAsync(clientId.Trim());
                var okResponse = new { ok = true, data = reglements };
                await WriteJson(ctx, 200, okResponse);
                EmitClientRequestLog(ctx.Request, 200, "client_reglements_ok", null, JsonSerializer.Serialize(okResponse), null);
                return;
            }

            await WriteJson(ctx, 404, new { error = "not_found" });
            EmitClientRequestLog(ctx.Request, 404, "not_found");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Gateway authorization failure");
            await WriteJson(ctx, 403, new { error = "forbidden_by_policy", message = ex.Message });
            EmitClientRequestLog(ctx.Request, 403, $"forbidden_by_policy: {ex.Message}", null, null, ex.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway request processing error");
            EmitClientRequestLog(ctx.Request, 500, ex.Message, null, null, ex.ToString());
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = Encoding.UTF8.GetBytes(json);

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    public void UpdateAuthorizationPolicy(AppSettings settings)
    {
        _catalog = ParseCatalog(settings.ApiFeatureCatalogJson);
        _enabledFeatures = ParseEnabled(settings.EnabledFeatureCodesJson);
        _featureFolderSelections = ParseFeatureFolderSelections(settings.FeatureFolderSelectionsJson);
        _existingSqlDatabaseNames = TryLoadExistingDatabaseNames(settings.SqlConnectionString);
        _logger.LogInformation("Authorization policy reloaded. Features enabled: {Count}", _enabledFeatures.Count);
    }

    private HashSet<string>? TryLoadExistingDatabaseNames(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;
        try
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM master.sys.databases";
            using var reader = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                set.Add(reader.GetString(0));
            return set;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway: impossible de lister master.sys.databases; pas de filtre sur les bases absentes.");
            return null;
        }
    }

    private bool IsDatabasePresentOnServer(string? databaseName)
    {
        var db = (databaseName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(db)) return false;
        return _existingSqlDatabaseNames is null || _existingSqlDatabaseNames.Contains(db);
    }

    private static FeatureDefinition[] ParseCatalog(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<FeatureDefinition[]>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static HashSet<string> ParseEnabled(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(raw) ?? [];
            return arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, HashSet<string>> ParseFeatureFolderSelections(string? raw)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(raw) ?? [];
            foreach (var kvp in parsed)
            {
                var featureCode = (kvp.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(featureCode)) continue;
                var folders = (kvp.Value ?? [])
                    .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                result[featureCode] = folders;
            }
        }
        catch
        {
            // ignore invalid json
        }
        return result;
    }

    private AccessDecision IsAllowed(AccessRequest req)
    {
        var action = (req.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("read" or "write"))
            return AccessDecision.Deny("invalid_action");

        foreach (var feature in _catalog)
        {
            if (!_enabledFeatures.Contains(feature.Code)) continue;
            foreach (var resource in feature.Resources)
            {
                if (!string.Equals(resource.Database, req.Database, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsDatabasePresentOnServer(resource.Database)) continue;
                if (!string.Equals(resource.Table, req.Table, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsResourceAllowedForFeature(feature.Code, resource.Database)) continue;

                foreach (var col in resource.Columns)
                {
                    var targetCol = req.Column ?? "*";
                    var columnMatch =
                        string.Equals(col.Name, "*", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(col.Name, targetCol, StringComparison.OrdinalIgnoreCase);
                    if (!columnMatch) continue;
                    if (col.Rights.Any(r => string.Equals(r, action, StringComparison.OrdinalIgnoreCase)))
                        return AccessDecision.Allow(feature.Code);
                }
            }
        }
        return AccessDecision.Deny("not_exposed_by_client");
    }

    private bool IsUtilityCallAuthorized(HttpListenerRequest req)
    {
        var provided = req.Headers["x-oxydriver-key"];
        var settings = _settingsStore.Load();
        var expected = settings.AccessKey;
        return !string.IsNullOrWhiteSpace(expected) &&
               string.Equals(provided, expected, StringComparison.Ordinal);
    }

    private async Task<ClientSummary?> GetClientSummaryAsync(string clientId)
    {
        EnsureAllowedOnConfiguredDatabases("client summary", clientId, "CLIEN", new[]
        {
            "CLIEN", "NOM", "PRENO", "TELDO", "TELPO", "TELTR", "EMAIL", "CONTR", "NUMRU", "QUARU", "RUE1", "VILLE", "CODPO", "RENOU"
        }, "read");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var resources = GetEnabledResourcesByTable("CLIEN");
        if (resources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour la fonctionnalité espace_client.");

        foreach (var resource in resources)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT TOP 1
  [CLIEN], [PRENO], [NOM], [TELDO], [TELPO], [TELTR], [EMAIL], [CONTR],
  [NUMRU], [QUARU], [RUE1], [VILLE], [CODPO], [RENOU]
FROM [{resource.Database}].[dbo].[{resource.Table}]
WHERE [CLIEN] = @clientId";
            cmd.Parameters.AddWithValue("@clientId", clientId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                continue;

            return new ClientSummary
            {
                ClientId = reader["CLIEN"]?.ToString() ?? clientId,
                Prenom = reader["PRENO"]?.ToString() ?? string.Empty,
                Nom = reader["NOM"]?.ToString() ?? string.Empty,
                TelephoneDomicile = reader["TELDO"]?.ToString() ?? string.Empty,
                TelephonePortable = reader["TELPO"]?.ToString() ?? string.Empty,
                TelephoneTravail = reader["TELTR"]?.ToString() ?? string.Empty,
                Email = reader["EMAIL"]?.ToString() ?? string.Empty,
                SousContrat = string.Equals(reader["CONTR"]?.ToString(), "O", StringComparison.OrdinalIgnoreCase),
                NumeroRue = reader["NUMRU"]?.ToString() ?? string.Empty,
                QualiteAdresse = reader["QUARU"]?.ToString() ?? string.Empty,
                Rue = reader["RUE1"]?.ToString() ?? string.Empty,
                Ville = reader["VILLE"]?.ToString() ?? string.Empty,
                CodePostal = reader["CODPO"]?.ToString() ?? string.Empty,
                Renouvellement = reader["RENOU"] is DateTime dt
                    ? dt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    : reader["RENOU"]?.ToString() ?? string.Empty
            };
        }
        return null;
    }

    private async Task<int> UpdateClientSummaryAsync(ClientUpdateRequest payload)
    {
        var clientId = payload.ClientId.Trim();
        var fields = new List<(string Column, string? Value)>
        {
            ("EMAIL", payload.Email),
            ("TELDO", payload.TelephoneDomicile),
            ("TELPO", payload.TelephonePortable),
            ("TELTR", payload.TelephoneTravail)
        };
        var writable = fields.Where(f => f.Value is not null).ToArray();
        if (writable.Length == 0) return 0;

        EnsureAllowedOnConfiguredDatabases("client update", clientId, "CLIEN", writable.Select(x => x.Column), "write");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var resources = GetEnabledResourcesByTable("CLIEN");
        if (resources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour CLIEN.");

        var totalUpdated = 0;
        foreach (var resource in resources)
        {
            await using var cmd = conn.CreateCommand();
            var setClauses = new List<string>();
            var idx = 0;
            foreach (var field in writable)
            {
                var p = $"@p{idx++}";
                setClauses.Add($"[{field.Column}] = {p}");
                cmd.Parameters.AddWithValue(p, (field.Value ?? string.Empty).Trim());
            }
            cmd.CommandText = $@"
UPDATE [{resource.Database}].[dbo].[{resource.Table}]
SET {string.Join(", ", setClauses)}
WHERE [CLIEN] = @clientId";
            cmd.Parameters.AddWithValue("@clientId", clientId);
            totalUpdated += await cmd.ExecuteNonQueryAsync();
        }
        return totalUpdated;
    }

    private static string NormalizeFactureType(string? raw)
    {
        var upper = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return upper is "F" or "D" or "I" ? upper : string.Empty;
    }

    private async Task<IReadOnlyList<FactureSummary>> GetClientFacturesAsync(string clientId, string factureType)
    {
        EnsureAllowedOnConfiguredDatabases("client factures", clientId, "FACTU", new[]
        {
            "CLE", "TYPE", "CLIEN", "NOM", "ADRES_1_", "ADRES_2_", "ADRES_3_", "TOHT", "TOTVA", "TOTTC"
        }, "read");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var resources = GetEnabledResourcesByTable("FACTU");
        if (resources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour FACTU.");

        var hasTypeFilter = !string.IsNullOrWhiteSpace(factureType);
        var items = new List<FactureSummary>();
        foreach (var resource in resources)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT
  [CLE], [TYPE], [CLIEN], [NOM], [ADRES_1_], [ADRES_2_], [ADRES_3_], [TOHT], [TOTVA], [TOTTC]
FROM [{resource.Database}].[dbo].[{resource.Table}]
WHERE [CLIEN] = @clientId{(hasTypeFilter ? " AND [TYPE] = @type" : string.Empty)}
ORDER BY [CLE] DESC";
            cmd.Parameters.AddWithValue("@clientId", clientId);
            if (hasTypeFilter)
                cmd.Parameters.AddWithValue("@type", factureType);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var line = new FactureSummary
                {
                    Id = DbToString(reader, "CLE"),
                    Type = DbToString(reader, "TYPE"),
                    ClientId = DbToString(reader, "CLIEN"),
                    Nom = DbToString(reader, "NOM"),
                    Adresse = string.Join(" ", new[]
                    {
                        DbToString(reader, "ADRES_1_"),
                        DbToString(reader, "ADRES_2_"),
                        DbToString(reader, "ADRES_3_")
                    }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    TotalHt = DbToString(reader, "TOHT"),
                    TotalTva = DbToString(reader, "TOTVA"),
                    TotalTtc = DbToString(reader, "TOTTC")
                };
                items.Add(line);
            }
        }

        return items;
    }

    private async Task<FactureDetail?> GetFactureSummaryAsync(string factureId)
    {
        EnsureAllowedOnConfiguredDatabases("facture summary", factureId, "FACTU", new[]
        {
            "CLE", "TYPE", "CLIEN", "NOM", "ADRES_1_", "ADRES_2_", "ADRES_3_", "TOHT", "TOTVA", "TOTTC"
        }, "read");
        EnsureAllowedOnConfiguredDatabases("facture summary", factureId, "CORFA", new[]
        {
            "CLE", "DESIG", "QUANT", "PRIBR", "REMIS", "PRINE", "PAYEU", "DATEF", "TATVA", "MONTA", "TTC"
        }, "read");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var factuResources = GetEnabledResourcesByTable("FACTU");
        var corfaResources = GetEnabledResourcesByTable("CORFA");
        if (factuResources.Count == 0 && corfaResources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour FACTU/CORFA.");
        foreach (var factu in factuResources)
        {
            var detail = await GetFactureHeaderAsync(conn, factu.Database, factu.Table, factureId);
            if (detail is null) continue;
            var corfa = corfaResources.FirstOrDefault(x => string.Equals(x.Database, factu.Database, StringComparison.OrdinalIgnoreCase));
            detail.Lignes = await GetFactureLinesAsync(conn, corfa?.Database ?? factu.Database, corfa?.Table ?? "CORFA", factureId);
            return detail;
        }
        return null;
    }

    private async Task<IReadOnlyList<AppareilSummary>> GetClientAppareilsAsync(string clientId)
    {
        EnsureAllowedOnConfiguredDatabases("client appareils", clientId, "APPAR", new[]
        {
            "APPAR", "INSTA", "EMPLA", "OBSER", "MARQU", "MODEL", "GENRE", "SERIE", "PUISS", "ENERG",
            "DEGAR", "FIGAR", "GARAN", "CONTR", "DAMES", "INTAL", "DUREE", "VENDE", "PRINC", "TARIF", "PRICO", "PARCO", "ORDR"
        }, "read");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var resources = GetEnabledResourcesByTable("APPAR");
        if (resources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour APPAR.");

        var items = new List<AppareilSummary>();
        foreach (var resource in resources)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT
  [APPAR], [INSTA], [EMPLA], [OBSER], [MARQU], [MODEL], [GENRE], [SERIE], [PUISS], [ENERG],
  [DEGAR], [FIGAR], [GARAN], [CONTR], [DAMES], [INTAL], [DUREE], [VENDE], [PRINC], [TARIF], [PRICO], [PARCO], [ORDR]
FROM [{resource.Database}].[dbo].[{resource.Table}]
WHERE [APPAR] = @clientId";
            cmd.Parameters.AddWithValue("@clientId", clientId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new AppareilSummary
                {
                    ClientId = DbToString(reader, "APPAR"),
                    Installation = DbToString(reader, "INSTA"),
                    Emplacement = DbToString(reader, "EMPLA"),
                    Observation = DbToString(reader, "OBSER"),
                    Marque = DbToString(reader, "MARQU"),
                    Modele = DbToString(reader, "MODEL"),
                    Genre = DbToString(reader, "GENRE"),
                    Serie = DbToString(reader, "SERIE"),
                    Puissance = DbToString(reader, "PUISS"),
                    Energie = DbToString(reader, "ENERG"),
                    DateDebutGarantie = DbToString(reader, "DEGAR"),
                    DateFinGarantie = DbToString(reader, "FIGAR"),
                    DureeGarantie = DbToString(reader, "GARAN"),
                    SousContrat = DbToString(reader, "CONTR"),
                    DateMiseEnService = DbToString(reader, "DAMES"),
                    Installateur = DbToString(reader, "INTAL"),
                    DureeEntretien = DbToString(reader, "DUREE"),
                    Vendeur = DbToString(reader, "VENDE"),
                    Principal = DbToString(reader, "PRINC"),
                    Tarif = DbToString(reader, "TARIF"),
                    PrixContratHt = DbToString(reader, "PRICO"),
                    Parc = DbToString(reader, "PARCO"),
                    OrdreClassement = DbToString(reader, "ORDR")
                });
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<ReglementSummary>> GetClientReglementsAsync(string clientId)
    {
        EnsureAllowedOnConfiguredDatabases("client reglements", clientId, "HREGL", new[]
        {
            "PAYEU", "DATRE", "VERSE", "MONNA", "TATVA", "BANQUE", "CHEQUE", "VILLE",
            "VERSA", "REMISE", "DATEREMISE", "INCIDENT", "DATINCIDENT"
        }, "read");

        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.SqlConnectionString))
            throw new InvalidOperationException("SQL connection string is missing.");

        await using var conn = new SqlConnection(settings.SqlConnectionString);
        await conn.OpenAsync();

        var resources = GetEnabledResourcesByTable("HREGL");
        if (resources.Count == 0)
            throw new InvalidOperationException("Base de données cible non définie pour HREGL.");

        var items = new List<ReglementSummary>();
        var operaAllowed = resources.Any(r => r.Columns.Any(c =>
            string.Equals(c.Name, "OPERA", StringComparison.OrdinalIgnoreCase) &&
            c.Rights.Any(x => string.Equals(x, "read", StringComparison.OrdinalIgnoreCase))));

        foreach (var resource in resources)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT
  [PAYEU], [DATRE], [VERSE], [MONNA], [TATVA], [BANQUE], [CHEQUE], [VILLE],
  [VERSA], [REMISE], [DATEREMISE], [INCIDENT], [DATINCIDENT]{(operaAllowed ? ", [OPERA]" : string.Empty)}
FROM [{resource.Database}].[dbo].[{resource.Table}]
WHERE [PAYEU] = @clientId
ORDER BY [DATRE] DESC";
            cmd.Parameters.AddWithValue("@clientId", clientId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(new ReglementSummary
                {
                    ClientId = DbToString(reader, "PAYEU"),
                    DateReglement = DbToString(reader, "DATRE"),
                    Versement = DbToString(reader, "VERSE"),
                    Monnaie = DbToString(reader, "MONNA"),
                    TauxTva = DbToString(reader, "TATVA"),
                    Banque = DbToString(reader, "BANQUE"),
                    Cheque = DbToString(reader, "CHEQUE"),
                    Ville = DbToString(reader, "VILLE"),
                    VersementAutreMonnaie = DbToString(reader, "VERSA"),
                    Remise = DbToString(reader, "REMISE"),
                    DateRemise = DbToString(reader, "DATEREMISE"),
                    Incident = DbToString(reader, "INCIDENT"),
                    DateIncident = DbToString(reader, "DATINCIDENT"),
                    Operateur = operaAllowed ? DbToString(reader, "OPERA") : string.Empty
                });
            }
        }

        return items;
    }

    private static async Task<FactureDetail?> GetFactureHeaderAsync(SqlConnection conn, string dbName, string tableName, string factureId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT TOP 1
  [CLE], [TYPE], [CLIEN], [NOM], [ADRES_1_], [ADRES_2_], [ADRES_3_], [TOHT], [TOTVA], [TOTTC]
FROM [{dbName}].[dbo].[{tableName}]
WHERE [CLE] = @factureId";
        cmd.Parameters.AddWithValue("@factureId", factureId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new FactureDetail
        {
            Id = DbToString(reader, "CLE"),
            Type = DbToString(reader, "TYPE"),
            ClientId = DbToString(reader, "CLIEN"),
            Nom = DbToString(reader, "NOM"),
            Adresse = string.Join(" ", new[]
            {
                DbToString(reader, "ADRES_1_"),
                DbToString(reader, "ADRES_2_"),
                DbToString(reader, "ADRES_3_")
            }.Where(s => !string.IsNullOrWhiteSpace(s))),
            TotalHt = DbToString(reader, "TOHT"),
            TotalTva = DbToString(reader, "TOTVA"),
            TotalTtc = DbToString(reader, "TOTTC"),
            Lignes = []
        };
    }

    private static async Task<List<FactureLine>> GetFactureLinesAsync(SqlConnection conn, string dbName, string tableName, string factureId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT
  [CLE], [DESIG], [QUANT], [PRIBR], [REMIS], [PRINE], [PAYEU], [DATEF], [TATVA], [MONTA], [TTC]
FROM [{dbName}].[dbo].[{tableName}]
WHERE [CLE] = @factureId";
        cmd.Parameters.AddWithValue("@factureId", factureId);

        var lines = new List<FactureLine>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(new FactureLine
            {
                FactureId = DbToString(reader, "CLE"),
                Designation = DbToString(reader, "DESIG"),
                Quantite = DbToString(reader, "QUANT"),
                PrixBrut = DbToString(reader, "PRIBR"),
                Remise = DbToString(reader, "REMIS"),
                PrixNet = DbToString(reader, "PRINE"),
                PayeurId = DbToString(reader, "PAYEU"),
                DateFacture = DbToString(reader, "DATEF"),
                TauxTva = DbToString(reader, "TATVA"),
                Montant = DbToString(reader, "MONTA"),
                TotalTtc = DbToString(reader, "TTC")
            });
        }

        return lines;
    }

    private static string DbToString(SqlDataReader reader, string column)
    {
        var value = reader[column];
        if (value is null || value is DBNull) return string.Empty;
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
        return Convert.ToString(value) ?? string.Empty;
    }

    private List<FeatureResource> GetEnabledResourcesByTable(string table)
    {
        return _catalog
            .Where(f => _enabledFeatures.Contains(f.Code))
            .SelectMany(f => f.Resources.Where(r =>
                string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase) &&
                IsDatabasePresentOnServer(r.Database) &&
                IsResourceAllowedForFeature(f.Code, r.Database)))
            .GroupBy(r => $"{r.Database}|{r.Table}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private bool IsResourceAllowedForFeature(string featureCode, string databaseName)
    {
        if (!_featureFolderSelections.TryGetValue(featureCode, out var selectedFolders) || selectedFolders.Count == 0)
            return true;
        var folder = ExtractFolderFromDatabase(databaseName);
        if (string.IsNullOrWhiteSpace(folder)) return true;
        return selectedFolders.Contains(folder);
    }

    private static string ExtractFolderFromDatabase(string databaseName)
    {
        var raw = (databaseName ?? string.Empty).Trim();
        var idx = raw.IndexOf('_');
        if (idx < 0 || idx == raw.Length - 1) return string.Empty;
        return raw[(idx + 1)..].Trim().ToUpperInvariant();
    }

    private void EnsureAllowedOnConfiguredDatabases(string actionName, string id, string table, IEnumerable<string> columns, string action)
    {
        var resources = GetEnabledResourcesByTable(table);
        if (resources.Count == 0)
            throw new UnauthorizedAccessException($"Access denied by policy: no_resource_for_table_{table}");

        foreach (var column in columns)
        {
            var allowedForColumn = resources.Any(resource =>
            {
                var decision = IsAllowed(new AccessRequest
                {
                    Database = resource.Database,
                    Table = table,
                    Column = column,
                    Action = action
                });
                return decision.Allowed;
            });
            if (!allowedForColumn)
            {
                _logger.LogWarning("Denied {ActionName} query for {Id}: not_exposed_{Table}.{Column}", actionName, id, table, column);
                throw new UnauthorizedAccessException($"Access denied by policy: not_exposed_{table}.{column}");
            }
        }
    }

    private void EnsureAllowed(string actionName, string id, IEnumerable<AccessRequest> checks)
    {
        foreach (var check in checks)
        {
            var allowed = IsAllowed(check);
            if (!allowed.Allowed)
            {
                _logger.LogWarning("Denied {ActionName} query for {Id}: {Reason}", actionName, id, allowed.Reason);
                throw new UnauthorizedAccessException($"Access denied by policy: {allowed.Reason}");
            }
        }
    }

    private void EmitClientRequestLog(
        HttpListenerRequest req,
        int statusCode,
        string message,
        string? requestContent = null,
        string? responseContent = null,
        string? errorContent = null
    )
    {
        ClientRequestLogged?.Invoke(new ClientRequestLogEntry
        {
            Timestamp = DateTime.Now,
            Method = req.HttpMethod,
            Path = req.Url?.AbsolutePath ?? "/",
            Query = req.Url?.Query ?? string.Empty,
            StatusCode = statusCode,
            Message = message,
            RequestContent = requestContent ?? string.Empty,
            ResponseContent = responseContent ?? string.Empty,
            ErrorContent = errorContent ?? string.Empty
        });
    }
}

public sealed class AccessRequest
{
    public string? Database { get; set; }
    public string? Table { get; set; }
    public string? Column { get; set; }
    public string? Action { get; set; }
}

public sealed record AccessDecision(bool Allowed, string Reason, string? FeatureCode)
{
    public static AccessDecision Allow(string featureCode) => new(true, "allowed", featureCode);
    public static AccessDecision Deny(string reason) => new(false, reason, null);
}

public sealed class ClientSummary
{
    public string ClientId { get; set; } = string.Empty;
    public string Prenom { get; set; } = string.Empty;
    public string Nom { get; set; } = string.Empty;
    public string TelephoneDomicile { get; set; } = string.Empty;
    public string TelephonePortable { get; set; } = string.Empty;
    public string TelephoneTravail { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool SousContrat { get; set; }
    public string NumeroRue { get; set; } = string.Empty;
    public string QualiteAdresse { get; set; } = string.Empty;
    public string Rue { get; set; } = string.Empty;
    public string Ville { get; set; } = string.Empty;
    public string CodePostal { get; set; } = string.Empty;
    public string Renouvellement { get; set; } = string.Empty;
}

public class FactureSummary
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string Nom { get; set; } = string.Empty;
    public string Adresse { get; set; } = string.Empty;
    public string TotalHt { get; set; } = string.Empty;
    public string TotalTva { get; set; } = string.Empty;
    public string TotalTtc { get; set; } = string.Empty;
}

public sealed class FactureLine
{
    public string FactureId { get; set; } = string.Empty;
    public string Designation { get; set; } = string.Empty;
    public string Quantite { get; set; } = string.Empty;
    public string PrixBrut { get; set; } = string.Empty;
    public string Remise { get; set; } = string.Empty;
    public string PrixNet { get; set; } = string.Empty;
    public string PayeurId { get; set; } = string.Empty;
    public string DateFacture { get; set; } = string.Empty;
    public string TauxTva { get; set; } = string.Empty;
    public string Montant { get; set; } = string.Empty;
    public string TotalTtc { get; set; } = string.Empty;
}

public sealed class FactureDetail : FactureSummary
{
    public List<FactureLine> Lignes { get; set; } = [];
}

public sealed class AppareilSummary
{
    public string ClientId { get; set; } = string.Empty;
    public string Installation { get; set; } = string.Empty;
    public string Emplacement { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public string Marque { get; set; } = string.Empty;
    public string Modele { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Serie { get; set; } = string.Empty;
    public string Puissance { get; set; } = string.Empty;
    public string Energie { get; set; } = string.Empty;
    public string DateDebutGarantie { get; set; } = string.Empty;
    public string DateFinGarantie { get; set; } = string.Empty;
    public string DureeGarantie { get; set; } = string.Empty;
    public string SousContrat { get; set; } = string.Empty;
    public string DateMiseEnService { get; set; } = string.Empty;
    public string Installateur { get; set; } = string.Empty;
    public string DureeEntretien { get; set; } = string.Empty;
    public string Vendeur { get; set; } = string.Empty;
    public string Principal { get; set; } = string.Empty;
    public string Tarif { get; set; } = string.Empty;
    public string PrixContratHt { get; set; } = string.Empty;
    public string Parc { get; set; } = string.Empty;
    public string OrdreClassement { get; set; } = string.Empty;
}

public sealed class ReglementSummary
{
    public string ClientId { get; set; } = string.Empty;
    public string DateReglement { get; set; } = string.Empty;
    public string Versement { get; set; } = string.Empty;
    public string Monnaie { get; set; } = string.Empty;
    public string TauxTva { get; set; } = string.Empty;
    public string Banque { get; set; } = string.Empty;
    public string Cheque { get; set; } = string.Empty;
    public string Ville { get; set; } = string.Empty;
    public string VersementAutreMonnaie { get; set; } = string.Empty;
    public string Remise { get; set; } = string.Empty;
    public string DateRemise { get; set; } = string.Empty;
    public string Incident { get; set; } = string.Empty;
    public string DateIncident { get; set; } = string.Empty;
    public string Operateur { get; set; } = string.Empty;
}

public sealed class ClientRequestLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RequestContent { get; set; } = string.Empty;
    public string ResponseContent { get; set; } = string.Empty;
    public string ErrorContent { get; set; } = string.Empty;
}

public sealed class ClientUpdateRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? TelephoneDomicile { get; set; }
    public string? TelephonePortable { get; set; }
    public string? TelephoneTravail { get; set; }
}

