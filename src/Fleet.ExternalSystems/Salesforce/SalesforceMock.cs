using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Core;
using Fleet.ExternalSystems.Infrastructure;

namespace Fleet.ExternalSystems.Salesforce;

/// <summary>
/// An in-memory Salesforce org that speaks a faithful subset of the REST API: OAuth client credentials,
/// SOQL query, sObject get/create/update, and upsert by external ID. The worker's SalesforceRestClient
/// works against this or a real org without code changes.
///
/// Objects: Equipment__c and Driver__c (custom, illustrative), WorkOrder and Case (standard).
/// </summary>
public class SalesforceStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<JsonObject>> objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> tokens = new();
    private readonly Dictionary<string, string> prefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Equipment__c"] = "a0E",
        ["Driver__c"] = "a0D",
        ["WorkOrder"] = "0WO",
        ["Case"] = "500",
    };

    private long idCounter = 100_000;
    private int workOrderNumber = 1000;
    private int caseNumber = 5000;
    private int nextDriverNumber = 2025;

    public SalesforceStore() => Seed();

    public CallLog Calls { get; } = new();

    public string IssueToken()
    {
        lock (gate)
        {
            var token = "00DDEMO!" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
            tokens[token] = DateTime.UtcNow.AddMinutes(20);
            return token;
        }
    }

    public bool IsValidToken(string? token)
    {
        lock (gate)
        {
            return token != null && tokens.TryGetValue(token, out var expires) && expires > DateTime.UtcNow;
        }
    }

    public void ExpireAllTokens()
    {
        lock (gate)
        {
            tokens.Clear();
        }
    }

    public List<JsonObject> All(string type)
    {
        lock (gate)
        {
            return objects.TryGetValue(type, out var list) ? list.Select(r => r.DeepClone().AsObject()).ToList() : new List<JsonObject>();
        }
    }

    public JsonObject? Get(string type, string id)
    {
        lock (gate)
        {
            return Find(type, "Id", id)?.DeepClone().AsObject();
        }
    }

    public JsonObject Create(string type, JsonObject fields)
    {
        lock (gate)
        {
            return CreateLocked(type, fields).DeepClone().AsObject();
        }
    }

    public bool Update(string type, string id, JsonObject fields)
    {
        lock (gate)
        {
            var record = Find(type, "Id", id);
            if (record == null)
            {
                return false;
            }

            Apply(record, fields);
            return true;
        }
    }

    public (JsonObject Record, bool Created) Upsert(string type, string externalField, string externalValue, JsonObject fields)
    {
        lock (gate)
        {
            var existing = Find(type, externalField, externalValue);
            if (existing != null)
            {
                Apply(existing, fields);
                return (existing.DeepClone().AsObject(), false);
            }

            var withKey = fields.DeepClone().AsObject();
            withKey[externalField] = externalValue;
            return (CreateLocked(type, withKey).DeepClone().AsObject(), true);
        }
    }

    public JsonObject HireDriver(string? name, string? homeHub)
    {
        lock (gate)
        {
            var id = $"D{nextDriverNumber++}";
            var fields = new JsonObject
            {
                ["Name"] = name ?? FleetSeed.NewHireNames[(nextDriverNumber - 2026) % FleetSeed.NewHireNames.Length],
                ["Driver_Id__c"] = id,
                ["Home_Terminal__c"] = homeHub ?? "OMA",
                ["Hire_Status__c"] = "Onboarding",
                ["Endorsements__c"] = "",
                ["Phone__c"] = $"(402) 555-0{nextDriverNumber % 1000:000}",
                ["Paired_Unit__c"] = "",
            };
            return CreateLocked("Driver__c", fields).DeepClone().AsObject();
        }
    }

    private JsonObject? Find(string type, string field, string value) =>
        objects.TryGetValue(type, out var list)
            ? list.FirstOrDefault(r => string.Equals(Text(r[field]), value, StringComparison.Ordinal))
            : null;

    private JsonObject CreateLocked(string type, JsonObject fields)
    {
        if (!objects.TryGetValue(type, out var list))
        {
            list = new List<JsonObject>();
            objects[type] = list;
        }

        var prefix = prefixes.TryGetValue(type, out var p) ? p : "a0X";
        var id = $"{prefix}{++idCounter:D12}AAA";
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff+0000", CultureInfo.InvariantCulture);
        var record = new JsonObject
        {
            ["attributes"] = new JsonObject
            {
                ["type"] = type,
                ["url"] = $"/services/data/v62.0/sobjects/{type}/{id}",
            },
            ["Id"] = id,
            ["CreatedDate"] = now,
            ["LastModifiedDate"] = now,
        };

        if (type.Equals("WorkOrder", StringComparison.OrdinalIgnoreCase))
        {
            record["WorkOrderNumber"] = (++workOrderNumber).ToString("D8", CultureInfo.InvariantCulture);
        }
        else if (type.Equals("Case", StringComparison.OrdinalIgnoreCase))
        {
            record["CaseNumber"] = (++caseNumber).ToString("D8", CultureInfo.InvariantCulture);
        }

        Apply(record, fields);
        list.Add(record);
        return record;
    }

    private static void Apply(JsonObject record, JsonObject fields)
    {
        foreach (var (key, value) in fields)
        {
            if (key is "Id" or "attributes" or "WorkOrderNumber" or "CaseNumber")
            {
                continue;
            }

            record[key] = value?.DeepClone();
        }

        record["LastModifiedDate"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff+0000", CultureInfo.InvariantCulture);
    }

    private void Seed()
    {
        foreach (var t in FleetSeed.Trucks)
        {
            CreateLocked("Equipment__c", new JsonObject
            {
                ["Name"] = $"Unit {t.Unit}",
                ["Unit_Number__c"] = t.Unit,
                ["Home_Terminal__c"] = t.HomeHub,
                ["Make__c"] = t.Make,
                ["Model__c"] = t.Model,
                ["Year__c"] = t.Year,
                ["VIN__c"] = t.Vin,
                ["Odometer__c"] = t.Odometer,
                ["PM_Due_Odometer__c"] = t.PmDueOdometer,
                ["Status__c"] = "Active",
            });
        }

        foreach (var d in FleetSeed.Drivers)
        {
            CreateLocked("Driver__c", new JsonObject
            {
                ["Name"] = d.Name,
                ["Driver_Id__c"] = d.DriverId,
                ["Home_Terminal__c"] = d.HomeHub,
                ["Hire_Status__c"] = d.Employment == DriverEmployment.Onboarding ? "Onboarding" : "Active",
                ["Endorsements__c"] = string.Join(";", d.Endorsements),
                ["Phone__c"] = d.Phone,
                ["Paired_Unit__c"] = d.PairedUnit ?? "",
            });
        }
    }

    public static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };
}

