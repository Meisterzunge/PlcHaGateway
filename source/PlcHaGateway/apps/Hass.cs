using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.Client;
using NetDaemon.Client.HomeAssistant.Model;
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
    public static async Task EnsureNativeEntityExistsAsync(this IMapping source, IHaContext ha, IHomeAssistantRunner runner, CancellationToken cancel)
    {
        if (!source.IsDateTimeNativeMapping())
            return;
        if (!source.EntityId.StartsWith("input_datetime.", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Date/time mapping '{source}' must target an input_datetime entity.");
        if (ha.GetAllEntities().Any(e => e.EntityId.Equals(source.EntityId, StringComparison.OrdinalIgnoreCase)))
            return;

        var conn = runner.CurrentConnection;
        if (conn is null)
            throw new InvalidOperationException($"Cannot create native helper '{source.EntityId}' because HA connection is unavailable.");

        var command = source.BuildInputDatetimeCreateCommand();
        await conn
            .SendCommandAndReturnResponseRawAsync(command, cancel)
            .ConfigureAwait(false);

        LogEvent.Hass.LogInformation("Created native input_datetime helper {0} for mapping {1}.", source.EntityId, source.Symbol.InstancePath);
    }

    public static Task BindToNativeEntity(this IMapping source, IHaContext ha)
    {
        // Validate:
        if (source.Backend != IntegrationType.Native)
            throw new InvalidOperationException($"Failed to create native entity for mapping of backend '{source.Backend}'!");
        if (source.SymbolType.IsPhysicalType())
            throw new InvalidOperationException($"Failed to create native entity for mapping of physical type '{source.SymbolType}'!");

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
    public static Task WriteMappingsAsync(this IEnumerable<IMapping> source)
    {
        var mapped = source
            .Select(m => (Mapping: m, Entity: m.TryGetAssociatedEntity()))
            .ToArray();

        foreach (var item in mapped.Where(i => i.Entity is null))
        {
            item.Mapping.ResetDirty();
            LogEvent.Hass.LogWarning("Skipping native update for unbound mapping {0}.", item.Mapping);
        }

        return Task.WhenAll(mapped
            .Where(i => i.Entity is not null)
            .Select(i => i.Mapping.WriteMappingAsync()));
    }
    /// <summary>
    /// Writes mapping to HASS.
    /// </summary>
    public static Task WriteMappingAsync(this IMapping source)
    {
        if (!source.SymbolType.IsOperationalType())
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
            case DateMapping dMapping:
                entity.CallService("set_datetime", new { date = dMapping.LocalDate.ToString("yyyy-MM-dd") });
                break;
            case TimeMapping tMapping:
                entity.CallService("set_datetime", new { time = string.Format("{0:00}:{1:00}:00", tMapping.LocalTime.Hours, tMapping.LocalTime.Minutes) });
                break;
            case DateTimeMapping dtMapping:
                var dt = dtMapping.LocalDateTime;
                entity.CallService("set_datetime", new { datetime = dt.ToString("yyyy-MM-dd HH:mm:ss") });
                break;

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
            case DateMapping:
            case TimeMapping:
            case DateTimeMapping:
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

    private static bool IsDateTimeNativeMapping(this IMapping source) => source is DateMapping or TimeMapping or DateTimeMapping;

    private static CreateInputDatetimeCommand BuildInputDatetimeCreateCommand(this IMapping source)
    {
        var (hasDate, hasTime) = source switch
        {
            DateMapping => (true, false),
            TimeMapping => (false, true),
            DateTimeMapping => (true, true),
            _ => throw new NotSupportedException($"Unsupported datetime mapping type '{source.GetType().Name}'.")
        };

        // HA's input_datetime/create API derives entity_id from the name field.
        // Use the mapped object id to keep helper entity_id deterministic.
        var objectId = source.EntityId.Split('.', 2).Last();
        var createName = string.IsNullOrWhiteSpace(objectId) ? source.Name : objectId;

        return new CreateInputDatetimeCommand
        {
            Name = createName,
            HasDate = hasDate,
            HasTime = hasTime
        };
    }

    private sealed record CreateInputDatetimeCommand : CommandMessage
    {
        public CreateInputDatetimeCommand() { Type = "input_datetime/create"; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
        [JsonPropertyName("has_date")]
        public bool HasDate { get; init; }
        [JsonPropertyName("has_time")]
        public bool HasTime { get; init; }
    }

    static Exception WriteMappingNotSupported(EntityType type) => throw new NotSupportedException($"Failed to write mapping of not supported entity type '{type}'!");
    #endregion
}
