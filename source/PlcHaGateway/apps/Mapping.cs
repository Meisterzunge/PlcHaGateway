using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class MappingAttribute : Attribute
{
    public MappingAttribute(EntityType entityType, params string[] supportedPlcTypes)
    {
        this.EntityType = entityType;
        this.EntityTypeName = entityType.GetAttribute().TypeName;
        this.SupportedPlcTypes = supportedPlcTypes
            .Select(t => t.ToPlcType())
            .ToArray();
    }


    public EntityType EntityType { get; }
    public string EntityTypeName { get; }
    public string[] SupportedPlcTypes { get; }
}

/// <summary>
/// Aggregates several <see cref="ISymbol">symbols</see> to be mapped for home assistant context.
/// </summary>
public class VirtualDevice : IEnumerable<ISymbol>
{
    public VirtualDevice(ISymbol symbol)
    {
        this.Symbol = symbol;
        this.Identifier = symbol.GetEntityPath();
        this.Name = "TODO"; // (BETA) ... TODO
        this.Model = "TODO"; // (BETA) ... TODO
        this.Manufacturer = "TODO"; // (BETA) ... TODO
        this.Version = 0.01; // (BETA) ... TODO

        this.Mappings = symbol.SubSymbols
            .Flatten()
            .OfMappedSymbols()
            .SelectWhereNotNull(s => MappingFactory.CreateMapping(s, this)!)
            .ToArray();
    }


    #region Properties.Management
    public ISymbol Symbol { get; }
    public IMapping[] Mappings { get; }
    #endregion
    #region Properties
    public string Identifier { get; }
    public string Name { get; }
    public string Model { get; }
    public string Manufacturer { get; }
    public double Version { get; }
    #endregion


    IEnumerator IEnumerable.GetEnumerator() => Mappings.GetEnumerator();
    public IEnumerator<ISymbol> GetEnumerator() => (IEnumerator<ISymbol>)Mappings.GetEnumerator();
}

