using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;


// ─── WeatherMode ──────────────────────────────────────────────────────────────
// Parsed from {attribute 'PlcHa.Weather' := '...'} value.
// Valid syntax:
//   now   → current weather from entity state/attributes  (FB_Mfr_WeatherNow only)
//   Nh    → hourly forecast ~N hours from now             (FB_Mfr_WeatherForecast only)
//   Nd    → daily  forecast ~N days  from now             (FB_Mfr_WeatherForecast only)
public abstract record WeatherMode
{
    public sealed record Now : WeatherMode;
    public sealed record HourlyOffset(int Hours) : WeatherMode;
    public sealed record DailyOffset(int Days)   : WeatherMode;

    private static readonly Regex HourlyRx = new(@"^(\d+)h$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DailyRx  = new(@"^(\d+)d$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string? raw, out WeatherMode mode)
    {
        mode = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var v = raw.Trim();

        if (v.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            mode = new Now();
            return true;
        }

        var hm = HourlyRx.Match(v);
        if (hm.Success && int.TryParse(hm.Groups[1].Value, out var h) && h > 0)
        {
            mode = new HourlyOffset(h);
            return true;
        }

        var dm = DailyRx.Match(v);
        if (dm.Success && int.TryParse(dm.Groups[1].Value, out var d) && d > 0)
        {
            mode = new DailyOffset(d);
            return true;
        }

        return false;
    }

    public override string ToString() => this switch
    {
        Now            => "now",
        HourlyOffset h => $"{h.Hours}h",
        DailyOffset  d => $"{d.Days}d",
        _              => "?"
    };
}


// ─── Field convention ─────────────────────────────────────────────────────────
internal enum WeatherFieldType
{
    Analog,
    Condition,
    WindBearing,
    Binary
}

// Describes one gateway→PLC write target inside an FB_Mfr_Weather symbol.
// SymbolName   : direct primitive field name on the weather FB (e.g. "fTemp")
// HaField      : key in the HA data dictionary; "_valid" is gateway-derived
// FieldType    : controls value conversion
internal record WeatherFieldDef(string SymbolName, string HaField, WeatherFieldType FieldType);


// ─── Interface ────────────────────────────────────────────────────────────────
public interface IWeatherBinding
{
    string EntityId { get; }
    string Name { get; }
    WeatherMode Mode { get; }
    VirtualDevice? Owner { get; }
    SymbolType SymbolType { get; }

    Task UpdateAsync(IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel);
}


// ─── Factory ──────────────────────────────────────────────────────────────────
internal static class WeatherBindingFactory
{
    public static IWeatherBinding? TryCreate(ISymbol symbol, VirtualDevice? owner)
    {
        if (!symbol.TryGetSymbolType(out var st, out _))
            return null;
        if (st != SymbolType.CurrentWeather && st != SymbolType.WeatherForecast)
            return null;

        try
        {
            return new WeatherBinding(symbol, st, owner);
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create weather binding for symbol '{0}'.", symbol.InstancePath);
            return null;
        }
    }
}


// ─── Implementation ───────────────────────────────────────────────────────────
internal sealed class WeatherBinding : IWeatherBinding
{
    // ── Field convention tables ───────────────────────────────────────────────
    private static readonly WeatherFieldDef[] CommonFields =
    [
        new("fTemp",    "temperature",               WeatherFieldType.Analog),
        new("eWthCond", "condition",                 WeatherFieldType.Condition),
        new("fWndSpd",  "wind_speed",                WeatherFieldType.Analog),
        new("eWndBrng", "wind_bearing",              WeatherFieldType.WindBearing),
        new("fPrec",    "precipitation",             WeatherFieldType.Analog),
        new("bValid",   "_valid",                    WeatherFieldType.Binary),  // gateway-derived
    ];

    private static readonly WeatherFieldDef[] CurrentOnlyFields =
    [
        new("fHmdt", "humidity",                    WeatherFieldType.Analog),
        new("fPres", "pressure",                    WeatherFieldType.Analog),
        new("fVis",  "visibility",                  WeatherFieldType.Analog),
        new("fCld",  "cloud_coverage",              WeatherFieldType.Analog),
    ];

