using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography.X509Certificates;
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

    public static Task CreateMqttEntity(this IMapping source, IMqttEntityManager entityManager)
    {
        // Validate:
        var mandatoryParams = source.Info.EntityType.GetAttribute().MandatoryParameters;
        var mappingParams = source.Symbol
            .GetMappingParameterAttributes()
            .ToArray();
        var missingParams = mandatoryParams
            .Where(mp => !mappingParams.Any(p => p.Name.Equals(mp.GetAttribute().PlcAttribute, StringComparison.InvariantCultureIgnoreCase)))
            .ToArray();
        if (!missingParams.IsEmpty())
            throw new ArgumentException($"Missing mandatory attribute(s): {string.Join(", ", missingParams)}");

        EntityCreationOptions ? options = null;
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
                devices.Add(source.Owner, device = new
                {
                    identifiers = source.Owner.Identifier,
                    name = source.Owner.Symbol.InstanceName, // (BETA) ... DeviceAttrib: implement new attribute in PLC
                    model = "ABC X1", // (BETA) ... DeviceAttrib: implement new attribute in PLC
                    manufacturer = "Voltium", // (BETA) ... DeviceAttrib: implement new attribute in PLC
                    sw_version = 1.22 // (BETA) ... DeviceAttrib: implement new attribute in PLC
                });
            }

            // Create MQTT entity:
            var stateTopic = $"homeassistant/sensor/{source.Owner.Identifier}/state";

            options = new EntityCreationOptions(source.DeviceClass, null, source.Name);
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
                        default:
                            addCfg.Add(attrib.MqttAttribute, attrib.Attribute.Value);
                            break;
                    }
                }

                // Apply configuration:
                addCfg.Add("state_topic", stateTopic);
                addCfg.Add("value_template", string.Format("{{ value_json.{0} }}", source.EntityId));
                addCfg.Add("device", device);
            }
        }

        var entityId = $"{source.Info.EntityTypeName}.{source.EntityId}";
        LogEvent.Mqtt.LogTrace("Creating MQTT entity {0}.", entityId);

        return (entityManager.CreateAsync(entityId, options, additionalConfig));
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
    #endregion


    private static Dictionary<VirtualDevice, dynamic> devices = new();
}