public interface IMapping
{
    #region Properties.Management
    MappingAttribute Info { get; }
    VirtualDevice? Owner { get; }
    ISymbol Symbol { get; }
    string EntityId { get; }
    #endregion
    #region Properties
    string Name { get; }
    string? DeviceClass { get; }
    object? Value { get; }
    #endregion
}
public abstract class Mapping<T> : IMapping
    where T : IConvertible
{
    internal Mapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null)
    {
        var sym = (Symbol)symbol;
        var session = (AdsSession)sym.Connection!.Session!;

        this.Info = info;
        this.Owner = owner;
        this.Symbol = symbol;
        this.dataTypes = session.SymbolServer.DataTypes;
        this.EntityId = symbol.GetEntityPath();
        this.Name = GetMappingParameter(PlcMappingParameter.Name).Value;
        this.DeviceClass = TryGetMappingParameter(PlcMappingParameter.DeviceClass)?.Value;

        LogEvent.Gw.LogTrace($"Created mapping for '{symbol.InstancePath}'.");
    }


    #region Properties.Exceptions
    protected Exception SetValueNotSupported => throw new NotSupportedException($"Failed to set value of not supported entity type '{Info.EntityType}'!");
    #endregion
    #region Properties.Management
    public MappingAttribute Info { get; }
    public VirtualDevice? Owner { get; }
    public ISymbol Symbol { get; }
    public string EntityId { get; }
    #endregion
    #region Properties
    public string Name { get; }
    public string? DeviceClass { get; protected set; }
    object? IMapping.Value => this.Value;
    public T? Value
    {
        get => value;
        set => SetValue(value);
    }
    private T? value;
    #endregion


    #region Management
    protected IDataType GetDataType(PlcMappingParameter parameter)
    {
        var param = GetMappingParameter(parameter);
        return (GetDataType(param.Value));
    }
    protected IDataType GetDataType(string typeName)
    {
        var dataType = dataTypes.FirstOrDefault(dt => dt.Name.Equals(typeName));
        if (dataType is null)
            throw new NullReferenceException($"Declared datatype '{typeName}' not found in TwinCAT type system.");
        else
            return (dataType);
    }
    protected ITypeAttribute GetMappingParameter(PlcMappingParameter parameter)
    {
        var attr = Symbol.TryGetMappingParameterAttribute(parameter);
        if (attr is null)
            throw new NullReferenceException($"Mandatory mapping parameter '{parameter.GetAttribute().PlcAttribute}' not specified.");
        else
            return (attr);
    }
    protected ITypeAttribute? TryGetMappingParameter(PlcMappingParameter parameter) => Symbol.TryGetMappingParameterAttribute(parameter);

    public virtual void SetValue(T value)
    {
        this.value = value;
    }
    #endregion


    public override string ToString() => EntityId;


    private IDataTypeCollection<IDataType> dataTypes;
}
[Mapping(EntityType.Sensor, Tc3_MiniFrame.AnalogInput, Tc3_MiniFrame.AnalogOutput, Tc3_MiniFrame.AnalogValue)]
[Mapping(EntityType.Number, Tc3_MiniFrame.AnalogOperationalValue)]
public class AnalogMapping : Mapping<double>
{
    public AnalogMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }


    public override void SetValue(double value)
    {
        // (BETA) ... TODO
        /*
        switch (Info.EntityType)
        {
            /// <see href="??">
            case EntityType.Sensor: throw new NotImplementedException("TODO");
            /// <see href="https://www.home-assistant.io/integrations/input_number/#actions">
            case EntityType.InputNumber: Entity.CallService("set_value", new { value = value }); break;

            default: throw SetValueNotSupported;
        }
        */
        base.SetValue(value);
    }
}
[Mapping(EntityType.BinarySensor, Tc3_MiniFrame.BinaryInput, Tc3_MiniFrame.BinaryOutput, Tc3_MiniFrame.BinaryValue)]
[Mapping(EntityType.Switch, Tc3_MiniFrame.BinaryOperationalValue)]
public class BooleanMapping : Mapping<bool>
{
    public BooleanMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }


    public override void SetValue(bool value)
    {
        // (BETA) ... TODO
        /*
        switch (Info.EntityType)
        {
            /// <see href="??">
            case EntityType.BinarySensor: throw new NotImplementedException("TODO");
            /// <see href="https://www.home-assistant.io/integrations/input_boolean/#actions">
            case EntityType.InputBoolean: Entity.CallService((value == true) ? "turn_on" : "turn_off"); break;

            default: throw SetValueNotSupported;
        }
        */
        base.SetValue(value);
    }
}
[Mapping(EntityType.Sensor, Tc3_MiniFrame.MultistateValue)]
[Mapping(EntityType.Select, Tc3_MiniFrame.MultistateOperationalValue)]
public class MultistateMapping : Mapping<uint>
{
    public MultistateMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner)
    {
        if (info.EntityType == EntityType.Sensor)
        {
            /// Refer to <see cref="https://www.home-assistant.io/integrations/sensor.mqtt/#options"/> for details.
            if (DeviceClass is null)
                this.DeviceClass = "enum";
            else
                throw new ArgumentException($"Multistate mappings of type '{info.EntityType.GetDescription()}' must not declare a device class!");
        }

        var enumType = (IEnumType)GetDataType(PlcMappingParameter.Enum);
        this.Options = enumType.EnumValues
            .Where(v => char.IsUpper(v.Name.FirstOrDefault()))
            .ToDictionary(
                v => Convert.ToUInt32(v.Primitive),
                v => v.Name
            );

        // [Legacy] Match PLC enum against HASS enum entity:
        /*
        var attrOptions = (JsonElement)Entity.Attributes!["options"];
        this.Options = attrOptions
            .EnumerateArray()
            .Select(j => j.GetString()!)
            .ToDictionary(
                o => MatchEnumState(o, enumType),
                o => o
            );
        */
    }


    public IReadOnlyDictionary<uint, string> Options { get; }
    // (BETA) ... TODO
    //public uint? Value
    //{
    //    get => Options.GetKeyOf(Entity.State);
    //}
    public override void SetValue(uint value)
    {
        // (BETA) ... TODO
        /*
        switch (Info.EntityType)
        {
            /// <see href="??">
            case EntityType.Sensor: throw new NotImplementedException("TODO");
            /// <see href="https://www.home-assistant.io/integrations/input_select/#actions">
            case EntityType.InputMultistate: Entity.CallService("select_option", new { option = Options[value!.Value] }); break;

            default: throw SetValueNotSupported;
        }
        */
        base.SetValue(value);
    }


    #region Helper
    private uint MatchEnumState(string text, IEnumType enumType)
    {
        if (enumType.EnumValues.TryParse(text, out IEnumValue val))
            return (Convert.ToUInt32(val.Primitive));
        else
            throw new KeyNotFoundException($"State '{text}' did not match any declared members of type '{enumType.Name}'.");
    }
    #endregion
}

internal sealed class MappingFactory
{
    #region Constants
    static readonly IReadOnlyDictionary<Type, MappingAttribute[]> MappingTypes = UtilAssembly
        .GetDefinedTypesOf<MappingAttribute>()
        .ToDictionary(
            t => t,
            t => t.GetCustomAttributes<MappingAttribute>().ToArray()
        );
    static readonly IReadOnlyDictionary<string, (Type Type, MappingAttribute Info)> MappingInfo = MappingTypes
        .SelectMany(t => t.Value)
        .SelectMany(m => m.SupportedPlcTypes)
        .ToDictionary(
            pt => pt,
            pt => GetMappingInfo(pt)
    );
    #endregion