public class SoqlException : Exception
{
    public SoqlException(string message) : base(message) { }
}

/// <summary>A small SOQL interpreter: SELECT fields FROM object [WHERE a op b [AND ...]] [ORDER BY f [ASC|DESC]] [LIMIT n].</summary>
public static class Soql
{
    private static readonly Regex QueryPattern = new(
        @"^\s*SELECT\s+(?<fields>.+?)\s+FROM\s+(?<obj>\w+)(?:\s+WHERE\s+(?<where>.+?))?(?:\s+ORDER\s+BY\s+(?<order>\w+)(?:\s+(?<dir>ASC|DESC))?)?(?:\s+LIMIT\s+(?<limit>\d+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ConditionPattern = new(
        @"^\s*(?<field>\w+)\s*(?<op>!=|<>|>=|<=|=|>|<)\s*(?<value>'(?:[^'\\]|\\.)*'|\S+)\s*$",
        RegexOptions.IgnoreCase);

    public static (string Object, List<JsonObject> Records) Execute(string soql, SalesforceStore store)
    {
        var match = QueryPattern.Match(soql);
        if (!match.Success)
        {
            throw new SoqlException("unexpected token in query");
        }

        var obj = match.Groups["obj"].Value;
        var fields = match.Groups["fields"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var conditions = new List<(string Field, string Op, string? Value)>();
        if (match.Groups["where"].Success)
        {
            foreach (var part in Regex.Split(match.Groups["where"].Value, @"\s+AND\s+", RegexOptions.IgnoreCase))
            {
                var c = ConditionPattern.Match(part);
                if (!c.Success)
                {
                    throw new SoqlException($"unsupported condition: {part.Trim()}");
                }

                var raw = c.Groups["value"].Value;
                string? literal = raw.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : raw.StartsWith('\'') ? raw[1..^1].Replace("\\'", "'") : raw;
                conditions.Add((c.Groups["field"].Value, c.Groups["op"].Value, literal));
            }
        }

        IEnumerable<JsonObject> records = store.All(obj).Where(r => conditions.All(c => Matches(r, c.Field, c.Op, c.Value)));
        if (match.Groups["order"].Success)
        {
            var orderField = match.Groups["order"].Value;
            var descending = match.Groups["dir"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase);
            records = descending
                ? records.OrderByDescending(r => SalesforceStore.Text(r[orderField]), StringComparer.Ordinal)
                : records.OrderBy(r => SalesforceStore.Text(r[orderField]), StringComparer.Ordinal);
        }

        if (match.Groups["limit"].Success)
        {
            records = records.Take(int.Parse(match.Groups["limit"].Value, CultureInfo.InvariantCulture));
        }

        var projected = records.Select(r =>
        {
            var output = new JsonObject { ["attributes"] = r["attributes"]?.DeepClone() };
            foreach (var f in fields.Prepend("Id").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                output[f] = r[f]?.DeepClone();
            }

            return output;
        }).ToList();

        return (obj, projected);
    }

    private static bool Matches(JsonObject record, string field, string op, string? literal)
    {
        var actual = SalesforceStore.Text(record[field]);
        if (literal == null)
        {
            return op is "=" ? actual == null : actual != null;
        }

        int comparison;
        if (double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(literal, NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
        {
            comparison = a.CompareTo(b);
        }
        else if (DateTime.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var da) &&
                 DateTime.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var db))
        {
            comparison = da.CompareTo(db);
        }
        else
        {
            comparison = string.Compare(actual ?? "", literal, StringComparison.OrdinalIgnoreCase);
        }

        return op switch
        {
            "=" => comparison == 0,
            "!=" or "<>" => comparison != 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            _ => false,
        };
    }
}

public static class SalesforceEndpoints
{
    public static void MapSalesforce(this WebApplication app)
    {
        var store = app.Services.GetRequiredService<SalesforceStore>();
        var chaos = app.Services.GetRequiredService<ChaosState>();
        var log = app.Services.GetRequiredService<EventLog>();

        // OAuth 2.0 client credentials flow (real orgs require the My Domain URL for this flow).
        app.MapPost("/services/oauth2/token", async (HttpRequest request) =>
        {
            if (chaos.SalesforceOutage)
            {
                store.Calls.Add("POST", "/services/oauth2/token", 503, "outage");
                return Results.Json(new { error = "server_unavailable", error_description = "Service unavailable" }, statusCode: 503);
            }

            var form = await request.ReadFormAsync();
            if (form["grant_type"] != "client_credentials" || string.IsNullOrEmpty(form["client_id"]) || string.IsNullOrEmpty(form["client_secret"]))
            {
                store.Calls.Add("POST", "/services/oauth2/token", 400, "invalid_client");
                return Results.Json(new { error = "invalid_client", error_description = "invalid client credentials" }, statusCode: 400);
            }

            store.Calls.Add("POST", "/services/oauth2/token", 200, "token issued");
            var instanceUrl = $"{request.Scheme}://{request.Host}";
            return Results.Json(new
            {
                access_token = store.IssueToken(),
                instance_url = instanceUrl,
                token_type = "Bearer",
                issued_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            });
        });

        var data = app.MapGroup("/services/data/{version}");

        data.MapGet("/query", (string version, string q, HttpRequest request) =>
        {
            var blocked = Guard(request, store, chaos);
            if (blocked != null)
            {
                return blocked;
            }

            try
            {
                var (obj, records) = Soql.Execute(q, store);
                store.Calls.Add("GET", $"query: {q}", 200, $"{records.Count} {obj} rows");
                return Results.Json(new { totalSize = records.Count, done = true, records });
            }
            catch (SoqlException e)
            {
                store.Calls.Add("GET", $"query: {q}", 400, "MALFORMED_QUERY");
                return Error(400, "MALFORMED_QUERY", e.Message);
            }
        });

        data.MapGet("/sobjects/{type}/{id}", (string version, string type, string id, HttpRequest request) =>
        {
            var blocked = Guard(request, store, chaos);
            if (blocked != null)
            {
                return blocked;
            }

            var record = store.Get(type, id);
            store.Calls.Add("GET", $"{type}/{id}", record == null ? 404 : 200);
            return record == null ? Error(404, "NOT_FOUND", "The requested resource does not exist") : Results.Json(record);
        });

        data.MapPost("/sobjects/{type}", (string version, string type, JsonObject body, HttpRequest request) =>
        {
            var blocked = Guard(request, store, chaos);
            if (blocked != null)
            {
                return blocked;
            }

            var record = store.Create(type, body);
            store.Calls.Add("POST", type, 201, $"created {record["Id"]}");
            return Results.Json(new { id = SalesforceStore.Text(record["Id"]), success = true, errors = Array.Empty<object>() }, statusCode: 201);
        });

        data.MapPatch("/sobjects/{type}/{id}", (string version, string type, string id, JsonObject body, HttpRequest request) =>
        {
            var blocked = Guard(request, store, chaos);
            if (blocked != null)
            {
                return blocked;
            }

            var ok = store.Update(type, id, body);
            store.Calls.Add("PATCH", $"{type}/{id}", ok ? 204 : 404);
            return ok ? Results.NoContent() : Error(404, "NOT_FOUND", "The requested resource does not exist");
        });

        // Upsert by external ID. 201 when created, 200 when an existing record was updated.
        data.MapPatch("/sobjects/{type}/{field}/{value}", (string version, string type, string field, string value, JsonObject body, HttpRequest request) =>
        {
            var blocked = Guard(request, store, chaos);
            if (blocked != null)
            {
                return blocked;
            }

            var (record, created) = store.Upsert(type, field, value, body);
            var id = SalesforceStore.Text(record["Id"]);
            var status = SalesforceStore.Text(record["Status"]) ?? SalesforceStore.Text(record["Hire_Status__c"]);
            store.Calls.Add("PATCH", $"{type}/{field}/{value}", created ? 201 : 200, $"{(created ? "created" : "updated")} {id}{(status != null ? $" ({status})" : "")}");
            if (created)
            {
                log.Add("Salesforce", "info", $"{type} {SalesforceStore.Text(record["WorkOrderNumber"]) ?? SalesforceStore.Text(record["CaseNumber"]) ?? id} created via upsert on {field}", value);
            }

            return Results.Json(new { id, success = true, errors = Array.Empty<object>(), created }, statusCode: created ? 201 : 200);
        });

        // ---- Demo-only admin endpoints (not part of the Salesforce API) ----

        app.MapGet("/admin/salesforce/objects/{type}", (string type) =>
            Results.Json(store.All(type).OrderByDescending(r => SalesforceStore.Text(r["LastModifiedDate"]), StringComparer.Ordinal).Take(150)));

        app.MapGet("/admin/salesforce/calls", () => Results.Json(store.Calls.Recent()));

        app.MapPost("/admin/salesforce/expire-tokens", () =>
        {
            store.ExpireAllTokens();
            log.Add("Salesforce", "info", "All access tokens expired. Clients will re-authenticate on their next call.");
            return Results.Ok();
        });

        // Simulates HR creating a new driver record. Salesforce then emits a Change Data Capture event,
        // which this mock delivers to the fleet API webhook (in production: a Pub/Sub API subscriber).
        app.MapPost("/admin/salesforce/hire", async (HireRequest? body, IHttpClientFactory httpFactory, IConfiguration config) =>
        {
            var record = store.HireDriver(body?.Name, body?.HomeHub);
            var driverId = SalesforceStore.Text(record["Driver_Id__c"]) ?? "";
            log.Add("Salesforce", "info", $"New hire {SalesforceStore.Text(record["Name"])} ({driverId}) created with Hire_Status__c = Onboarding.");

            var cdc = record.DeepClone().AsObject();
            cdc["ChangeEventHeader"] = new JsonObject
            {
                ["entityName"] = "Driver__c",
                ["changeType"] = "CREATE",
                ["recordIds"] = new JsonArray(SalesforceStore.Text(record["Id"])),
                ["commitTimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            var apiBase = config["Api:BaseUrl"] ?? "http://localhost:5100";
            try
            {
                using var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var response = await http.PostAsync(
                    $"{apiBase}/api/integrations/salesforce/events",
                    new StringContent(cdc.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
                log.Add("Salesforce", response.IsSuccessStatusCode ? "info" : "warn",
                    $"Change event for {driverId} delivered to the fleet API ({(int)response.StatusCode}).");
            }
            catch (Exception e)
            {
                log.Add("Salesforce", "warn", $"Change event for {driverId} not delivered ({e.Message}). The scheduled sync will pick it up.");
            }

            return Results.Json(record);
        });
    }

    public record HireRequest(string? Name, string? HomeHub);

    private static IResult? Guard(HttpRequest request, SalesforceStore store, ChaosState chaos)
    {
        var path = request.Path.Value ?? "";
        var shortPath = path.Contains("/sobjects/") ? path[(path.IndexOf("/sobjects/", StringComparison.Ordinal) + 10)..] : "query";
        if (chaos.SalesforceOutage)
        {
            store.Calls.Add(request.Method, shortPath, 503, "SERVER_UNAVAILABLE");
            return Error(503, "SERVER_UNAVAILABLE", "Service temporarily unavailable");
        }

        if (chaos.SalesforceRateLimit)
        {
            store.Calls.Add(request.Method, shortPath, 403, "REQUEST_LIMIT_EXCEEDED");
            return Error(403, "REQUEST_LIMIT_EXCEEDED", "TotalRequests Limit exceeded.");
        }

        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        if (!store.IsValidToken(token))
        {
            store.Calls.Add(request.Method, shortPath, 401, "INVALID_SESSION_ID");
            return Error(401, "INVALID_SESSION_ID", "Session expired or invalid");
        }

        return null;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new[] { new { message, errorCode = code } }, statusCode: status);
}
