using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Utilities.Core;
using static Tc3_MiniFrame;


[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class MappingAttribute : Attribute
{
    public MappingAttribute(EntityType entityType, params FunctionBlock[] supportedPlcTypes)
    {
        this.EntityType = entityType;
        this.EntityTypeName = entityType.GetAttribute().TypeName;
        this.SupportedPlcTypes = supportedPlcTypes;
    }


    public EntityType EntityType { get; }
    public string EntityTypeName { get; }
    public FunctionBlock[] SupportedPlcTypes { get; }
}

public interface IProjectInfo
{
    string? ProjectName { get; }
    string? Version { get; }
}
/// <summary>
/// Aggregates several <see cref="ISymbol">symbols</see> to be mapped for home assistant context.
/// </summary>
public class VirtualDevice : IEnumerable<ISymbol>
{
    public VirtualDevice(ISymbol symbol, IProjectInfo? info = null)
    {
        this.Symbol = symbol;
        this.info = info;
        this.Identifier = symbol.GetEntityPath();
        this.Name = symbol.TryGetMappingParameterAttribute(PlcMappingParameter.Name)?.Value ?? symbol.InstanceName;
        this.Model = symbol.TryGetMappingParameterAttribute(PlcMappingParameter.Model)?.Value ?? info?.ProjectName;
        this.Manufacturer = symbol.TryGetMappingParameterAttribute(PlcMappingParameter.Manufacturer)?.Value;

        var ver = symbol.TryGetMappingParameterAttribute(PlcMappingParameter.Version)?.Value ?? info?.Version;
        if (Version.TryParse(ver, out var v))
            this.Version = v;

        this.Mappings = symbol.SubSymbols
            .Flatten()
            .WhereMapped()
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
    public string? Model { get; }
    public string? Manufacturer { get; }
    public Version? Version { get; }
    #endregion


    public override string ToString() => Identifier;

    IEnumerator IEnumerable.GetEnumerator() => Mappings.GetEnumerator();
    public IEnumerator<ISymbol> GetEnumerator() => (IEnumerator<ISymbol>)Mappings.GetEnumerator();


    private IProjectInfo? info;
}

public enum AutomationContext
{
    /// <summary>
    /// PLC related context.
    /// </summary>
    Plc,
    /// <summary>
    /// HomeAssist or MQTT related context.
    /// </summary>
    Hass
}

public record Modification(DateTime TimeStamp, AutomationContext? Source);

public interface IMapping
{
    #region Properties.Management
    MappingAttribute Info { get; }
    IntegrationType Backend { get; }
    FunctionBlock FunctionBlockType { get; }
    VirtualDevice? Owner { get; }
    ISymbol Symbol { get; }
    string EntityId { get; }

    Modification? LastModified { get; }
    #endregion
    #region Properties
    string Name { get; }
    string? DeviceClass { get; }
    object? Value { get; }
    #endregion


    #region Management
    bool SetValue(object? value, AutomationContext? source = null);
    bool IsDirty(AutomationContext target);
    internal void ResetDirty();
    #endregion
}
public abstract class Mapping<T> : IMapping
    where T : struct
{
    internal Mapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null)
    {
        var sym = (Symbol)symbol;
        var session = (AdsSession)sym.Connection!.Session!;
        var entityInfo = symbol.GetEntityInfo();

        this.dataTypes = session.SymbolServer.DataTypes;

        this.Info = info;
        this.FunctionBlockType = symbol.GetFunctionBlockType(out _);
        this.Owner = owner;
        this.Symbol = symbol;
        this.EntityId = entityInfo.Path;
        this.Backend = entityInfo.Backend;
        this.Name = TryGetMappingParameter(PlcMappingParameter.Name)?.Value ?? Symbol.InstanceName;
        this.DeviceClass = TryGetMappingParameter(PlcMappingParameter.DeviceClass)?.Value;

        LogEvent.Gw.LogTrace($"Created mapping for '{symbol.InstancePath}'.");
    }


    #region Properties.Management
    public MappingAttribute Info { get; }
    public IntegrationType Backend { get; private set; }
    public FunctionBlock FunctionBlockType { get; }
    public VirtualDevice? Owner { get; }
    public ISymbol Symbol { get; }
    public string EntityId { get; }

    public Modification? LastModified { get; private set; }
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
    private bool dirty;
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

    bool IMapping.SetValue(object? value, AutomationContext? source)
    {
        value = ConvertValue(value);
        if (value is null)
            throw new NotSupportedException($"Not allowed to assign value of null to '{this}'!");
        else if (value is T val)
            return (this.SetValue(val, source));
        else
            throw new InvalidCastException($"Failed to apply value of type '{value.GetType().Name}' to '{this}'!");
    }
    protected virtual T? ConvertValue(object? value)
    {
        if ((value is string sVal) && (string.IsNullOrEmpty(sVal)))
            return (null);
        else
            return ((T?)Convert.ChangeType(value, typeof(T)));
    }
    /// <returns>Returns <c>true</c> if value has changed, otherwise <c>false</c>.</returns>
    public bool SetValue(T? value, AutomationContext? source = null)
    {
        if ((this.value?.Equals(value) == true) && (this.LastModified is not null))
            return (false);
        else
        {
            this.dirty = true;
            this.value = value;
            this.LastModified = new(DateTime.Now, source);

            return (true);
        }
    }
    public bool IsDirty(AutomationContext target)
    {
        if (dirty)
            // Indicate dirty if target context was not the last modification source:
            return (target != LastModified?.Source);
        else
            return (false);
    }
    public void ResetDirty()
    {
        this.dirty = false;
    }
    #endregion


    public override string ToString() => EntityId;


    private IDataTypeCollection<IDataType> dataTypes;
}
[Mapping(EntityType.Sensor, FunctionBlock.AnalogInput, FunctionBlock.AnalogOutput, FunctionBlock.AnalogValue)]
[Mapping(EntityType.Number, FunctionBlock.AnalogOperationalValue)]
public class AnalogMapping : Mapping<float>
{
    public AnalogMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }
}
[Mapping(EntityType.BinarySensor, FunctionBlock.BinaryInput, FunctionBlock.BinaryOutput, FunctionBlock.BinaryValue)]
[Mapping(EntityType.Switch, FunctionBlock.BinaryOperationalValue)]
public class BooleanMapping : Mapping<bool>
{
    public BooleanMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }
}
[Mapping(EntityType.Sensor, FunctionBlock.MultistateValue)]
[Mapping(EntityType.Select, FunctionBlock.MultistateOperationalValue)]
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


    #region Properties
    public IReadOnlyDictionary<uint, string> Options { get; }
    public string? State
    {
        get
        {
            if (Value is null)
                return (null);
            else if (!Options.TryGetValue(Value!.Value, out var val))
                return (null);
            else
                return (val);
        }
        set => this.Value = ConvertValue(value);
    }
    #endregion


    #region Management
    protected override uint? ConvertValue(object? value)
    {
        switch (value)
        {
            case string sVal:
                if (Options.TryGetKeyOf(v => v.Equals(sVal, StringComparison.InvariantCultureIgnoreCase), out var val))
                    return (val);
                else
                    return (null);

            default: return (base.ConvertValue(value));
        }
    }
    #endregion


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
    static readonly IReadOnlyDictionary<FunctionBlock, (Type Type, MappingAttribute Info)> MappingInfo = MappingTypes
        .SelectMany(t => t.Value)
        .SelectMany(m => m.SupportedPlcTypes)
        .ToDictionary(
            pt => pt,
            pt => GetMappingInfo(pt)
    );
    #endregion


    public static VirtualDevice[] CreateDevices(IEnumerable<ISymbol> symbols, IProjectInfo? info = null) => symbols
        .Select(s => new VirtualDevice(s, info))
        .ToArray();
    public static IMapping[] CreateMappings(IEnumerable<ISymbol> symbols) => symbols
        .Select(s => CreateMapping(s))
        .WhereNotNull()
        .ToArray();
    public static IMapping? CreateMapping(ISymbol symbol, VirtualDevice? device = null)
    {
        try
        {
            var fb = symbol.GetFunctionBlockType(out _);
            if (fb == FunctionBlock.View)
                return (null); // Skip (Not required as mapping target).
            else if (MappingInfo.TryGetValue(fb, out var mapping))
                return ((IMapping)Activator.CreateInstance(mapping.Type, [mapping.Info, symbol, device])!);
            else
                throw new NotSupportedException($"Datatype '{fb.GetTypeName()}' is not supported!");
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mapping for symbol '{0}'.", symbol.InstancePath);
        }
        return (null);
    }


    #region Helper
    private static (Type Type, MappingAttribute Info) GetMappingInfo(FunctionBlock plcType)
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


    public static IEnumerable<IMapping> OfBackend(this IEnumerable<IMapping> source, IntegrationType backend) => source.Where(m => m.Backend.Equals(backend));
    public static IEnumerable<IMapping> WhereDirty(this IEnumerable<IMapping> source, AutomationContext context) => source.Where(m => m.IsDirty(context));
    public static void ResetDirty(this IEnumerable<IMapping> source) => source.ForEach(m => m.ResetDirty());
    /// <summary>
    /// Write changed values to specified <see cref="AutomationContext">context</see>.
    /// </summary>
    /// <returns>Written mappings.</returns>
    public static async Task<IMapping[]> UpdateWhereDirtyAsync(this IEnumerable<IMapping> source, AutomationContext context, Plc plc, IMqttEntityManager entityManager, CancellationToken cancellationToken)
    {
        var dirtyMappings = source
            .WhereDirty(context)
            .ToArray();
        if (!dirtyMappings.IsEmpty())
        {
            switch (context)
            {
                case AutomationContext.Plc:
                    await dirtyMappings.WriteMappingsAsync(plc, cancellationToken).ConfigureAwait(false);
                    break;

                case AutomationContext.Hass:
                    var dirtyMap = dirtyMappings
                        .GroupBy(m => m.Backend)
                        .ToDictionary(
                            grp => grp.Key,
                            grp => grp.ToArray()
                        );

                    foreach (var item in dirtyMap)
                    {
                        Task updateTask;
                        switch (item.Key)
                        {
                            case IntegrationType.Native: updateTask = item.Value.WriteMappingsAsync(); break;
                            case IntegrationType.Mqtt: updateTask = item.Value.WriteMappingsAsync(entityManager); break;

                            default: throw new NotSupportedException($"Failed to update dirty mapping of not supported backend '{item.Key}'!");
                        }
                        await updateTask.ConfigureAwait(false);
                    }
                    break;

                default: throw new NotSupportedException($"Failed to update dirty mappings of not supported context '{context}'!");
            }
        }

        return (dirtyMappings);
    }
    

    public static IEnumerable<ISymbol> WhereMapped(this IEnumerable<ISymbol> source) => source.Where(IsMapped);
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
    public static (string Path, IntegrationType Backend) GetEntityInfo(this ISymbol source)
    {
        var mapping = source.TryGetMappingParameterAttribute()?.Value;
        if (mapping?.Contains('.') == true)
            // Mappings binds to some specific entity, configured in home assistant:
            return (mapping, IntegrationType.Native);
        else
            // Mappings specifies a new entity, wich will be configured via MQTT: 
            return (GetEntityPath(source), IntegrationType.Mqtt);
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
    public static bool IsFunctionBlock(this ISymbol source, FunctionBlock expectedType) => (GetFunctionBlockType(source, out _) == expectedType);
    public static FunctionBlock GetFunctionBlockType(this string source)
    {
        var parts = source.Split('.');
        switch (parts.Length)
        {
            case 1: break;
            case 2:
                if (!parts.First().Equals(Tc3_MiniFrame.LibraryName, StringComparison.InvariantCultureIgnoreCase))
                    throw new ArgumentException($"Type '{source}' is not a '{Tc3_MiniFrame.LibraryName}' type!");
                break;

            default: throw new KeyNotFoundException($"Type '{source}' doesn't seem to be a functionblock!");
        }

        var typeName = parts.Last();
        return (Tc3_MiniFrame.FunctionBlocks.GetKeyOf((i) => i.TypeName.Equals(typeName)));
    }
    public static FunctionBlock GetFunctionBlockType(this ISymbol source, out IDataType dataType)
    {
        dataType = GetFunctionBlockType(source);
        return (dataType.Name.GetFunctionBlockType());
    }
    public static IDataType GetFunctionBlockType(this ISymbol source)
    {
        if (source.IsMiniFrameType())
            return (source.DataType);

        if (source.DataType is not IStructType structType)
            throw new NotSupportedException($"Failed to determine functionblock type from symbol '{source.InstancePath}' of not non-structured datatype '{source.TypeName}'!");
        else if (!structType.BaseType.IsMiniFrameType())
            throw new NotSupportedException($"Failed to determine functionblock type from symbol '{source.InstancePath}' of not supported datatype '{source.TypeName}'!");
        else
            return (structType.BaseType);
    }
}