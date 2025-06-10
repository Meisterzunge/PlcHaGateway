using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NetDaemon.Extensions.MqttEntityManager;
using NetDaemon.HassModel.Entities;
using Utilities.Core;

namespace HassModel;


/// <summary>
/// Manage to connect <i>Home Assistant</i> and <i>TwinCAT PLC</i>.
/// </summary>
[NetDaemonApp]
public class PlcHaGatewayApp : IAsyncInitializable, IDisposable
{
    public PlcHaGatewayApp(IConfiguration config, ILogger<PlcHaGatewayApp> logger, IHaContext context, IMqttEntityManager entityManager)
    {
        new Common(logger);

        this.plc = config.CreatePlcFromSettings();
        this.ha = context;
        this.entityManager = entityManager;
    }


    #region Properties.Management
    //private CancellationToken ConnectionToken => connectionToken.Token;
    //private CancellationTokenSource connectionToken = new();

    public IEnumerable<IMapping> Mappings => DeviceMappings
        .SelectMany(d => d.Mappings)
        .Concat(SymbolMappings);
    public IReadOnlyCollection<VirtualDevice> DeviceMappings;
    public IReadOnlyCollection<IMapping> SymbolMappings;
    #endregion


    #region Initialization
    /*
    public async Task TestDev()
    {
        var _entityManager = entityManager;
        var create = false;


        // BINARY_SENSOR:
        if (create)
        {
            await entityManager
                .CreateAsync("binary_sensor.test2_sensor2", new EntityCreationOptions(
                    DeviceClass: "connectivity",
                    UniqueId: "1234-11-22-40",
                    Name: "Some binary sensor"))
                .ConfigureAwait(false);
        }
        await _entityManager.SetStateAsync("binary_sensor.test2_sensor2", "ON").ConfigureAwait(false);

        // NUMBER:
        if (create)
        {
            await entityManager
                .CreateAsync("number.test2_number2", new EntityCreationOptions(
                    DeviceClass: "temperature",
                    UniqueId: "12AA-11-22-50",
                    Name: "Some number"))
                .ConfigureAwait(false);
        }
        await _entityManager.SetStateAsync("number.test2_number2", "1").ConfigureAwait(false);

        // ---------------


        // This device will have five sensors. We tie all of the sensors together
        // by sharing the same `identifiers` list with each sensor.
        var identifiers = new[] { "test_car_charger" };

        // It is important that all sensors share the same State Topic so that
        // we can update all values in one go.
        // You will see that in each sensor, the `value_template` defines how
        // we extract the sensor value from the multiple update.
        var stateTopic = "homeassistant/sensor/test_car_charger/state";

        // First we define the device that will own all the sensors. This is passed
        // when we create the first of the sensors.
        var device = new { identifiers = identifiers, name = "Car Charger", model = "ABC X1", manufacturer = "Voltium", sw_version = 1.22 };

        if (create)
        {
            // ANALOG INPUT/OUTPUT
            // Create the first sensor for temperature. This requires a unique entity ID
            // and value_template, but needs to include the shared state topic and device info
            await _entityManager.CreateAsync("sensor.test_car_charger_temperature", new EntityCreationOptions
            {
                Name = "Temperature",
                DeviceClass = "temperature",
            }, new
            {
                unit_of_measurement = "\u00b0C",
                state_topic = stateTopic,   // Note the override of the state topic
                value_template = "{{ value_json.temperature }}", // and value from state
                device             // Links the sensors together
            });

            // ANALOG INPUT (Battery)
            // ...followed by a battery sensor. This has a special meaning in Home Assistant
            // as entities with the device class of `battery` can be used in automations to
            // identify which are running low
            await _entityManager.CreateAsync("sensor.test_car_charger_battery", new EntityCreationOptions
            {
                Name = "Battery",
                DeviceClass = "battery"
            }, new
            {
                unit_of_measurement = "%",
                state_topic = stateTopic,
                value_template = "{{ value_json.battery }}",
                device
            });

            // SENSOR (As string)
            // and finally, a mode sensor that can be represented as a string
            await _entityManager.CreateAsync("sensor.test_car_charger_mode", new EntityCreationOptions
            {
                Name = "Mode",
            }, new
            {
                icon = "mdi:list-status",
                state_topic = stateTopic,
                value_template = "{{ value_json.mode }}",
                device
            });

            // BINARY_SENSOR:
            await _entityManager.CreateAsync("binary_sensor.test_car_charger_feedback", new EntityCreationOptions
            {
                Name = "Feedback",
                DeviceClass = "problem"
            }, new
            {
                icon = "mdi:toggle-switch",
                state_topic = stateTopic,   // Note the override of the state topic
                value_template = "{{ value_json.feedback }}", // and value from state
                device             // Links the sensors together
            });

            // NUMBER (Setpoint)
            await _entityManager.CreateAsync("number.test_car_charger_setpoint", new EntityCreationOptions
            {
                Name = "Setpoint",
                DeviceClass = "temperature"
            }, new
            {
                unit_of_measurement = "\u00b0C",
                icon = "mdi:temperature-celsius",
                //mode = "slider",
                initial = 21,
                min = 15,
                max = 28,
                step = 0.1,
                state_topic = stateTopic,   // Note the override of the state topic
                value_template = "{{ value_json.setpoint }}", // and value from state
                device             // Links the sensors together
            });
        }

        // SUBSCRIBE TO UPDATES:
        (await _entityManager.PrepareCommandSubscriptionAsync("number.test_car_charger_setpoint").ConfigureAwait(false))
            .Subscribe(new Action<string>(async state =>
            {
                await _entityManager.SetStateAsync("number.test_car_charger_setpoint", state).ConfigureAwait(false);
            }));

        // Now that we have everything set up we can post an update to the shared state topic.
        // This needs to be a JSON string comprising all of the values we want to set so let's
        // start with a dynamic object and then JSON it
        var newState = new
        {
            temperature = DateTime.Now.Second,
            battery = 80,
            mode = "Idle",
            feedback = "ON",
            setpoint = 21
        };
        await _entityManager.SetStateAsync("sensor.test_car_charger", JsonSerializer.Serialize(newState));

    }
    */
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        /*
        /// # add ti wiki or so
        /// [MQTT entity-types](https://www.home-assistant.io/integrations/mqtt/#configuration)
        /// 
        await TestDev().ConfigureAwait(false);
        return;
        */



