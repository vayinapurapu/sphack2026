
using CsvHelper;
using CsvHelper.Configuration;
using ClosedXML.Excel;
using System.Globalization;

namespace FunctionApp;

public static class DataHelpers
{
    public static List<StatementRecord> LoadStatements(string folder)
    {
        var records = new List<StatementRecord>();
        if (!Directory.Exists(folder)) return records;
        var files = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            try
            {
                if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
                    {
                        PrepareHeaderForMatch = args => args.Header.Replace(" ", "_").ToLowerInvariant(),
                        MissingFieldFound = null,
                        BadDataFound = null,
                    };
                    using var reader = new StreamReader(file);
                    using var csv = new CsvReader(reader, cfg);
                    var dyn = csv.GetRecords<dynamic>();
                    foreach (var d in dyn)
                    {
                        var dict = (IDictionary<string, object>)d;
                        var rec = new StatementRecord
                        {
                            Date = DateOnly.FromDateTime(DateTime.Parse(dict["date"].ToString() ?? string.Empty)),
                            Bank = dict.TryGetValue("bank", out var bank) ? bank?.ToString() ?? string.Empty : string.Empty,
                            Account_Id = dict.TryGetValue("account_id", out var acc) ? acc?.ToString() ?? string.Empty : string.Empty,
                            Business_Unit = dict.TryGetValue("business_unit", out var bu) ? bu?.ToString() ?? string.Empty : string.Empty,
                            Currency = dict.TryGetValue("currency", out var cur) ? cur?.ToString() ?? string.Empty : string.Empty,
                            Opening_Balance = ToDouble(dict, "opening_balance"),
                            Closing_Balance = ToDouble(dict, "closing_balance"),
                            Inflow = ToDouble(dict, "inflow"),
                            Outflow = ToDouble(dict, "outflow"),
                        };
                        records.Add(rec);
                    }
                }
                else
                {
                    using var wb = new XLWorkbook(file);
                    var ws = wb.Worksheets.First();
                    var tbl = ws.RangeUsed().AsTable();
                    var headers = tbl.Fields.Select(f => f.Name).ToList();
                    foreach (var row in tbl.DataRange.Rows())
                    {
                        string Get(string name)
                        {
                            var idx = headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
                            if (idx < 0) return string.Empty;
                            return row.Cell(idx + 1).GetValue<string>();
                        }
                        double GetD(string name)
                        {
                            var v = Get(name);
                            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv)) return dv;
                            return 0d;
                        }
                        var rec = new StatementRecord
                        {
                            Date = DateOnly.FromDateTime(DateTime.Parse(Get("date"))),
                            Bank = Get("bank"),
                            Account_Id = Get("account_id"),
                            Business_Unit = Get("business_unit"),
                            Currency = Get("currency"),
                            Opening_Balance = GetD("opening_balance"),
                            Closing_Balance = GetD("closing_balance"),
                            Inflow = GetD("inflow"),
                            Outflow = GetD("outflow"),
                        };
                        records.Add(rec);
                    }
                }
            }
            catch { /* skip bad file */ }
        }
        return records;
    }

    private static double ToDouble(IDictionary<string, object> dict, string key)
    {
        if (dict.TryGetValue(key, out var v) && v != null)
        {
            if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return 0d;
    }
}
