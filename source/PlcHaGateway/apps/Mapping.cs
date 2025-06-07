using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using NetDaemon.HassModel.Entities;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;

namespace HassModel;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class MappingAttribute : Attribute
{
    public MappingAttribute(EntityType entityType, params string[] supportedPlcTypes)
    {
        this.EntityType = entityType;
        this.EntityTypeName = entityType.GetDescription();
        this.SupportedPlcTypes = supportedPlcTypes
            .Select(t => t.ToPlcType())
            .ToArray();
    }


    public EntityType EntityType { get; }
    public string EntityTypeName { get; }
    public string[] SupportedPlcTypes { get; }
}
public enum PlcMappingParameter
{
    [Description("plcha.mapping")]
    Mapping,
    [Description("plcha.name")]
    Name,
    [Description("plcha.unit")]
    Unit,
    [Description("plcha.enum")]
    Enum
}
public enum EntityType
{
    [Description("sensor")]
    Sensor,
    [Description("binary_sensor")]
    BinarySensor,
    [Description("input_number")]
    InputNumber,
    [Description("input_boolean")]
    InputBoolean,
    [Description("input_select")]
    InputMultistate
}

public interface IMapping
{
    MappingAttribute Info { get; }
    ISymbol Symbol { get; }
    Entity Entity { get; }
}
public abstract class Mapping<T> : IMapping
    where T : Entity
{
    internal Mapping(MappingAttribute info, ISymbol symbol, T entity)
    {
        var sym = (Symbol)symbol;
        var session = (AdsSession)sym.Connection!.Session!;

        this.Info = info;
        this.Symbol = symbol;
        this.dataTypes = session.SymbolServer.DataTypes;
        this.Entity = entity;

        LogEvent.Gw.LogTrace($"Created mapping for '{symbol.InstancePath}'.");
    }


    #region Properties.Exceptions
    protected Exception SetValueNotSupported => throw new NotSupportedException($"Failed to set value of not supported entity type '{Info.EntityType}'!");
    #endregion
    #region Properties.Management
    public MappingAttribute Info { get; }
    public ISymbol Symbol { get; }
    Entity IMapping.Entity => Entity;
    public T Entity { get; }
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
            throw new NullReferenceException($"Mandatory mapping parameter '{parameter.GetDescription()}' not specified.");
        else
            return (attr);
    }
    #endregion


    private IDataTypeCollection<IDataType> dataTypes;
}
[Mapping(EntityType.Sensor, "FB_Mfr_AI", "FB_Mfr_AO", "FB_Mfr_AVal")]
[Mapping(EntityType.InputNumber, "FB_Mfr_AValOp")]
public class AnalogMapping : Mapping<NumericEntity>
{
    public AnalogMapping(MappingAttribute info, ISymbol symbol, Entity entity) : base(info, symbol, entity.AsNumeric()) { }


    public double? Value
    {
        get => Entity.State;
        set
        {
            switch (Info.EntityType)
            {
                /// <see href="??">
                case EntityType.Sensor: throw new NotImplementedException("TODO");
                /// <see href="https://www.home-assistant.io/integrations/input_number/#actions">
                case EntityType.InputNumber: Entity.CallService("set_value", new { value = value }); break;

                default: throw SetValueNotSupported;
            }
        }
    }
}
[Mapping(EntityType.BinarySensor, "FB_Mfr_BI", "FB_Mfr_BO", "FB_Mfr_BVal")]
[Mapping(EntityType.InputBoolean, "FB_Mfr_BValOp")]
public class BooleanMapping : Mapping<Entity>
{
    public BooleanMapping(MappingAttribute info, ISymbol symbol, Entity entity) : base(info, symbol, entity) { }


