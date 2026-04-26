using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
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
    public MappingAttribute(EntityType entityType, params SymbolType[] supportedSymbolTypes)
    {
        this.EntityType = entityType;
        this.EntityTypeName = entityType.GetAttribute().TypeName;
        this.SupportedSymbolTypes = supportedSymbolTypes;
    }


    public EntityType EntityType { get; }
    public string EntityTypeName { get; }
    public SymbolType[] SupportedSymbolTypes { get; }
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
        this.Name = symbol.GetEntityName() ?? symbol.InstanceName;
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
        this.Events = symbol.SubSymbols
            .Flatten()
            .WhereMapped()
            .SelectWhereNotNull(s => EventBindingFactory.TryCreate(s, this))
            .ToArray();
        this.WeatherBindings = symbol.SubSymbols
            .Flatten()
            .WhereMapped()
            .SelectWhereNotNull(s => WeatherBindingFactory.TryCreate(s, this))
            .ToArray();
    }


    #region Properties.Management
    public ISymbol Symbol { get; }
    public IMapping[] Mappings { get; }
    public IEventBinding[] Events { get; }
    public IWeatherBinding[] WeatherBindings { get; }
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
    SymbolType SymbolType { get; }
    VirtualDevice? Owner { get; }
    ISymbol Symbol { get; }
    string EntityId { get; }
    string? AttributeKey { get; }

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
    internal Mapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null, SymbolType? symbolType = null)
    {
        var sym = (Symbol)symbol;
        var session = (AdsSession)sym.Connection!.Session!;
        var entityInfo = symbol.GetEntityInfo();

        this.dataTypes = session.SymbolServer.DataTypes;

        this.Info = info;
        this.SymbolType = symbolType ?? symbol.GetSymbolType(out _);
        this.Owner = owner;
        this.Symbol = symbol;
        this.EntityId = entityInfo.Path;
        this.Backend = entityInfo.Backend;
        this.AttributeKey = entityInfo.AttributeKey;
        this.Name = symbol.GetEntityName(owner) ?? Symbol.InstanceName;
        this.DeviceClass = TryGetMappingParameter(PlcMappingParameter.DeviceClass)?.Value;

        LogEvent.Gw.LogTrace($"Created mapping '{this}'.");
    }


    #region Properties.Management
    public MappingAttribute Info { get; }
    public IntegrationType Backend { get; private set; }
    public SymbolType SymbolType { get; }
    public VirtualDevice? Owner { get; }
    public ISymbol Symbol { get; }
    public string EntityId { get; }
    public string? AttributeKey { get; }

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
        var dataType = dataTypes.FirstOrDefault(dt => dt.Name.Split('.').Last().Equals(typeName));
        if (dataType is null)
            throw new NullReferenceException($"Declared datatype '{typeName}' not found in TwinCAT type system.");
        else
            return (dataType);
    }
    protected ITypeAttribute GetMappingParameter(PlcMappingParameter parameter)
    {
        var attr = Symbol.TryGetMappingParameterAttribute(parameter, Owner);
        if (attr is null)
            throw new NullReferenceException($"Mandatory mapping parameter '{parameter.GetAttribute().PlcAttribute}' not specified.");
        else
            return (attr);
    }
    protected ITypeAttribute? TryGetMappingParameter(PlcMappingParameter parameter) => Symbol.TryGetMappingParameterAttribute(parameter, Owner);

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
[Mapping(EntityType.Sensor, SymbolType.AnalogInput, SymbolType.AnalogOutput, SymbolType.AnalogValue)]
[Mapping(EntityType.Number, SymbolType.AnalogOperationalValue)]
public class AnalogMapping : Mapping<float>
{
    public AnalogMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }
    internal AnalogMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner, SymbolType symbolType) : base(info, symbol, owner, symbolType) { }
}
[Mapping(EntityType.BinarySensor, SymbolType.BinaryInput, SymbolType.BinaryOutput, SymbolType.BinaryValue)]
[Mapping(EntityType.Switch, SymbolType.BinaryOperationalValue)]
public class BooleanMapping : Mapping<bool>
{
    public BooleanMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : base(info, symbol, owner) { }
    internal BooleanMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner, SymbolType symbolType) : base(info, symbol, owner, symbolType) { }
}
[Mapping(EntityType.Sensor, SymbolType.MultistateValue)]
[Mapping(EntityType.Select, SymbolType.MultistateOperationalValue)]
public class MultistateMapping : Mapping<uint>
{
    public MultistateMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner = null) : this(info, symbol, owner, null) { }
    internal MultistateMapping(MappingAttribute info, ISymbol symbol, VirtualDevice? owner, SymbolType? symbolType) : base(info, symbol, owner, symbolType)
    {
        if (info.EntityType == EntityType.Sensor)
        {
            /// Refer to <see cref="https://www.home-assistant.io/integrations/sensor.mqtt/#options"/> for details.
            if (DeviceClass is null)
                this.DeviceClass = "enum";
            else
                throw new ArgumentException($"Multistate mappings of type '{info.EntityType.GetDescription()}' must not declare a device class!");
        }

        var enumType = ResolveEnumType();
        this.Options = enumType.GetFields();

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
    private IEnumType ResolveEnumType()
    {
        var enumAttribute = TryGetMappingParameter(PlcMappingParameter.Enum);
        if (enumAttribute is not null)
            return ((IEnumType)GetDataType(enumAttribute.Value));

        var dataType = Symbol.GetResolvedDataType();
        if (dataType is IEnumType enumType)
            return (enumType);

        throw new NullReferenceException($"Failed to determine enum datatype for symbol '{Symbol.InstancePath}'.");
    }
    private uint MatchEnumState(string text, IEnumType enumType)
    {
        if (enumType.EnumValues.TryParse(text, out IEnumValue? val) && (val is not null))
            return (Convert.ToUInt32(val.Value));
        else
            throw new KeyNotFoundException($"State '{text}' did not match any declared members of type '{enumType.Name}'.");
    }
    #endregion
}

