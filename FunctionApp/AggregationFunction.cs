
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.IO;

namespace FunctionApp;

public class AggregationFunction
{
    private readonly ILogger _logger;
    private static readonly Dictionary<string,double> THRESHOLDS = new() { {"USD", 2500000}, {"CAD", 500000} };

    public AggregationFunction(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AggregationFunction>();
    }

    [Function("aggregate")]
    public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Function, "get", Route = "aggregate")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var asOfStr = query.Get("asOf");
        var lookbackStr = query.Get("lookbackDays");
        var lookbackDays =  int.TryParse(lookbackStr, out var lbd) ? lbd : 7;

        var asOf = !string.IsNullOrWhiteSpace(asOfStr) ? DateOnly.Parse(asOfStr) : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var dataFolder = Environment.GetEnvironmentVariable("DATA_FOLDER") ?? (OperatingSystem.IsWindows() ? @"D:\home\site\wwwroot\data" : "/home/site/wwwroot/data");

        var records = DataHelpers.LoadStatements(dataFolder);
        if (records.Count == 0)
        {
            var respEmpty = req.CreateResponse(System.Net.HttpStatusCode.OK);
            var empty = new AggregateResponse { AsOf = asOf.ToString("yyyy-MM-dd") };
            respEmpty.WriteString(JsonSerializer.Serialize(empty));
            return respEmpty;
        }

        // Filter windows
        var asOfDt = new DateTime(asOf.Year, asOf.Month, asOf.Day);
        var winStart = asOfDt.AddDays(-lookbackDays);
        var dfWin = records.Where(r => r.Date.ToDateTime(new TimeOnly(0,0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0,0)) >= winStart).ToList();
        var dfAsOf = records.Where(r => r.Date == asOf).ToList();
        if (dfAsOf.Count == 0)
        {
            var latest = records.Max(r => r.Date);
            asOf = latest;
            asOfDt = new DateTime(asOf.Year, asOf.Month, asOf.Day);
            dfAsOf = records.Where(r => r.Date == asOf).ToList();
            dfWin = records.Where(r => r.Date.ToDateTime(new TimeOnly(0,0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0,0)) >= asOfDt.AddDays(-lookbackDays)).ToList();
        }

        // rollup averages
        var roll = dfWin
            .GroupBy(r => new { r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new { g.Key.Business_Unit, g.Key.Bank, g.Key.Currency, avg7d = g.Average(x => x.Closing_Balance) })
            .ToDictionary(x => (x.Business_Unit, x.Bank, x.Currency), x => x.avg7d);

        var today = dfAsOf
            .GroupBy(r => new { r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new {
                g.Key.Business_Unit,
                g.Key.Bank,
                g.Key.Currency,
                closing = g.Sum(x => x.Closing_Balance),
                inflow = g.Sum(x => x.Inflow),
                outflow = g.Sum(x => x.Outflow)
            }).ToList();

        var summary = new List<SummaryRow>();
        foreach (var t in today)
        {
            var key = (t.Business_Unit, t.Bank, t.Currency);
            var avg = roll.ContainsKey(key) ? roll[key] : t.closing;
            var varPct = avg != 0 ? (t.closing - avg) / avg : 0d;
            var thresh = THRESHOLDS.TryGetValue(t.Currency.ToUpperInvariant(), out var th) ? th : 0d;
            summary.Add(new SummaryRow
            {
                BusinessUnit = t.Business_Unit,
                Bank = t.Bank,
                Currency = t.Currency,
                ClosingBalance = Math.Round(t.closing,2),
                Avg7d = Math.Round(avg,2),
                VariancePct = Math.Round(varPct,4),
                Threshold = thresh,
                Breach = t.closing < thresh
            });
        }

        var start3 = asOfDt.AddDays(-3);
        var details = records.Where(r => r.Date.ToDateTime(new TimeOnly(0,0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0,0)) >= start3)
            .GroupBy(r => new { r.Date, r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new DetailRow
            {
                Date = g.Key.Date.ToString("yyyy-MM-dd"),
                BusinessUnit = g.Key.Business_Unit,
                Bank = g.Key.Bank,
                Currency = g.Key.Currency,
                Inflow = Math.Round(g.Sum(x => x.Inflow),2),
                Outflow = Math.Round(g.Sum(x => x.Outflow),2),
                ClosingBalance = Math.Round(g.Sum(x => x.Closing_Balance),2)
            }).ToList();

        var response = new AggregateResponse
        {
            AsOf = asOf.ToString("yyyy-MM-dd"),
            Summary = summary,
            Details = details
        };

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "application/json");
        resp.WriteString(JsonSerializer.Serialize(response));
        return resp;
    }

    [Function("aggregatesp")]
    public async Task<HttpResponseData> RunSP([HttpTrigger(AuthorizationLevel.Function, "get", Route = "aggregatesp")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var asOfStr = query.Get("asOf");
        var lookbackStr = query.Get("lookbackDays");
        var lookbackDays = int.TryParse(lookbackStr, out var lbd) ? lbd : 7;

        var asOf = !string.IsNullOrWhiteSpace(asOfStr) ? DateOnly.Parse(asOfStr) : DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        var clientId = Environment.GetEnvironmentVariable("SP_CLIENT_ID");
        var tenantId = Environment.GetEnvironmentVariable("SP_TENANT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("SP_CLIENT_SECRET");
        var spHost = Environment.GetEnvironmentVariable("SP_HOSTNAME"); // e.g. contoso.sharepoint.com
        var sitePath = Environment.GetEnvironmentVariable("SP_SITE_PATH"); // e.g. /sites/mysite
        var folderPath = Environment.GetEnvironmentVariable("SP_FOLDER_PATH") ?? string.Empty; // path inside the drive

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(spHost) || string.IsNullOrWhiteSpace(sitePath))
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            err.WriteString("Missing SP_* environment variables (SP_CLIENT_ID, SP_TENANT_ID, SP_CLIENT_SECRET, SP_HOSTNAME, SP_SITE_PATH).");
            return err;
        }

        var dataFolder = Environment.GetEnvironmentVariable("DATA_FOLDER") ?? (OperatingSystem.IsWindows() ? @"D:\home\site\wwwroot\data" : "/home/site/wwwroot/data");
        var spCache = Path.Combine(dataFolder, "spcache");
        Directory.CreateDirectory(spCache);

        var app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithTenantId(tenantId)
            .Build();
        var auth = await app.AcquireTokenForClient(new[] { "https://graph.microsoft.com/.default" }).ExecuteAsync();
        var token = auth.AccessToken;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Build Graph endpoint to list files in the target folder
        var siteSegment = $"{spHost}:{(sitePath.StartsWith("/") ? sitePath : "/" + sitePath)}:";
        var childrenUrl = $"https://graph.microsoft.com/v1.0/sites/{siteSegment}/drive/root:/{(string.IsNullOrWhiteSpace(folderPath) ? string.Empty : folderPath)}:/children";
        childrenUrl = childrenUrl.Replace(":/drive/root:/:", ":/drive/root:").Replace(":/:", ":/");

        var listResp = await http.GetAsync(childrenUrl);
        if (!listResp.IsSuccessStatusCode)
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            err.WriteString($"Failed to list files from SharePoint: {await listResp.Content.ReadAsStringAsync()}");
            return err;
        }

        using var jdoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var root = jdoc.RootElement;
        if (root.TryGetProperty("value", out var val))
        {
            foreach (var item in val.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString() ?? string.Empty;
                if (!(name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))) continue;
                var downloadUrl = $"https://graph.microsoft.com/v1.0/sites/{siteSegment}/drive/root:/{(string.IsNullOrWhiteSpace(folderPath) ? string.Empty : folderPath + "/")}{Uri.EscapeDataString(name)}:/content";
                var dl = await http.GetAsync(downloadUrl);
                if (!dl.IsSuccessStatusCode) continue;
                var outPath = Path.Combine(spCache, name);
                using var fs = File.Create(outPath);
                await (await dl.Content.ReadAsStreamAsync()).CopyToAsync(fs);
            }
        }

        var records = DataHelpers.LoadStatements(spCache);
        if (records.Count == 0)
        {
            var respEmpty = req.CreateResponse(System.Net.HttpStatusCode.OK);
            var empty = new AggregateResponse { AsOf = asOf.ToString("yyyy-MM-dd") };
            respEmpty.WriteString(JsonSerializer.Serialize(empty));
            return respEmpty;
        }

        // Filter windows
        var asOfDt = new DateTime(asOf.Year, asOf.Month, asOf.Day);
        var winStart = asOfDt.AddDays(-lookbackDays);
        var dfWin = records.Where(r => r.Date.ToDateTime(new TimeOnly(0, 0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0, 0)) >= winStart).ToList();
        var dfAsOf = records.Where(r => r.Date == asOf).ToList();
        if (dfAsOf.Count == 0)
        {
            var latest = records.Max(r => r.Date);
            asOf = latest;
            asOfDt = new DateTime(asOf.Year, asOf.Month, asOf.Day);
            dfAsOf = records.Where(r => r.Date == asOf).ToList();
            dfWin = records.Where(r => r.Date.ToDateTime(new TimeOnly(0, 0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0, 0)) >= asOfDt.AddDays(-lookbackDays)).ToList();
        }

        var roll = dfWin
            .GroupBy(r => new { r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new { g.Key.Business_Unit, g.Key.Bank, g.Key.Currency, avg7d = g.Average(x => x.Closing_Balance) })
            .ToDictionary(x => (x.Business_Unit, x.Bank, x.Currency), x => x.avg7d);

        var today = dfAsOf
            .GroupBy(r => new { r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new {
                g.Key.Business_Unit,
                g.Key.Bank,
                g.Key.Currency,
                closing = g.Sum(x => x.Closing_Balance),
                inflow = g.Sum(x => x.Inflow),
                outflow = g.Sum(x => x.Outflow)
            }).ToList();

        var summary = new List<SummaryRow>();
        foreach (var t in today)
        {
            var key = (t.Business_Unit, t.Bank, t.Currency);
            var avg = roll.ContainsKey(key) ? roll[key] : t.closing;
            var varPct = avg != 0 ? (t.closing - avg) / avg : 0d;
            var thresh = THRESHOLDS.TryGetValue(t.Currency.ToUpperInvariant(), out var th) ? th : 0d;
            summary.Add(new SummaryRow
            {
                BusinessUnit = t.Business_Unit,
                Bank = t.Bank,
                Currency = t.Currency,
                ClosingBalance = Math.Round(t.closing, 2),
                Avg7d = Math.Round(avg, 2),
                VariancePct = Math.Round(varPct, 4),
                Threshold = thresh,
                Breach = t.closing < thresh
            });
        }

        var start3 = asOfDt.AddDays(-3);
        var details = records.Where(r => r.Date.ToDateTime(new TimeOnly(0, 0)) <= asOfDt && r.Date.ToDateTime(new TimeOnly(0, 0)) >= start3)
            .GroupBy(r => new { r.Date, r.Business_Unit, r.Bank, r.Currency })
            .Select(g => new DetailRow
            {
                Date = g.Key.Date.ToString("yyyy-MM-dd"),
                BusinessUnit = g.Key.Business_Unit,
                Bank = g.Key.Bank,
                Currency = g.Key.Currency,
                Inflow = Math.Round(g.Sum(x => x.Inflow), 2),
                Outflow = Math.Round(g.Sum(x => x.Outflow), 2),
                ClosingBalance = Math.Round(g.Sum(x => x.Closing_Balance), 2)
            }).ToList();

        var response = new AggregateResponse
        {
            AsOf = asOf.ToString("yyyy-MM-dd"),
            Summary = summary,
            Details = details
        };

        var respSP = req.CreateResponse(System.Net.HttpStatusCode.OK);
        respSP.Headers.Add("Content-Type", "application/json");
        respSP.WriteString(JsonSerializer.Serialize(response));
        return respSP;
    }
}