        LogEvent.Ads.LogInformation("Establish connection.");
        try
        {
            await plc.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent.Ads.LogError(ex, "Failed to establish PLC connection.");
            throw;
        }

        LogEvent.Gw.LogTrace("Creating mappings...");
        try
        {
            this.DeviceMappings = MappingFactory.CreateDevices(plc.MappedDevices);
            this.SymbolMappings = MappingFactory.CreateMappings(plc.MappedSymbols);
            var totalMappings = Mappings.Count();

            if (totalMappings == 0)
                LogEvent.Gw.LogWarning($"No mappings found to create.");
            else
            {
                LogEvent.Gw.LogInformation($"Created {DeviceMappings.Count} virtual device(s).");
                LogEvent.Gw.LogInformation($"Created {totalMappings} mapping(s) total.");
            }
        }
        catch (Exception ex)
        {
            LogEvent.Gw.LogError(ex, "Failed to create mappings.");
            throw;
        }

        LogEvent.Hass.LogTrace("Binding static mappings...");
        // (BETA) ... hass-mappings
        // berücksichtigen dass es auch mappings gibt, die direkt auf entities im `IHaContext` context gemapped werden!
        /*
        var entityId = mapping.EntityId;
        if (!entityId.Contains('.'))
            entityId = $"{mapping.Info.EntityTypeName}.{entityId}";
        */

        LogEvent.Mqtt.LogTrace("Binding MQTT mappings...");
        foreach (var mapping in Mappings)
        {
            try
            {
                await mapping.CreateMqttEntity(entityManager).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogEvent.Mqtt.LogError(ex, "Failed to create MQTT entity '{0}'.", mapping);
            }
        }



        // (BETA) ... Set initial value
        //    RESUME; // wenn alle entities erzeugt sind: nen SetValue über das ganze ding machen
        // vorher noch aktuelle werte aus plc lesen?


        // (BETA) ... TODO
        /*
        var avalOp = (AnalogMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_number.test_number"));
        avalOp.Value = 15;
        //var aval = (AnalogMapping)Mappings.First(m => m.Entity.EntityId.Equals("sensor.test_tfl"));
        //aval.Value = 20;

        var bvalOp = (BooleanMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_boolean.test_toggle"));
        bvalOp.Value = true;

        var mvalOp = (MultistateMapping)Mappings.First(m => m.Entity.EntityId.Equals("input_select.test_sel1"));
        mvalOp.Value = 1;
        */
    }
    public void Dispose() => plc.Dispose();
    #endregion


    private Plc plc;
    private IHaContext ha;
    private IMqttEntityManager entityManager;
}