    private static readonly WeatherFieldDef[] ForecastOnlyFields =
    [
        new("fTempLw",   "templow",                           WeatherFieldType.Analog),
        new("fPrecProb", "precipitation_probability",         WeatherFieldType.Analog),
        new("bDay",      "is_daytime",                        WeatherFieldType.Binary),
    ];

    // ── HA condition string → E_Mfr_WthCond uint value ───────────────────────
    private static readonly IReadOnlyDictionary<string, uint> ConditionMap =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["clear-night"] = 1,
            ["cloudy"] = 2,
            ["exceptional"] = 3,
            ["fog"] = 4,
            ["hail"] = 5,
            ["lightning"] = 6,
            ["lightning-rainy"] = 7,
            ["partlycloudy"] = 8,
            ["pouring"] = 9,
            ["rainy"] = 10,
            ["snowy"] = 11,
            ["snowy-rainy"] = 12,
            ["sunny"] = 13,
            ["windy"] = 14,
            ["windy-variant"] = 15,
        };

    // ── Cardinal string → E_Mfr_WndBrng uint value ───────────────────────────
    private static readonly IReadOnlyDictionary<string, uint> CardinalMap =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["N"]   = 1,  ["NNE"] = 2,  ["NE"]  = 3,  ["ENE"] = 4,
            ["E"]   = 5,  ["ESE"] = 6,  ["SE"]  = 7,  ["SSE"] = 8,
            ["S"]   = 9,  ["SSW"] = 10, ["SW"]  = 11, ["WSW"] = 12,
            ["W"]   = 13, ["WNW"] = 14, ["NW"]  = 15, ["NNW"] = 16,
        };

    // ── Instance state ────────────────────────────────────────────────────────
    public string EntityId { get; }
    public string Name { get; }
    public WeatherMode Mode { get; }
    public VirtualDevice? Owner { get; }
    public SymbolType SymbolType { get; }

    private readonly IAdsConnection connection;
    private readonly WeatherFieldDef[] fields;
    private readonly int vldIndex; // index of the "Vld" field
    private readonly SumSymbolWrite fieldWriteCmd;
    private readonly SumSymbolWrite? faultWriteCmd; // null if FltNtf not accessible
    private bool lastUpdateSucceeded = true; // edge-detect for fault notifications

    public WeatherBinding(ISymbol symbol, SymbolType symbolType, VirtualDevice? owner)
    {
        var sym = (Symbol)symbol;
        this.connection = (IAdsConnection)sym.Connection!;
        this.Owner      = owner;
        this.SymbolType = symbolType;
        this.Name       = symbol.GetEntityName(owner) ?? symbol.InstanceName;

        // EntityId: the PlcHa.Mapping value must be a fully-qualified weather entity id.
        var entityInfo = symbol.GetEntityInfo();
        if (entityInfo.Backend != IntegrationType.Native)
            throw new ArgumentException(
                $"PlcHa.Mapping on '{symbol.InstancePath}' must be a fully-qualified weather entity id " +
                "(e.g. weather.home). Relative paths are not supported for weather bindings.");
        this.EntityId = entityInfo.Path;

        // Mode: parsed from PlcHa.Weather attribute.
        var modeRaw = symbol.TryGetMappingParameterAttribute(PlcMappingParameter.WeatherMode)?.Value;
        if (!WeatherMode.TryParse(modeRaw, out var mode))
            throw new ArgumentException(
                $"Invalid or missing PlcHa.Weather value '{modeRaw}' on '{symbol.InstancePath}'. " +
                "Use 'now', 'Nh' (e.g. '2h') or 'Nd' (e.g. '1d').");
        this.Mode = mode;

        // Cross-validate: FB_Mfr_WeatherNow ↔ 'now' only; FB_Mfr_WeatherForecast ↔ Nh/Nd only.
        if (symbolType == SymbolType.CurrentWeather && this.Mode is not WeatherMode.Now)
            throw new ArgumentException(
            $"FB_Mfr_WeatherNow '{symbol.InstancePath}' only supports PlcHa.Weather = 'now'.");
        if (symbolType == SymbolType.WeatherForecast && this.Mode is WeatherMode.Now)
            throw new ArgumentException(
                $"FB_Mfr_WeatherForecast '{symbol.InstancePath}' requires PlcHa.Weather = 'Nh' (hourly) " +
                "or 'Nd' (daily), not 'now'.");

        // Build field list for this symbol type.
        this.fields = symbolType == SymbolType.CurrentWeather
            ? [.. CommonFields, .. CurrentOnlyFields]
            : [.. CommonFields, .. ForecastOnlyFields];

        this.vldIndex = Array.FindIndex(fields, f => f.HaField == "_valid");

        // Resolve ADS symbols for the field write command.
        var fieldSymbols = fields
            .Select(f => symbol.SubSymbols[f.SymbolName])
            .ToList();
        this.fieldWriteCmd = new SumSymbolWrite(connection, fieldSymbols);

        // Fault notification: optional — present when FltNtf sub-symbol is reachable.
        if (symbol.SubSymbols.TryGetInstance("FltNtf", out var fltNtf) && fltNtf is not null)
        {
            this.faultWriteCmd = new SumSymbolWrite(connection,
            [
                fltNtf.SubSymbols["sMessage"],
                fltNtf.SubSymbols["sTitle"],
                fltNtf.SubSymbols["eSeverity"],
                fltNtf.SubSymbols["bBusy"],
            ]);
        }

        LogEvent.Gw.LogTrace("Created weather binding '{0}' (mode={1}).", EntityId, Mode);
    }

    public async Task UpdateAsync(IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel)
    {
        try
        {
            IReadOnlyDictionary<string, object?> data = this.Mode switch
            {
                WeatherMode.Now            => FetchCurrent(ha),
                WeatherMode.HourlyOffset h => await FetchForecastAsync(runner, "hourly", h.Hours, false, cancel).ConfigureAwait(false),
                WeatherMode.DailyOffset  d => await FetchForecastAsync(runner, "daily",  d.Days,  true,  cancel).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unsupported weather mode '{Mode}'.")
            };

            var (values, valid) = MapValues(data);
            if (vldIndex >= 0)
                values[vldIndex] = valid;

            await fieldWriteCmd.WriteAsync(values!, cancel).ConfigureAwait(false);
            lastUpdateSucceeded = true;
            LogEvent.Gw.LogTrace("Updated weather binding '{0}' (mode={1}, valid={2}).", EntityId, Mode, valid);
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to update weather binding '{0}' (mode={1}).", EntityId, Mode);

            // Fire fault notification only on the first failure after a success (edge trigger).
            if (lastUpdateSucceeded)
                await TriggerFaultAsync(ex.Message, cancel).ConfigureAwait(false);

            lastUpdateSucceeded = false;
        }
    }

    // ── Data fetching ─────────────────────────────────────────────────────────

    private IReadOnlyDictionary<string, object?> FetchCurrent(IHaContext ha)
    {
        var entity = ha.GetAllEntities().FirstOrDefault(e =>
            e.EntityId.Equals(EntityId, StringComparison.OrdinalIgnoreCase));

        if (entity is null)
            throw new InvalidOperationException($"Weather entity '{EntityId}' not found in Home Assistant.");

        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["condition"] = entity.EntityState?.State
        };

        var attrs = entity.EntityState?.Attributes;
        if (attrs is not null)
        {
            foreach (var kv in attrs)
                data[kv.Key] = kv.Value is JsonElement je ? NormalizeElement(je) : kv.Value;
        }

        return data;
    }

    private async Task<IReadOnlyDictionary<string, object?>> FetchForecastAsync(
        IHomeAssistantRunner runner, string forecastType, int offset, bool isDailyOffset, CancellationToken cancel)
    {
        var conn = runner.CurrentConnection
            ?? throw new InvalidOperationException("No active Home Assistant connection.");

        var cmd    = new GetForecastsCommand(EntityId, forecastType);
        var result = await conn
            .SendCommandAndReturnResponseAsync<GetForecastsCommand, GetForecastsResult>(cmd, cancel)
            .ConfigureAwait(false);

        var forecasts = result?.Response?.GetValueOrDefault(EntityId)?.Forecast;
        if (forecasts is null || forecasts.Count == 0)
            throw new InvalidOperationException(
                $"No '{forecastType}' forecast data returned for '{EntityId}'. " +
                "The integration may not support this forecast type.");

        // Pick the slot whose datetime is closest to now + N hours (hourly) or noon of day N (daily).
        var targetUtc = isDailyOffset
            ? DateTime.UtcNow.Date.AddDays(offset).AddHours(12)
            : DateTime.UtcNow.AddHours(offset);

        var best = forecasts
            .Select(f => (Item: f, Dt: TryParseDateTime(f.Datetime)))
            .Where(t => t.Dt.HasValue)
            .OrderBy(t => Math.Abs((t.Dt!.Value - targetUtc).TotalSeconds))
            .FirstOrDefault();

        if (best.Item is null)
            throw new InvalidOperationException(
                $"Could not find a usable forecast slot for '{EntityId}' at offset {Mode}. " +
                "Check that the integration returns datetime values.");

        return best.Item.ToDataDictionary();
    }

    // ── Value mapping ─────────────────────────────────────────────────────────

    private (object?[] values, bool valid) MapValues(IReadOnlyDictionary<string, object?> data)
    {
        var values = new object?[fields.Length];
        var valid = true;

        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];

            if (f.HaField == "_valid")
            {
                values[i] = false;   // placeholder — filled after loop
                continue;
            }

            if (!data.TryGetValue(f.HaField, out var raw) || raw is null)
            {
                valid = false;
                values[i] = Neutral(f.FieldType);
                continue;
            }

            try
            {
                values[i] = f.FieldType switch
                {
                    WeatherFieldType.Analog => (object)Convert.ToSingle(raw, CultureInfo.InvariantCulture),
                    WeatherFieldType.Condition => MapCondition(raw.ToString()),
                    WeatherFieldType.WindBearing => MapWindBearing(raw),
                    WeatherFieldType.Binary => Convert.ToBoolean(raw),
                    _ => throw new NotSupportedException($"Unknown WeatherFieldType '{f.FieldType}'.")
                };
            }
            catch (Exception ex)
            {
                LogEvent.Gw.LogWarning(ex, "Failed to map weather field '{0}' for '{1}'. Using neutral value.", f.HaField, EntityId);
                valid = false;
                values[i] = Neutral(f.FieldType);
            }
        }

        return (values, valid);
    }

    private static object Neutral(WeatherFieldType ft) => ft switch
    {
        WeatherFieldType.Analog => 0.0f,
        WeatherFieldType.Condition => 0u,
        WeatherFieldType.WindBearing => 0u,
        WeatherFieldType.Binary => false,
        _ => 0
    };

    private static uint MapCondition(string? raw)
    {
        if (raw is null) return 0u;
        if (ConditionMap.TryGetValue(raw, out var v)) return v;
        LogEvent.Gw.LogWarning("Unknown HA weather condition '{0}' — mapping to Invalid.", raw);
        return 0u;
    }

    private uint MapWindBearing(object raw)
    {
        // HA provides wind_bearing as float degrees OR cardinal string, depending on integration.
        switch (raw)
        {
            case double d: return DegreesToCardinal(d);
            case float f: return DegreesToCardinal(f);
            case int i: return DegreesToCardinal(i);
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                return je.TryGetDouble(out var jd) ? DegreesToCardinal(jd) : 0u;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return CardinalStringToEnum(je.GetString() ?? "");
            case string s:
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
                    return DegreesToCardinal(deg);
                return CardinalStringToEnum(s);
        }

        LogEvent.Gw.LogWarning("Unexpected wind_bearing type '{0}' for '{1}'. Mapping to Invalid.",
            raw.GetType().Name, EntityId);
        return 0u;
    }

    // 16 cardinal directions, each 22.5°. idx 0 = N, result enum values 1..16.
    private static uint DegreesToCardinal(double degrees)
    {
        var idx = (uint)Math.Floor((degrees % 360.0 + 360.0 + 11.25) / 22.5) % 16;
        return idx + 1;
    }

    private uint CardinalStringToEnum(string s)
    {
        if (CardinalMap.TryGetValue(s.Trim(), out var v)) return v;
        LogEvent.Gw.LogWarning("Unknown wind bearing string '{0}' for '{1}'. Mapping to Invalid.", s, EntityId);
        return 0u;
    }

    // ── Fault notification ────────────────────────────────────────────────────

    private async Task TriggerFaultAsync(string errorMessage, CancellationToken cancel)
    {
        if (faultWriteCmd is null) return;
        try
        {
            var msg = errorMessage.Length > 255 ? errorMessage[..255] : errorMessage;
            var title = $"Weather ({Mode})";
            var sev = (uint)E_Mfr_NotifySeverity.Error;

            // Write sMessage, sTitle, eSeverity, bBusy=TRUE in one sum command.
            // The existing NotificationBinding for FltNtf will detect the bBusy rising edge
            // and fire the HA persistent notification automatically.
            await faultWriteCmd.WriteAsync(new object[] { msg, title, sev, true }, cancel)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogWarning(ex, "Failed to trigger fault notification for weather binding '{0}'.", EntityId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTime? TryParseDateTime(string? raw)
    {
        if (raw is null) return null;
        if (DateTimeOffset.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var dto))
            return dto.UtcDateTime;
        return null;
    }

    private static object? NormalizeElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetSingle(out var f) ? f : (object?)je.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => je.ToString()
    };
}


