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
    Enum
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
public enum FunctionBlock
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
    View
}

public class Tc3_MiniFrame
{
    #region Constants
    public const string LibraryName = "Tc3_MiniFrame";
    public const string FunctionBlockPrefix = "FB_Mfr";

    public static IReadOnlyDictionary<FunctionBlock, FunctionBlockAttribute> FunctionBlocks = UtilEnum.GetCustomAttributes<FunctionBlock, FunctionBlockAttribute>();
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
    public ISymbol[] MappedDevices { get; private set; }
    /// <summary>
    /// Mapped root symbols, <b>not</b> of type <c>view</c>.
    /// </summary>
    public ISymbol[] MappedSymbols { get; private set; }

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
        var rootSymbols = res.Symbols
            .WhereMapped()
            .ToList();
        this.MappedDevices = rootSymbols
            .PopWhere(s => s.IsFunctionBlock(FunctionBlock.View))
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
    private static readonly FunctionBlock[] PhysicalTypes = { FunctionBlock.AnalogInput, FunctionBlock.AnalogOutput, FunctionBlock.BinaryInput, FunctionBlock.BinaryOutput };
    private static readonly FunctionBlock[] InputTypes = { FunctionBlock.AnalogInput, FunctionBlock.BinaryInput };
    private static readonly FunctionBlock[] OutputTypes = { FunctionBlock.AnalogOutput, FunctionBlock.BinaryOutput };
    private static readonly FunctionBlock[] ValueTypes = { FunctionBlock.AnalogValue, FunctionBlock.BinaryValue, FunctionBlock.MultistateValue };
    private static readonly FunctionBlock[] OperationalTypes = { FunctionBlock.AnalogOperationalValue, FunctionBlock.BinaryOperationalValue, FunctionBlock.MultistateOperationalValue };
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

    public static bool IsPhysicalType(this FunctionBlock source) => PhysicalTypes.Contains(source);
    public static bool IsInputType(this FunctionBlock source) => InputTypes.Contains(source);
    public static bool IsOutputType(this FunctionBlock source) => OutputTypes.Contains(source);
    public static bool IsValueType(this FunctionBlock source) => ValueTypes.Contains(source);
    public static bool IsOperationalType(this FunctionBlock source) => OperationalTypes.Contains(source);
    public static string GetTypeName(this FunctionBlock source) => Tc3_MiniFrame.FunctionBlocks[source].TypeName;
    public static string ToPlcType(this FunctionBlock source) => Tc3_MiniFrame.FunctionBlocks[source].TypeName.ToPlcType();
    public static string ToPlcType(this string source) => $"{Tc3_MiniFrame.LibraryName}.{source}";

    public static bool IsMiniFrameType(this ISymbol source) => IsMiniFrameType(source.DataType);
    public static bool IsMiniFrameType(this IDataType source) => source.Name.StartsWith(Tc3_MiniFrame.LibraryName);

    public static MappingParameterAttribute GetAttribute(this PlcMappingParameter source) => source.GetCustomAttribute<MappingParameterAttribute, PlcMappingParameter>();

    internal static IEnumerable<ISymbol> GetTargetSymbols(this IEnumerable<IMapping> source, AdsCommandId command) => source.SelectMany(m => GetTargetSymbols(m, command));
    internal static IEnumerable<ISymbol> GetTargetSymbols(this IMapping source, AdsCommandId command)
    {
        // TODO: Method returns a single symol so far. But it is intent to return more than one symbol some day..

        var fb = source.Symbol.GetFunctionBlockType(out _);

        string? targetSymbol;
        switch (command)
        {
            case AdsCommandId.Read: targetSymbol = Tc3_MiniFrame.FunctionBlocks[fb].ReadSymbol; break;
            case AdsCommandId.Write: targetSymbol = Tc3_MiniFrame.FunctionBlocks[fb].WriteSymbol; break;

            default: throw new NotSupportedException($"Failed to determine symbols for not supported commands '{command}'!");
        }
        if (targetSymbol is null)
            yield break;
        else
            // Yield and associate target symbol:
            yield return (source.Symbol.SubSymbols[targetSymbol].Associate(source));
    }

    public static IEnumerable<IMapping> OfCyclicallyReadable(this IEnumerable<IMapping> source) => source.Where(IsCyclicallyReadable);
    public static bool IsCyclicallyReadable(this IMapping source)
    {
        if (source.Backend == IntegrationType.Native)
            return (source.FunctionBlockType.IsOperationalType());
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
}