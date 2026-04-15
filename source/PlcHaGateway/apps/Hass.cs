using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using TwinCAT.TypeSystem;
using Utilities.Core;


public enum IntegrationType
{
    /// <summary>
    /// Common <see cref="https://developers.home-assistant.io/docs/core/entity/">home assistant entity</see>.
    /// </summary>
    Native,
    /// <summary>
    /// <see cref="https://www.home-assistant.io/integrations/mqtt">MQTT Integration</see>.
    /// </summary>
    Mqtt
}

internal static partial class Ext
{
    public static Task BindToNativeEntity(this IMapping source, IHaContext ha)
    {
        // Validate:
        if (source.Backend != IntegrationType.Native)
            throw new InvalidOperationException($"Failed to create native entity for mapping of backend '{source.Backend}'!");
        if (source.FunctionBlockType.IsPhysicalType())
            throw new InvalidOperationException($"Failed to create native entity for mapping of physical type '{source.FunctionBlockType}'!");

        var entity = ha
            .GetAllEntities()
            .FirstOrDefault(e => e.EntityId.Equals(source.EntityId));
        if (entity is null)
            throw new NullReferenceException($"Missing native entity '{source.EntityId}'!");

        // Associate native entity:
        source.Associate(entity);

        // Subscribe to native entity:
        Action<StateChange> OnSubscribe = (state) =>
        {
            LogEvent.Hass.LogTrace("Receive changed value of native entity {0}.", state.Entity.EntityId);
            try
            {
                source.SetNativeValue(state.New);
            }
            catch (Exception ex)
            {
                LogEvent.Hass.LogError(ex, "Failed to apply received value of native entity {0}.", state.Entity.EntityId);
            }
        };
        entity
            .StateChanges()
            .Subscribe(OnSubscribe);

        // Set initial value:
        source.SetNativeValue(entity.EntityState);

        return (Task.CompletedTask);
    }

    /// <summary>
    /// Writes mappings to HASS.
    /// </summary>
    public static Task WriteMappingsAsync(this IEnumerable<IMapping> source) => Task.WhenAll(source.Select(WriteMappingAsync));
    /// <summary>
    /// Writes mapping to HASS.
    /// </summary>
    public static Task WriteMappingAsync(this IMapping source)
    {
        if (!source.FunctionBlockType.IsOperationalType())
            throw new InvalidOperationException($"Failed to write mapping of non-operational native entity '{source}'!");

        var entity = source.GetAssociatedEntity();
        switch (source)
        {
            /// <see href="https://www.home-assistant.io/integrations/input_number/#actions">
            case AnalogMapping aMapping: entity.CallService("set_value", new { value = source.Value }); break;
            /// <see href="https://www.home-assistant.io/integrations/input_boolean/#actions">
            case BooleanMapping bMapping: entity.CallService((source.Value?.Equals(true) == true) ? "turn_on" : "turn_off"); break;
            /// <see href="https://www.home-assistant.io/integrations/input_select/#actions">
            case MultistateMapping mMapping: entity.CallService("select_option", new { option = mMapping.State }); break;

            default: throw new NotSupportedException($"Failed to write mapping of not supported type '{source.GetType().Name}'!");
        }
        source.ResetDirty();

        return (Task.CompletedTask);
    }

    internal static Entity? Associate(this IMapping source, Entity? entity)
    {
        if (entity is null)
            associatedHassEntities.Remove(source);
        else
            associatedHassEntities.AddOrUpdate(source, entity);
        return (entity);
    }
    public static Entity GetAssociatedEntity(this IMapping source) => associatedHassEntities[source];
    public static Entity? TryGetAssociatedEntity(this IMapping source) => (associatedHassEntities.TryGetValue(source, out var mapping) ? mapping : null);
    private static readonly Dictionary<IMapping, Entity> associatedHassEntities = new();


    #region Helper
    private static void SetNativeValue(this IMapping source, EntityState? state)
    {
        // Resolve raw value: either entity state or a named attribute.
        object? raw;
        if (source.AttributeKey is null)
            raw = state?.State;
        else
        {
            var attrs = state?.Attributes;
            if (attrs is null || !attrs.ContainsKey(source.AttributeKey))
            {
                LogEvent.Hass.LogWarning("Attribute '{0}' not present on entity {1}. Skipping update.", source.AttributeKey, source.EntityId);
                return;
            }
            var attrVal = attrs[source.AttributeKey];
            // Normalize JsonElement to its underlying value so existing ConvertValue pipelines work correctly.
            raw = attrVal is JsonElement je ? NormalizeElement(je) : attrVal;
        }

        // Convert value using existing per-type logic:
        object? val = raw;
        switch (source)
        {
            case null: return;

            case AnalogMapping:
                break;
            case BooleanMapping:
                if (raw is string s)
                    val = state?.State!.Equals("on", StringComparison.InvariantCultureIgnoreCase);
                else
                    val = Convert.ToBoolean(raw);
                break;
            case MultistateMapping:
                break;

            default: throw new NotSupportedException($"Failed to obtain value from not supported mapping of type '{source.GetType().Name}'!");
        }

        if (val is not null)
            source.SetValue(val, AutomationContext.Hass);
    }
    #endregion
    #region Helper.Exceptions
    private static object? NormalizeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetSingle(out var f) ? f : (object?)element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.ToString()
    };
    static Exception WriteMappingNotSupported(EntityType type) => throw new NotSupportedException($"Failed to write mapping of not supported entity type '{type}'!");
    #endregion
}