// ─── HA WebSocket: weather.get_forecasts ─────────────────────────────────────
// Modelled after GetNotificationsCommand in Events.cs.
// Calls the HA WebSocket 'call_service' with return_response=true.

internal static class ForecastItemExtensions
{
    public static IReadOnlyDictionary<string, object?> ToDataDictionary(this ForecastItem f)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (f.Temperature.HasValue)              d["temperature"]               = (float)f.Temperature.Value;
        if (f.TempLow.HasValue)                  d["templow"]                   = (float)f.TempLow.Value;
        if (f.Condition is not null)             d["condition"]                 = f.Condition;
        if (f.WindSpeed.HasValue)                d["wind_speed"]                = (float)f.WindSpeed.Value;
        if (f.WindBearing.HasValue)              d["wind_bearing"]              = (object)f.WindBearing.Value;
        if (f.Precipitation.HasValue)            d["precipitation"]             = (float)f.Precipitation.Value;
        if (f.PrecipitationProbability.HasValue) d["precipitation_probability"] = (float)f.PrecipitationProbability.Value;
        if (f.Humidity.HasValue)                 d["humidity"]                  = (float)f.Humidity.Value;
        if (f.Pressure.HasValue)                 d["pressure"]                  = (float)f.Pressure.Value;
        if (f.Visibility.HasValue)               d["visibility"]                = (float)f.Visibility.Value;
        if (f.CloudCoverage.HasValue)            d["cloud_coverage"]            = (float)f.CloudCoverage.Value;
        if (f.IsDaytime.HasValue)                d["is_daytime"]                = f.IsDaytime.Value;
        return d;
    }
}