    public bool? Value
    {
        get => Entity.IsOn();
        set
        {
            switch (Info.EntityType)
            {
                /// <see href="??">
                case EntityType.BinarySensor: throw new NotImplementedException("TODO");
                /// <see href="https://www.home-assistant.io/integrations/input_boolean/#actions">
                case EntityType.InputBoolean: Entity.CallService((value == true) ? "turn_on" : "turn_off"); break;

                default: throw SetValueNotSupported;
            }
        }
    }
}
[Mapping(EntityType.Sensor, "FB_Mfr_MVal")]
[Mapping(EntityType.InputMultistate, "FB_Mfr_MValOp")]
public class MultistateMapping : Mapping<Entity>
{
    public MultistateMapping(MappingAttribute info, ISymbol symbol, Entity entity) : base(info, symbol, entity)
    {
        var enumType = (IEnumType)GetDataType(PlcMappingParameter.Enum);

        var attrOptions = (JsonElement)Entity.Attributes!["options"];
        this.Options = attrOptions
            .EnumerateArray()
            .Select(j => j.GetString()!)
            .ToDictionary(
                o => MatchEnumState(o, enumType),
                o => o
            );
    }


    IReadOnlyDictionary<uint, string> Options { get; }
    public uint? Value
    {
        get => Options.GetKeyOf(Entity.State);
        set
        {
            switch (Info.EntityType)
            {
                /// <see href="??">
                case EntityType.Sensor: throw new NotImplementedException("TODO");
                /// <see href="https://www.home-assistant.io/integrations/input_select/#actions">
                case EntityType.InputMultistate: Entity.CallService("select_option", new { option = Options[value!.Value] }); break;

                default: throw SetValueNotSupported;
            }
        }
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
    static readonly string PlcType_View = "FB_Mfr_View".ToPlcType();

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


    public static IMapping[] CreateMappings(IHaContext ha, Plc plc)
    {
        var entities = ha.GetAllEntities();
        return (plc.MappedSymbols
            .SelectWhereNotNull(s => CreateMapping(entities, s)!)
            .ToArray());
    }
    public static IMapping? CreateMapping(IReadOnlyCollection<Entity> entities, ISymbol symbol)
    {
        try
        {
            var gwType = symbol.GetGatewayDataType();
            if (gwType.Name == PlcType_View)
                return (null); // Skip (Not required as mapping target).
            else if (MappingInfo.TryGetValue(gwType.Name, out var mapping))
            {
                var entityId = symbol.GetEntityPath();
                if (!entityId.Contains('.'))
                    entityId = $"{mapping.Info.EntityTypeName}.{entityId}";

                var entity = entities.FirstOrDefault(e => e.EntityId.Equals(entityId));
                if (entity is null)
                    throw new NullReferenceException($"Referenced entity '{entityId}' is missing in home assistant context!");
                else
                    return ((IMapping)Activator.CreateInstance(mapping.Type, [mapping.Info, symbol, entity])!);
            }
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
        .GetDescription()
        .Split('.')
        .First();
    private static readonly IReadOnlyDictionary<string, PlcMappingParameter> PlcMappingAttributes = Enum
        .GetValues<PlcMappingParameter>()
        .ToDictionary(
            e => e.GetDescription(),
            e => e
        );
    #endregion


    public static string ToPlcType(this string source) => $"Tc3_MiniFrame.{source}";
    public static bool IsMapped(this ISymbol source) => (source.TryGetMappingParameterAttribute() is not null);
    public static IEnumerable<ITypeAttribute> GetMappingParameterAttributes(this ISymbol source) => source.Attributes
        .Where(a => PlcMappingAttributes.ContainsKey(a.Name.ToLower()));
    public static ITypeAttribute? TryGetMappingParameterAttribute(this ISymbol source, PlcMappingParameter parameter = PlcMappingParameter.Mapping)
    {
        var attribName = parameter.GetDescription();
        return (source
            .GetMappingParameterAttributes()
            .FirstOrDefault(a => a.Name.ToLower().Equals(attribName)));
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
    public static IDataType GetGatewayDataType(this ISymbol source)
    {
        const string mfrTypeName = "Tc3_MiniFrame.FB_Mfr";

        if (source.TypeName.StartsWith(mfrTypeName))
            return (source.DataType);

        if (source.DataType is not IStructType structType)
            throw new NotSupportedException($"Failed to determine gateway datatype from symbol '{source.InstancePath}' of not non-structured datatype '{source.TypeName}'!");
        else if (!structType.BaseTypeName.StartsWith(mfrTypeName))
            throw new NotSupportedException($"Failed to determine gateway datatype from symbol '{source.InstancePath}' of not supported datatype '{source.TypeName}'!");
        else
            return (structType.BaseType);
    }
}