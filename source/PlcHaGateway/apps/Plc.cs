using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HassModel;
using Microsoft.Extensions.Configuration;
using TwinCAT;
using TwinCAT.Ads;
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

internal class Tc3_MiniFrame
{
    #region Constants
    public const string LibraryName = "Tc3_MiniFrame";
    public const string FunctionBlockPrefix = "FB_Mfr";
    
    public const string AnalogInput = $"{FunctionBlockPrefix}_AI";
    public const string AnalogOutput = $"{FunctionBlockPrefix}_AO";
    public const string AnalogValue = $"{FunctionBlockPrefix}_AVal";
    public const string AnalogOperationalValue = $"{FunctionBlockPrefix}_AValOp";
    public const string BinaryInput = $"{FunctionBlockPrefix}_BI";
    public const string BinaryOutput = $"{FunctionBlockPrefix}_BO";
    public const string BinaryValue = $"{FunctionBlockPrefix}_BVal";
    public const string BinaryOperationalValue = $"{FunctionBlockPrefix}_BValOp";
    public const string MultistateValue = $"{FunctionBlockPrefix}_MVal";
    public const string MultistateOperationalValue = $"{FunctionBlockPrefix}_MValOp";
    public const string View = $"{FunctionBlockPrefix}_View";
    #endregion
}
internal class Plc : IDisposable
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
    #endregion


    #region Connection
    public async Task ConnectAsync(CancellationToken cancel)
    {
        // Establish connection:
        LogEvent.Ads.LogInformation("Establish connection.");
        await session.ConnectAsync(cancel).ConfigureAwait(false);

        // Load symbols:
        var symbolLoader = SymbolLoaderFactory.Create(session.Connection, SymbolLoaderSettings);
        var res = await symbolLoader.GetSymbolsAsync(CancellationToken.None).ConfigureAwait(false);
        res.ThrowOnError();

        // Load mapped symbols:
        var rootSymbols = res.Symbols
            .OfMappedSymbols()
            .ToList();
        this.MappedDevices = rootSymbols
            .PopWhere(s => s.IsGatewayDataType(Tc3_MiniFrame.View.ToPlcType()))
            .ToArray();
        this.MappedSymbols = rootSymbols.ToArray();
    }
    public void Dispose() => session.Dispose();
    #endregion


    private AdsSession session;
}


internal static partial class Ext
{
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

    public static bool IsMiniFrameType(this ISymbol source) => IsMiniFrameType(source.DataType);
    public static bool IsMiniFrameType(this IDataType source) => source.Name.StartsWith(Tc3_MiniFrame.LibraryName);

    public static MappingParameterAttribute GetAttribute(this PlcMappingParameter source) => source.GetCustomAttribute<MappingParameterAttribute, PlcMappingParameter>();
}