internal sealed record GetForecastsCommand : CommandMessage
{
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "weather";

    [JsonPropertyName("service")]
    public string Service { get; init; } = "get_forecasts";

    [JsonPropertyName("target")]
    public object Target { get; init; }

    [JsonPropertyName("service_data")]
    public object ServiceData { get; init; }

    [JsonPropertyName("return_response")]
    public bool ReturnResponse { get; init; } = true;

    public GetForecastsCommand(string entityId, string forecastType)
    {
        Type = "call_service";
        Target = new { entity_id = entityId };
        ServiceData = new { type = forecastType };
    }
}

internal sealed record GetForecastsResult
{
    [JsonPropertyName("response")]
    public Dictionary<string, ForecastEntityResult>? Response { get; init; }
}

internal sealed record ForecastEntityResult
{
    [JsonPropertyName("forecast")]
    public List<ForecastItem>? Forecast { get; init; }
}

internal sealed record ForecastItem
{
    [JsonPropertyName("datetime")]
    public string? Datetime { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("templow")]
    public double? TempLow { get; init; }

    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    [JsonPropertyName("wind_speed")]
    public double? WindSpeed { get; init; }

    [JsonPropertyName("wind_bearing")]
    public JsonElement? WindBearing { get; init; }

    [JsonPropertyName("precipitation")]
    public double? Precipitation { get; init; }

    [JsonPropertyName("precipitation_probability")]
    public double? PrecipitationProbability { get; init; }

    [JsonPropertyName("humidity")]
    public double? Humidity { get; init; }

    [JsonPropertyName("pressure")]
    public double? Pressure { get; init; }

    [JsonPropertyName("visibility")]
    public double? Visibility { get; init; }

    [JsonPropertyName("cloud_coverage")]
    public double? CloudCoverage { get; init; }

    [JsonPropertyName("is_daytime")]
    public bool? IsDaytime { get; init; }
}
