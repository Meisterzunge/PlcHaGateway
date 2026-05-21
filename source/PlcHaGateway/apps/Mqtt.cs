using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Extensions.MqttEntityManager;
using TwinCAT.Ads;
using TwinCAT.Ads.Native;
using TwinCAT.TypeSystem;
using Utilities.Core;


[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class EntityTypeAttribute : Attribute
{
    public EntityTypeAttribute(string typeName, params PlcMappingParameter[] mandatoryParameters)
    {
        this.TypeName = typeName;
        this.MandatoryParameters = mandatoryParameters;
    }
    

    public string TypeName { get; }
    public PlcMappingParameter[] MandatoryParameters { get; }
}
public enum EntityType
{
    /// <see href="https://www.home-assistant.io/integrations/sensor.mqtt/">
    [EntityType("sensor")]
    Sensor,
    /// <see href="https://www.home-assistant.io/integrations/binary_sensor.mqtt/">
    [EntityType("binary_sensor")]
    BinarySensor,

    /// <see href="https://www.home-assistant.io/integrations/number.mqtt/">
    [EntityType("number")]
    Number,
    /// <see href="https://www.home-assistant.io/integrations/switch.mqtt/">
    [EntityType("switch")]
    Switch,
    /// <see href="https://www.home-assistant.io/integrations/select.mqtt/">
    [EntityType("select", PlcMappingParameter.Enum)]
    Select
}


internal static partial class Ext
{
    #region Constants
    private static IReadOnlyDictionary<PlcMappingParameter, MappingParameterAttribute> MqttMappingParameters = Enum.GetValues<PlcMappingParameter>()
        .Select(p => (Parameter: p, Attribute: p.GetAttribute()))
        .Where(p => !string.IsNullOrEmpty(p.Attribute.MqttAttribute))
        .ToDictionary(
            p => p.Parameter,
            p => p.Attribute
        );
    #endregion


    public static EntityTypeAttribute GetAttribute(this EntityType source) => source.GetCustomAttribute<EntityTypeAttribute, EntityType>();

    public static async Task CreateMqttEntity(this IMapping source, IMqttEntityManager entityManager)
    {
        // Validate:
        if (source.Backend != IntegrationType.Mqtt)
            throw new InvalidOperationException($"Failed to create MQTT entity for mapping of backend '{source.Backend}'!");
            
        var mandatoryParams = source.Info.EntityType.GetAttribute().MandatoryParameters;
        var mappingParams = source.Symbol
            .GetMappingParameterAttributes()
            .ToArray();
        var missingParams = mandatoryParams
            .Where(mp => !mappingParams.Any(p => p.Name.Equals(mp.GetAttribute().PlcAttribute, StringComparison.InvariantCultureIgnoreCase)))
            .ToArray();
        if (!missingParams.IsEmpty())
            throw new ArgumentException($"Missing mandatory attribute(s): {string.Join(", ", missingParams)}");

        EntityCreationOptions? options = null;
        object? additionalConfig = null;
        if (source.Owner is null)
        {
            // Create independant, device-less entity:
            throw new NotImplementedException($"Creating device-less entity not implemented, yet!"); // (BETA) ... device-less
        }
        else
        {
            // Determine or create device information:
            if (!devices.TryGetValue(source.Owner, out var device))
            {
                var dev = (IDictionary<string, object>)(object)new ExpandoObject();
                {
                    dev.Add("identifiers", new string[] { source.Owner.Identifier });
                    dev.Add("name", source.Owner.Name);
                    if (source.Owner.Model is not null)
                        dev.Add("model", source.Owner.Model);
                    if (source.Owner.Manufacturer is not null)
                        dev.Add("manufacturer", source.Owner.Manufacturer);
                    if (source.Owner.Version is not null)
                        dev.Add("sw_version", source.Owner.Version.ToDouble());
                }
                devices.Add(source.Owner, device = dev);
            }

            // Create MQTT entity:
            options = new EntityCreationOptions(source.DeviceClass, null, source.Name!);
            additionalConfig = new ExpandoObject();
            {
                // Apply specified attributes:
                var addCfg = (IDictionary<string, object>)additionalConfig;
                var attributes = source
                    .GetMqttMappingParameterAttributes()
                    .ToArray();
                foreach (var attrib in attributes)
                {
                    switch (attrib.Parameter)
                    {
                        case PlcMappingParameter.Enum:
                            var msSource = (MultistateMapping)source;
                            var enumValues = msSource.Options.Values.ToArray();
                            addCfg.Add(attrib.MqttAttribute, enumValues);
                            break;
                        case PlcMappingParameter.Minimum:
                        case PlcMappingParameter.Maximum:
                        case PlcMappingParameter.Step:
                            if (!double.TryParse(attrib.Attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue))
                                throw new FormatException($"Failed to parse numeric mapping parameter '{attrib.Parameter}' with value '{attrib.Attribute.Value}' for symbol '{source.Symbol.InstancePath}'.");
                            addCfg.Add(attrib.MqttAttribute, numberValue);
                            break;
                        default:
                            addCfg.Add(attrib.MqttAttribute, attrib.Attribute.Value);
                            break;
                    }
                }

                // Apply configuration:
                addCfg.Add("state_topic", $"homeassistant/{source.Info.EntityTypeName}/{source.Owner.Identifier}/state");
                addCfg.Add("value_template", string.Format("{{{{ value_json.{0} }}}}", source.EntityId));
                addCfg.Add("default_entity_id", $"{source.Info.EntityTypeName}.{source.EntityId}");
                addCfg.Add("device", device);
            }
        }

        var entityId = $"{source.Info.EntityTypeName}.{source.EntityId}";
        LogEvent.Mqtt.LogTrace("Creating MQTT entity {0}.", entityId);

        await entityManager.CreateAsync(entityId, options, additionalConfig).ConfigureAwait(false);

        // Subscribe to MQTT entity:
        // > Don't subscribe to 'value type' function blocks:
        //   Unfortunately HASS don't send state-change updates for indicator entities like 'binary_sensor' and 'sensor' 😥
        if (source.SymbolType.IsOperationalType())
        {
            Action<string> OnSubscribe = async (state) =>
            {
                LogEvent.Mqtt.LogTrace("Receive changed value of MQTT entity {0}.", entityId);
                try
                {
                    source.SetMqttValue(state);
                    await new[] { source }
                        .WriteMappingsAsync(entityManager, false) // Do not reset dirty hence we want to update the PLC site!
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogEvent.Mqtt.LogError(ex, "Failed to apply received value '{0}' of MQTT entity {1}.", state, entityId);
                }
            };
            var command = await entityManager
                .PrepareCommandSubscriptionAsync(entityId)
                .ConfigureAwait(false);
            command.Subscribe(OnSubscribe);
        }
    }

    /// <summary>
    /// Writes mappings to MQTT.
    /// </summary>
    public static Task WriteMappingsAsync(this IEnumerable<IMapping> source, IMqttEntityManager entityManager, bool resetDirty = true) => Task.WhenAll(source
        .GroupBy(m => m.Owner)
        .SelectMany(grp => WriteDeviceMappingsAsync(grp.Key!, grp, entityManager, resetDirty)));
    /// <summary>
    /// Writes mappings of specified device to MQTT.
    /// </summary>
    private static IEnumerable<Task> WriteDeviceMappingsAsync(this VirtualDevice? source, IEnumerable<IMapping> mappings, IMqttEntityManager entityManager, bool resetDirty = true)
    {
        if (source is null)
            return mappings.GroupBy(m => m.Info.EntityTypeName)
                .Select(grp => WriteDeviceMappingsAsync(source, grp.Key, grp, entityManager, resetDirty));

        return mappings.GroupBy(m => m.Info.EntityTypeName)
            .Select(grp => WriteDeviceMappingsAsync(source, grp.Key, source.Mappings.Where(m => m.Info.EntityTypeName == grp.Key), entityManager, resetDirty));
    }
    /// <summary>
    /// Writes mappings of specified device and entity type to MQTT.
    /// </summary>
    private static Task WriteDeviceMappingsAsync(this VirtualDevice? source, string entityType, IEnumerable<IMapping> mappings, IMqttEntityManager entityManager, bool resetDirty = true)
    {
        // TODO: Handle device-less mappings
        if (source is null)
        {
            mappings.ResetDirty();
            return (Task.CompletedTask);
        }

        var state = (IDictionary<string, object>)(object)new ExpandoObject();
        {
            foreach (var mapping in mappings)
            {
                Debug.Assert(ReferenceEquals(mapping.Owner, source), $"Only mappings of specified device '{source}' allowed!");
                Debug.Assert(mapping.Info.EntityTypeName.Equals(entityType), $"Only mappings of specified entity type '{entityType}' allowed!");

                state.Add(mapping.EntityId, mapping.GetMqttValue() ?? "#null");
                if (resetDirty)
                    mapping.ResetDirty();
            }
        }
        return (entityManager.SetStateAsync($"{entityType}.{source.Identifier}", JsonSerializer.Serialize(state)));
    }
    #region Helper
    private static IEnumerable<(PlcMappingParameter Parameter, string MqttAttribute, ITypeAttribute Attribute)> GetMqttMappingParameterAttributes(this IMapping source)
    {
        var attribs = source.Symbol.GetMappingParameterAttributes();
        foreach (var attrib in attribs)
        {
            if (MqttMappingParameters.TryGetKeyOf(a => attrib.Name.Equals(a.PlcAttribute, StringComparison.InvariantCultureIgnoreCase), out var param))
            {
                var paramAttrib = MqttMappingParameters[param];
                if (string.IsNullOrEmpty(attrib.Value))
                    throw new ArgumentNullException($"Specified attribute '{paramAttrib.PlcAttribute}' has no value!");
                else
                    yield return (param, paramAttrib.MqttAttribute!, attrib);
            }
        }
        yield break;
    }
    private static object? GetMqttValue(this IMapping source)
    {
        switch (source)
        {
            case AnalogMapping aMapping: return (aMapping.Value);
            case BooleanMapping bMapping:
                if (bMapping.Value is null)
                    return (null);
                else
                    return (bMapping.Value!.Value ? "ON" : "OFF");
            case MultistateMapping mMapping: return (mMapping.State);

            default: throw new NotSupportedException($"Failed to obtain value from not supported mapping of type '{source.GetType().Name}'!");
        }
    }
    private static void SetMqttValue(this IMapping source, string? value)
    {
        // Convert value:
        object? val = value;
        switch (source)
        {
            case null: break;

            case AnalogMapping aMapping: break;
            case BooleanMapping bMapping:
                val = value!.Equals("ON", StringComparison.InvariantCultureIgnoreCase);
                break;
            case MultistateMapping mMapping: break;

            default: throw new NotSupportedException($"Failed to obtain value from not supported mapping of type '{source.GetType().Name}'!");
        }

        source!.SetValue(val, AutomationContext.Hass);
    }
    #endregion


    private static Dictionary<VirtualDevice, dynamic> devices = new();
}