internal sealed class MappingFactory
{
    #region Constants
    static readonly MappingAttribute PrimitiveAnalogInfo = new(EntityType.Sensor, SymbolType.PrimitiveAnalog);
    static readonly MappingAttribute PrimitiveBooleanInfo = new(EntityType.BinarySensor, SymbolType.PrimitiveBinary);
    static readonly MappingAttribute PrimitiveMultistateInfo = new(EntityType.Sensor, SymbolType.PrimitiveMultistate);
    static readonly IReadOnlyDictionary<Type, MappingAttribute[]> MappingTypes = UtilAssembly
        .GetDefinedTypesOf<MappingAttribute>()
        .ToDictionary(
            t => t,
            t => t.GetCustomAttributes<MappingAttribute>().ToArray()
        );
    static readonly IReadOnlyDictionary<SymbolType, (Type Type, MappingAttribute Info)> MappingInfo = MappingTypes
        .SelectMany(t => t.Value)
        .SelectMany(m => m.SupportedSymbolTypes)
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
            if (symbol.TryGetSymbolType(out var fb, out _))
            {
                if (fb == SymbolType.View)
                    return (null); // Skip (Not required as mapping target).
                else if (fb.IsEventType())
                    return (null); // Skip (Handled as event bindings, not IMapping).
                else if (fb.IsWeatherType())
                    return (null); // Skip (Handled as weather bindings, not IMapping).
                else if (MappingInfo.TryGetValue(fb, out var mapping))
                    return ((IMapping)Activator.CreateInstance(mapping.Type, [mapping.Info, symbol, device])!);
                else
                    throw new NotSupportedException($"Datatype '{fb.GetTypeName()}' is not supported!");
            }
            else if (symbol.TryGetPrimitiveMappingType(out var primitiveType))
            {
                return (primitiveType switch
                {
                    SymbolType.PrimitiveAnalog => new AnalogMapping(PrimitiveAnalogInfo, symbol, device, primitiveType),
                    SymbolType.PrimitiveBinary => new BooleanMapping(PrimitiveBooleanInfo, symbol, device, primitiveType),
                    SymbolType.PrimitiveMultistate => new MultistateMapping(PrimitiveMultistateInfo, symbol, device, primitiveType),
                    _ => throw new NotSupportedException($"Primitive datatype '{symbol.TypeName}' is not supported!")
                });
            }
            else
                throw new NotSupportedException($"Datatype '{symbol.TypeName}' is not supported!");
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mapping for symbol '{0}'.", symbol.InstancePath);
        }
        return (null);
    }


    #region Helper
    private static (Type Type, MappingAttribute Info) GetMappingInfo(SymbolType plcType)
    {
        foreach (var info in MappingTypes)
        {
            foreach (var attrib in info.Value)
            {
                if (attrib.SupportedSymbolTypes.Contains(plcType))
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
    private static readonly string[] PrimitiveNumericTypeNames = ["SINT", "USINT", "BYTE", "INT", "UINT", "WORD", "DINT", "UDINT", "DWORD", "LINT", "ULINT", "LWORD", "REAL", "LREAL"];
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
        .Where(a => a.TryResolveMappingParameterAttribute(out _, out var targetPath) && (targetPath is null));
    public static ITypeAttribute? TryGetMappingParameterAttribute(this ISymbol source, PlcMappingParameter parameter = PlcMappingParameter.Mapping) => source
        .GetMappingParameterAttributes()
        .FirstOrDefault(a => a.Name.Equals(parameter.GetAttribute().PlcAttribute, StringComparison.InvariantCultureIgnoreCase));
    /// <summary>
    /// Resolves a mapping parameter for a mapped symbol and supports virtual-device scoped overrides declared on parent symbols.
    /// Override syntax: PlcHa.X[Target.Path]
    /// </summary>
    public static ITypeAttribute? TryGetMappingParameterAttribute(this ISymbol source, PlcMappingParameter parameter, VirtualDevice? owner)
    {
        // Overrides are only supported in virtual-device context.
        if (owner?.Symbol is not null)
        {
            var root = owner.Symbol;
            var parent = source.Parent;
            ITypeAttribute? selectedOverride = null;
            while (parent is not null)
            {
                var targetPath = GetRelativeSymbolPath(parent, source);
                var overrideAttribute = parent.Attributes.FirstOrDefault(a =>
                    a.TryResolveMappingParameterAttribute(out var attrParameter, out var attrTarget)
                    && (attrParameter == parameter)
                    && !string.IsNullOrEmpty(attrTarget)
                    && attrTarget.Equals(targetPath, StringComparison.InvariantCultureIgnoreCase)
                );
                if (overrideAttribute is not null)
                    selectedOverride = overrideAttribute;

                if (ReferenceEquals(parent, root))
                    break;
                parent = parent.Parent;
            }

            // Most upward/outer override wins.
            if (selectedOverride is not null)
                return (selectedOverride);
        }

        // Fallback to mapped-entity attribute/default behavior.
        return (source.TryGetMappingParameterAttribute(parameter));
    }
    public static string? TryGetMapping(this ISymbol source)
    {
        var attrib = source.TryGetMappingParameterAttribute();
        if (attrib is null)
            return (null);
        else if (string.IsNullOrEmpty(attrib.Value))
            return (source.InstanceName);
        else
            return (attrib.Value);
    }

    /// <summary>
    /// Returns an enumeration of all parents and the instance self, known as <i>XPath</i>.
    /// </summary>
    /// <param name="root">Optional root symbol to stop at, otherwise returns full path to top.</param>
    public static IEnumerable<ISymbol> GetXPath(this ISymbol source, ISymbol? root = null) => getXPath(source, root).Reverse();
    private static IEnumerable<ISymbol> getXPath(this ISymbol source, ISymbol? root = null)
    {
        yield return (source);

        var parent = source.Parent;
        while ((parent is not null) && (parent != root))
        {
            yield return (parent);
            parent = parent.Parent;
        }
    }
    private static string GetRelativeSymbolPath(ISymbol anchor, ISymbol target)
    {
        var names = new List<string>();
        var symbol = target;
        while ((symbol is not null) && !ReferenceEquals(symbol, anchor))
        {
            names.Add(symbol.InstanceName);
            symbol = symbol.Parent;
        }
        if (!ReferenceEquals(symbol, anchor))
            throw new InvalidOperationException($"Failed to determine symbol path from '{anchor.InstancePath}' to '{target.InstancePath}'.");

        names.Reverse();
        return (string.Join('.', names));
    }
    private static bool TryResolveMappingParameterAttribute(this ITypeAttribute source, out PlcMappingParameter parameter, out string? targetPath)
    {
        parameter = default;
        targetPath = null;

        var attributeName = source.Name?.Trim();
        if (string.IsNullOrEmpty(attributeName))
            return (false);

        var openBracket = attributeName.IndexOf('[');
        if (openBracket >= 0)
        {
            var closeBracket = attributeName.LastIndexOf(']');
            if ((closeBracket <= openBracket) || (closeBracket != (attributeName.Length - 1)))
                return (false);

            targetPath = attributeName.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
            if (string.IsNullOrEmpty(targetPath))
                return (false);

            targetPath = NormalizeTargetPath(targetPath);
            attributeName = attributeName.Substring(0, openBracket);
        }

        return (PlcMappingAttributes.TryGetValue(attributeName.ToLower(), out parameter));
    }
    private static string NormalizeTargetPath(string source) => string.Join('.', source
        .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    public static (string Path, IntegrationType Backend, string? AttributeKey) GetEntityInfo(this ISymbol source)
    {
        var mapping = source.TryGetMappingParameterAttribute()?.Value;
        if (mapping?.Contains('.') == true)
        {
            // Mapping binds to a specific entity (native).
            
            var colonIdx = mapping.IndexOf(':');
            if (colonIdx == -1)
                // Bind to entity state:
                return (mapping, IntegrationType.Native, null);
            else
            {
                // Bind to specific entity attribute:
                var entityId = mapping.Substring(0, colonIdx);
                var attributeKey = mapping.Substring(colonIdx + 1);
                return (entityId, IntegrationType.Native, string.IsNullOrWhiteSpace(attributeKey) ? null : attributeKey);
            }
        }
        else
            // Mapping specifies a new entity, which will be configured via MQTT:
            return (GetEntityPath(source), IntegrationType.Mqtt, null);
    }
    public static string GetEntityPath(this ISymbol source) => string.Join("_", source
        .GetXPath()
        .Select(s => s.TryGetMapping()?.ToLower())
        .Where(s => !string.IsNullOrEmpty(s)));
    public static string? GetEntityName(this ISymbol source, VirtualDevice? owner = null) => string.Join(" - ", source
        .GetXPath(owner?.Symbol)
        .Select(s => s.TryGetMappingParameterAttribute(PlcMappingParameter.Name)?.Value)
        .Where(s => !string.IsNullOrEmpty(s)));

    public static bool IsSymbolType(this ISymbol source, SymbolType expectedType) => (GetSymbolType(source, out _) == expectedType);
    public static bool IsViewSymbolTypeOrSubclass(this ISymbol source) => TryGetViewBaseType(source.DataType, out _);
    public static bool IsSupportedMappedMember(this ISymbol source)
    {
        if (!source.IsMapped())
            return (false);
        if (source.TryGetSymbolType(out var symbolType, out _))
            return (symbolType != SymbolType.View);
        return (source.TryGetPrimitiveMappingType(out _));
    }
    public static bool TryGetSymbolType(this ISymbol source, out SymbolType symbolType, out IDataType? dataType)
    {
        symbolType = default;
        dataType = null;

        if (!TryGetMiniFrameBaseType(source.DataType, out var miniFrameType))
            return (false);
        if (!miniFrameType.Name.TryGetSymbolType(out symbolType))
            return (false);

        dataType = miniFrameType;
        return (true);
    }
    public static bool HasSupportedMappedMembers(this ISymbol source) => source.SubSymbols
        .Flatten()
        .Where(s => !ReferenceEquals(s, source))
        .Any(IsSupportedMappedMember);
    public static bool TryGetSymbolType(this string source, out SymbolType symbolType)
    {
        symbolType = default;
        if (string.IsNullOrWhiteSpace(source))
            return (false);

        var parts = source.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return (false);
        var typeName = parts.Last();
        if (!typeName.StartsWith($"{Tc3_MiniFrame.FunctionBlockPrefix}_", StringComparison.InvariantCultureIgnoreCase))
            return (false);
        if ((parts.Length > 1) && !parts.Any(p => p.Equals(Tc3_MiniFrame.LibraryName, StringComparison.InvariantCultureIgnoreCase)))
            return (false);

        return (Tc3_MiniFrame.SymbolTypes.TryGetKeyOf(
            i => i.TypeName.Equals(typeName, StringComparison.InvariantCultureIgnoreCase),
            out symbolType));
    }
    public static SymbolType GetSymbolType(this string source)
    {
        if (source.TryGetSymbolType(out var symbolType))
            return (symbolType);
        else
            throw new KeyNotFoundException($"Type '{source}' doesn't seem to be a supported symbol type!");
    }
    public static SymbolType GetSymbolType(this ISymbol source, out IDataType dataType)
    {
        dataType = GetMiniFrameType(source);
        return (dataType.Name.GetSymbolType());
    }
    public static IDataType GetMiniFrameType(this ISymbol source)
    {
        if (TryGetMiniFrameBaseType(source.DataType, out var dataType))
            return (dataType);

        if (source.DataType is not IStructType)
            throw new NotSupportedException($"Failed to determine MiniFrame type from symbol '{source.InstancePath}' of not non-structured datatype '{source.TypeName}'!");

        throw new NotSupportedException($"Failed to determine MiniFrame type from symbol '{source.InstancePath}' of not supported datatype '{source.TypeName}'!");
    }
    public static IDataType GetResolvedDataType(this ISymbol source) => GetResolvedDataType(source.DataType);
    public static IDataType GetResolvedDataType(this IDataType? source)
    {
        var dataType = source;
        while (dataType is IAliasType aliasType)
            dataType = aliasType.BaseType;

        return (dataType ?? throw new NotSupportedException("Failed to resolve datatype."));
    }
    public static bool TryGetPrimitiveMappingType(this ISymbol source, out SymbolType symbolType)
    {
        var dataType = source.GetResolvedDataType();
        if (dataType is IEnumType)
        {
            symbolType = SymbolType.PrimitiveMultistate;
            return (true);
        }
        if (dataType is not IPrimitiveType)
        {
            symbolType = default;
            return (false);
        }

        var typeName = dataType.Name.Split('.').Last().ToUpperInvariant();
        if (typeName.Equals("BOOL"))
        {
            symbolType = SymbolType.PrimitiveBinary;
            return (true);
        }
        if (PrimitiveNumericTypeNames.Contains(typeName))
        {
            symbolType = SymbolType.PrimitiveAnalog;
            return (true);
        }

        symbolType = default;
        return (false);
    }

    private static bool TryGetViewBaseType(IDataType? source, out IDataType dataType)
    {
        var currentType = source;
        while (currentType is not null)
        {
            if (currentType.IsMiniFrameType())
            {
                if (currentType.Name.TryGetSymbolType(out var symbolType) && (symbolType == SymbolType.View))
                {
                    dataType = currentType;
                    return (true);
                }

                break;
            }

            if (currentType is not IStructType structType)
                break;

            currentType = structType.BaseType;
        }

        dataType = null!;
        return (false);
    }

    private static bool TryGetMiniFrameBaseType(IDataType? source, out IDataType dataType)
    {
        var currentType = source;
        while (currentType is not null)
        {
            if (currentType is IAliasType aliasType)
            {
                currentType = aliasType.BaseType;
                continue;
            }

            if (currentType.IsMiniFrameType())
            {
                dataType = currentType;
                return (true);
            }

            if (currentType is not IStructType structType)
                break;

            currentType = structType.BaseType;
        }

        dataType = null!;
        return (false);
    }
}