    public static VirtualDevice[] CreateDevices(IEnumerable<ISymbol> symbols) => symbols
        .Select(s => new VirtualDevice(s))
        .ToArray();
    //private static IMapping? CreateMapping(IMqttEntityManager entityManager, ISymbol symbol)
    //{
    //
    //    /*
    //            this.Devices = res.Symbols
    //                .Where(s => s.IsMapped())
    //                .ToDictionary(
    //                    s => new VirtualDevice(s),
    //                    s => s.SubSymbols.Flatten().ToArray()
    //                );
    //
    //            this.Members = symbol.SubSymbols
    //                .Flatten()
    //                .ToArray();
    //    */
    //}
    public static IMapping[] CreateMappings(IEnumerable<ISymbol> symbols) => symbols
        .Select(s => CreateMapping(s))
        .WhereNotNull()
        .ToArray();
    public static IMapping? CreateMapping(ISymbol symbol, VirtualDevice? device = null)
    {
        try
        {
            var gwType = symbol.GetGatewayDataType();
            if (gwType.Name == Tc3_MiniFrame.View.ToPlcType())
                return (null); // Skip (Not required as mapping target).
            else if (MappingInfo.TryGetValue(gwType.Name, out var mapping))
                return ((IMapping)Activator.CreateInstance(mapping.Type, [mapping.Info, symbol, device])!);
            else
                throw new NotSupportedException($"Datatype '{gwType.Name}' is not supported!");
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mapping for symbol '{0}'.", symbol.InstancePath);
        }
        return (null);
    }


    #region Helper
    private static (Type Type, MappingAttribute Info) GetMappingInfo(string plcType)
    {
        foreach (var info in MappingTypes)
        {
            foreach (var attrib in info.Value)
            {
                if (attrib.SupportedPlcTypes.Contains(plcType))
                    return (info.Key, attrib);
            }
        }
        throw new NullReferenceException($"Failed to determine mapping information for PLC type '{plcType}'!");
    }
    #endregion
}


internal static partial class Ext
{
    #region Constants
    static readonly string PlcMappingParameterAttribute = PlcMappingParameter.Mapping
        .GetAttribute().PlcAttribute
        .Split('.')
        .First();
    private static readonly IReadOnlyDictionary<string, PlcMappingParameter> PlcMappingAttributes = Enum
        .GetValues<PlcMappingParameter>()
        .ToDictionary(
            e => e.GetAttribute().PlcAttribute,
            e => e
        );
    #endregion


    public static string ToPlcType(this string source) => $"{Tc3_MiniFrame.LibraryName}.{source}";
    public static IEnumerable<ISymbol> OfMappedSymbols(this IEnumerable<ISymbol> source) => source.Where(IsMapped);
    public static bool IsMapped(this ISymbol source) => (source.TryGetMappingParameterAttribute() is not null);
    public static IEnumerable<ITypeAttribute> GetMappingParameterAttributes(this ISymbol source) => source.Attributes
        .Where(a => PlcMappingAttributes.ContainsKey(a.Name.ToLower()));
    public static ITypeAttribute? TryGetMappingParameterAttribute(this ISymbol source, PlcMappingParameter parameter = PlcMappingParameter.Mapping)
    {
        var attribName = parameter.GetAttribute().PlcAttribute;
        return (source
            .GetMappingParameterAttributes()
            .FirstOrDefault(a => a.Name.Equals(attribName, StringComparison.InvariantCultureIgnoreCase)));
    }

    /// <summary>
    /// Returns an enumeration of all parents and the instance self, known as <i>XPath</i>.
    /// </summary>
    public static IEnumerable<ISymbol> GetXPath(this ISymbol source) => getXPath(source).Reverse();
    private static IEnumerable<ISymbol> getXPath(this ISymbol source)
    {
        yield return (source);

        var parent = source.Parent;
        while (parent is not null)
        {
            yield return (parent);
            parent = parent.Parent;
        }
    }
    public static string GetEntityPath(this ISymbol source) => string.Join('_', source
        .GetXPath()
        .Select(s => s.GetInstanceEntityName()?.ToLower())
        .Where(s => !string.IsNullOrEmpty(s)));
    public static string? GetInstanceEntityName(this ISymbol source)
    {
        var attrib = source.TryGetMappingParameterAttribute();
        if (attrib is null)
            return (null);
        else if (string.IsNullOrEmpty(attrib.Value))
            return (source.InstanceName);
        else
            return (attrib.Value);
    }
    public static bool IsGatewayDataType(this ISymbol source, string expectedTypeName) => GetGatewayDataType(source).Name.Equals(expectedTypeName);
    public static IDataType GetGatewayDataType(this ISymbol source)
    {
        if (source.IsMiniFrameType())
            return (source.DataType);

        if (source.DataType is not IStructType structType)
            throw new NotSupportedException($"Failed to determine gateway datatype from symbol '{source.InstancePath}' of not non-structured datatype '{source.TypeName}'!");
        else if (!structType.BaseType.IsMiniFrameType())
            throw new NotSupportedException($"Failed to determine gateway datatype from symbol '{source.InstancePath}' of not supported datatype '{source.TypeName}'!");
        else
            return (structType.BaseType);
    }
}