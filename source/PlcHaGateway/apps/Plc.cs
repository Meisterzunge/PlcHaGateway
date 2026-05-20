using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HassModel;
using Microsoft.Extensions.Configuration;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;


[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class MappingParameterAttribute : Attribute
{
    public MappingParameterAttribute(string plcAttribute, string? mqttAttribute = null)
    {
        this.PlcAttribute = plcAttribute;
        this.MqttAttribute = mqttAttribute;
    }
    

    public string PlcAttribute { get; }
    public string? MqttAttribute { get; }
}
public enum PlcMappingParameter
{
    [MappingParameter("plcha.mapping")]
    Mapping,
    [MappingParameter("plcha.name")]
    Name,
    [MappingParameter("plcha.model")]
    Model,
    [MappingParameter("plcha.manufacturer")]
    Manufacturer,
    [MappingParameter("plcha.version")]
    Version,

    [MappingParameter("plcha.deviceclass")]
    DeviceClass,
    [MappingParameter("plcha.icon", "icon")]
    Icon,

    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/#unit_of_measurement">
    [MappingParameter("plcha.unit", "unit_of_measurement")]
    Unit,
    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/#step">
    [MappingParameter("plcha.step", "step")]
    Step,
    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/#min">
    [MappingParameter("plcha.min", "min")]
    Minimum,
    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/#max">
    [MappingParameter("plcha.max", "max")]
    Maximum,
    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/#mode">
    [MappingParameter("plcha.mode", "mode")]
    DisplayMode,

    /// <see href="https://www.home-assistant.io/integrations/select.mqtt/#options">
    [MappingParameter("plcha.enum", "options")]
    Enum,
    [MappingParameter("plcha.weather")]
    WeatherMode
}

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class FunctionBlockAttribute : Attribute
{
    public FunctionBlockAttribute(string suffix, string? readSymbol = null, string? writeSymbol = null)
    {
        this.TypeName = $"{Tc3_MiniFrame.FunctionBlockPrefix}_{suffix}";
        this.ReadSymbol = readSymbol;
        this.WriteSymbol = writeSymbol;
    }
    

    #region Properties.Management
    /// <summary>
    /// Sub symbol to target for read requests.
    /// </summary>
    public string? ReadSymbol { get; }
    /// <summary>
    /// Sub symbol to target for write requests.
    /// </summary>
    public string? WriteSymbol { get; }
    #endregion
    #region Properties
    public string TypeName { get; }
    #endregion
}
public enum SymbolType
{
    [FunctionBlock("AI", "fVal")]
    AnalogInput,
    [FunctionBlock("AO", "fVal", "fVal")]
    AnalogOutput,
    [FunctionBlock("AVal", "fVal", "fValue")]
    AnalogValue,
    [FunctionBlock("AValOp", "fVal", "fVal")]
    AnalogOperationalValue,
    [FunctionBlock("BI", "bVal")]
    BinaryInput,
    [FunctionBlock("BO", "bVal", "bVal")]
    BinaryOutput,
    [FunctionBlock("BVal", "bVal", "bValue")]
    BinaryValue,
    [FunctionBlock("BValOp", "bVal", "bVal")]
    BinaryOperationalValue,
    [FunctionBlock("MVal", "nVal", "nValue")]
    MultistateValue,
    [FunctionBlock("MValOp", "nVal", "nVal")]
    MultistateOperationalValue,
    [FunctionBlock("View")]
    View,

    [FunctionBlock("WeatherNow")]
    CurrentWeather,
    [FunctionBlock("WeatherForecast")]
    WeatherForecast,

    [FunctionBlock("Notification")]
    Notification,
    [FunctionBlock("Event")]
    Event,
    [FunctionBlock("Log")]
    Log,

    PrimitiveAnalog,
    PrimitiveBinary,
    PrimitiveMultistate
}

public class Tc3_MiniFrame
{
    #region Constants
    public const string LibraryName = "Tc3_MiniFrame";
    public const string FunctionBlockPrefix = "FB_Mfr";

    public static IReadOnlyDictionary<SymbolType, FunctionBlockAttribute> SymbolTypes = UtilEnum.GetCustomAttributes<SymbolType, FunctionBlockAttribute>();
    #endregion
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal class SymbolPathAttribute : Attribute
{
    public SymbolPathAttribute(string rootSymbol, params string[] subSymbols)
    {
        this.RootSymbol = rootSymbol;
        this.SubSymbols = subSymbols;
    }


    public string RootSymbol { get; }
    public string[]? SubSymbols { get; }
}

internal class Plc : IDisposable, IProjectInfo
{
    #region Constants
    static SessionSettings AdsSettings = SessionSettings.Default;
    static ISymbolLoaderSettings SymbolLoaderSettings = new SymbolLoaderSettings(SymbolsLoadMode.Flat);
    #endregion


    public Plc(AmsAddress address)
    {
        this.session = new AdsSession(address, AdsSettings);
    }


    #region Properties.Management
    /// <summary>
    /// Mapped root symbols, of type <c>view</c>.
    /// </summary>
    public ISymbol[] MappedDevices { get; private set; } = [];
    /// <summary>
    /// Mapped root symbols, <b>not</b> of type <c>view</c>.
    /// </summary>
    public ISymbol[] MappedSymbols { get; private set; } = [];

    [SymbolPath("TwinCAT_SystemInfoVarList._AppInfo", "ProjectName")]
    public string? ProjectName { get; private set; }
    [SymbolPath("Global_Version.", "sVersion")]
    public string? Version { get; private set; }
    #endregion


    #region Connection
    public async Task ConnectAsync(CancellationToken cancel)
    {
        // Establish connection:
        LogEvent.Ads.LogInformation("Establish connection.");
        await session.ConnectAsync(cancel).ConfigureAwait(false);

        // Load symbols:
        var symbolLoader = SymbolLoaderFactory.Create(session.Connection, SymbolLoaderSettings);
        var res = await symbolLoader.GetSymbolsAsync(cancel).ConfigureAwait(false);
        res.ThrowOnError();

        // Load static symbols:
        await ReadStaticSymbolsAsync(res, cancel).ConfigureAwait(false);

        // Load mapped symbols:
        var allSymbols = res.Symbols?.ToArray() ?? [];
        var rootSymbols = allSymbols
            .WhereMapped()
            .ToList();
        this.MappedDevices = rootSymbols
            .PopWhere(s => s.IsViewSymbolTypeOrSubclass())
            .Concat(allSymbols
                .Where(s => !s.IsMapped())
                .Where(s => s.IsViewSymbolTypeOrSubclass())
                .Where(s => s.HasSupportedMappedMembers()))
            .ToArray();
        this.MappedSymbols = rootSymbols.ToArray();
    }
    public void Dispose() => session.Dispose();
    #endregion
    #region Communication
    /// <summary>
    /// Determine and read any declared <see cref="SymbolPathAttribute">static symbol</see> values.
    /// </summary>
    private async Task ReadStaticSymbolsAsync(ResultSymbols symbols, CancellationToken cancel)
    {
        var staticSymbols = this.GetType()
            .GetPropertyMap<SymbolPathAttribute>()
            .ToDictionary(
                s => s.Key,
                s => TryGetSymbol(symbols, s.Value)
            );
        var staticRes = await new SumSymbolRead(session.Connection!, staticSymbols.Values.WhereNotNull().ToList())
            .Read2Async(cancel)
            .ConfigureAwait(false);
        if (staticRes.Succeeded)
        {
            // Assign read values to declared properties:
            staticRes.ValueResults!
                .ToDictionary(
                    r => staticSymbols.GetKeyOf(r.Source),
                    r => r.Value
                )
                .ForEach(r => r.Key.SetValue(this, r.Value));
        }
    }
    public SumSymbolRead CreateSymbolReadCommand(IEnumerable<IMapping> source) => new SumSymbolRead(session.Connection!, source
        .GetTargetSymbols(AdsCommandId.Read)
        .ToList(), SumAccessMode.IndexGroupIndexOffset, SumFallbackMode.All);
    public SumSymbolWrite CreateSymbolWriteCommand(IEnumerable<IMapping> source) => new SumSymbolWrite(session.Connection!, source
        .GetTargetSymbols(AdsCommandId.Write)
        .ToList());
    #endregion


    #region Helper
    private ISymbol? TryGetSymbol(ResultSymbols symbols, SymbolPathAttribute symbolPath)
    {
        var symbol = symbols.Symbols!.LastOrDefault(s => s.InstancePath.StartsWith(symbolPath.RootSymbol));
        foreach (var sub in symbolPath.SubSymbols)
            symbol?.SubSymbols.TryGetInstance(sub, out symbol);
        return (symbol);
    }
    #endregion


    private AdsSession session;
}

internal static partial class Ext
{
    #region Constants
    private static readonly SymbolType[] MiniFramePhysicalTypes = { SymbolType.AnalogInput, SymbolType.AnalogOutput, SymbolType.BinaryInput, SymbolType.BinaryOutput };
    private static readonly SymbolType[] MiniFrameInputTypes = { SymbolType.AnalogInput, SymbolType.BinaryInput };
    private static readonly SymbolType[] MiniFrameOutputTypes = { SymbolType.AnalogOutput, SymbolType.BinaryOutput };
    private static readonly SymbolType[] MiniFrameValueTypes = { SymbolType.AnalogValue, SymbolType.BinaryValue, SymbolType.MultistateValue };
    private static readonly SymbolType[] MiniFramePrimitiveValueTypes = { SymbolType.PrimitiveAnalog, SymbolType.PrimitiveBinary, SymbolType.PrimitiveMultistate };
    private static readonly SymbolType[] MiniFrameOperationalTypes = { SymbolType.AnalogOperationalValue, SymbolType.BinaryOperationalValue, SymbolType.MultistateOperationalValue };
    private static readonly SymbolType[] MiniFrameEventTypes = { SymbolType.Notification, SymbolType.Event, SymbolType.Log };
    private static readonly SymbolType[] WeatherTypes = { SymbolType.CurrentWeather, SymbolType.WeatherForecast };
    #endregion


    public static Plc CreatePlcFromSettings(this IConfiguration source) => new Plc(source.GetPlcNetId());
    public static AmsAddress GetPlcNetId(this IConfiguration source) => new AmsAddress(
        source.GetValue<string>("Plc:NetId")!,
        source.GetValue<int>("Plc:Port")
    );

    public static IEnumerable<ISymbol> Flatten(this ISymbol source) => source.SubSymbols
        .Flatten()
        .Prepend(source);
    public static IEnumerable<ISymbol> Flatten(this IEnumerable<ISymbol> source)
    {
        var subSymbols = source
            .Select(Flatten)
            .SelectMany(s => s);
        foreach (var sub in subSymbols)
            yield return (sub);
    }

    public static bool IsPhysicalType(this SymbolType source) => MiniFramePhysicalTypes.Contains(source);
    public static bool IsInputType(this SymbolType source) => MiniFrameInputTypes.Contains(source);
    public static bool IsOutputType(this SymbolType source) => MiniFrameOutputTypes.Contains(source);
    public static bool IsMiniFrameValueType(this SymbolType source) => MiniFrameValueTypes.Contains(source);
    public static bool IsPrimitiveValueType(this SymbolType source) => MiniFramePrimitiveValueTypes.Contains(source);
    public static bool IsOperationalType(this SymbolType source) => MiniFrameOperationalTypes.Contains(source);
    public static bool IsEventType(this SymbolType source) => MiniFrameEventTypes.Contains(source);
    public static bool IsWeatherType(this SymbolType source) => WeatherTypes.Contains(source);
    public static bool IsMiniFrameSymbolType(this SymbolType source) => Tc3_MiniFrame.SymbolTypes.ContainsKey(source);
    public static string GetTypeName(this SymbolType source)
    {
        if (Tc3_MiniFrame.SymbolTypes.TryGetValue(source, out var info))
            return (info.TypeName);

        throw new NotSupportedException($"Symbol type '{source}' does not define a MiniFrame function block type name.");
    }
    public static string ToPlcType(this SymbolType source)
    {
        if (Tc3_MiniFrame.SymbolTypes.TryGetValue(source, out var info))
            return (info.TypeName.ToPlcType());

        throw new NotSupportedException($"Symbol type '{source}' does not define a MiniFrame function block type.");
    }
    public static string ToPlcType(this string source) => $"{Tc3_MiniFrame.LibraryName}.{source}";

    public static bool IsMiniFrameType(this ISymbol source) => (source.DataType is not null) && IsMiniFrameType(source.DataType);
    public static bool IsMiniFrameType(this IDataType? source)
    {
        if (!string.IsNullOrWhiteSpace(source?.Name))
        {
            var parts = source.Name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Contains(Tc3_MiniFrame.LibraryName, StringComparer.InvariantCultureIgnoreCase))
                return (true);
            if (parts.Last().StartsWith($"{Tc3_MiniFrame.FunctionBlockPrefix}_", StringComparison.InvariantCultureIgnoreCase))
                return (true);
        }
        return (false);
    }
    public static MappingParameterAttribute GetAttribute(this PlcMappingParameter source) => source.GetCustomAttribute<MappingParameterAttribute, PlcMappingParameter>();

    internal static IEnumerable<ISymbol> GetTargetSymbols(this IEnumerable<IMapping> source, AdsCommandId command) => source.SelectMany(m => GetTargetSymbols(m, command));
    internal static IEnumerable<ISymbol> GetTargetSymbols(this IMapping source, AdsCommandId command)
    {
        if (source.Symbol.TryGetSymbolType(out var fb, out _))
        {
            string? targetSymbol;
            switch (command)
            {
                case AdsCommandId.Read: targetSymbol = Tc3_MiniFrame.SymbolTypes[fb].ReadSymbol; break;
                case AdsCommandId.Write: targetSymbol = Tc3_MiniFrame.SymbolTypes[fb].WriteSymbol; break;

                default: throw new NotSupportedException($"Failed to determine symbols for not supported commands '{command}'!");
            }
            if (targetSymbol is null)
                yield break;
            else
                // Yield and associate target symbol:
                yield return (source.Symbol.SubSymbols[targetSymbol].Associate(source));
        }
        else
            yield return (source.Symbol.Associate(source));
    }

    public static IEnumerable<IMapping> OfCyclicallyReadable(this IEnumerable<IMapping> source) => source.Where(IsCyclicallyReadable);
    public static bool IsCyclicallyReadable(this IMapping source)
    {
        if (source.Backend == IntegrationType.Native)
            return (source.SymbolType.IsOperationalType());
        else
            return (true);
    }

    internal static async Task<int> ReadMappingsAsync(this SumSymbolRead source, CancellationToken cancellationToken)
    {
        var read = await source.Read2Async(cancellationToken).ConfigureAwait(false);
        var changes = read.ValueResults
            .Where(r => r.Succeeded) // TODO: Consider failed requests by settings entities to unreliable.
            .Sum(r => r.Source
                .TryGetAssociatedMapping()!
                .SetValue(r.Value!, AutomationContext.Plc) ? 1 : 0);
        return (changes);
    }
    internal static async Task WriteMappingsAsync(this IEnumerable<IMapping> source, Plc plc, CancellationToken cancellationToken)
    {
        var mappings = source
            .Where(m => m.Value is not null)
            .ToDictionary(
                m => m,
                m => m.Value
            );
        var res = await plc
            .CreateSymbolWriteCommand(mappings.Keys)
            .WriteAsync(mappings.Values.ToArray()!, cancellationToken)
            .ConfigureAwait(false);

        mappings.Keys.ResetDirty();
    }

    internal static ISymbol Associate(this ISymbol source, IMapping mapping)
    {
        if (mapping is null)
            associatedPlcMappings.Remove(source);
        else
            associatedPlcMappings.AddOrUpdate(source, mapping);
        return (source);
    }
    public static IMapping? TryGetAssociatedMapping(this ISymbol source) => (associatedPlcMappings.TryGetValue(source, out var mapping) ? mapping : null);
    private static readonly Dictionary<ISymbol, IMapping> associatedPlcMappings = new();

    public static Dictionary<uint, string> GetFields(this IEnumType source) => source.EnumValues
        .OfValidFields()
        .ToDictionary(v => Convert.ToUInt32(v.Value), v => v.Name.TrimStart('e'));
    private static IEnumerable<IEnumValue> OfValidFields(this IEnumerable<IEnumValue> source)
    {
        var prefixed = source
            .Where(f => f.Name.StartsWith("e"))
            .ToArray();
        if (prefixed.IsEmpty())
            // No prefixed enum values, consider all fields as valid:
            return (source);
        else
            // Prefixed enum values exist, consider only prefixed fields as valid:
            return (prefixed);
    }
}