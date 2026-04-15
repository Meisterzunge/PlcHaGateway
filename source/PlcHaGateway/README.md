# PlcHaGateway

NetDaemon 5 (.NET 9) daemon bridging a **TwinCAT PLC** (via ADS) with **Home Assistant** (MQTT / native entities).

## Architecture

```
TwinCAT PLC  <ADS>  PlcHaGateway  <MQTT>  Home Assistant
                                       <native> Home Assistant
```

## PLC Mapping Attributes

Apply these `{attribute}` pragmas to MFFB symbol declarations in TwinCAT:

| Attribute | Required | Example value | Description |
|---|---|---|---|
| `PlcHa.Mapping` | Yes | `Test10` or `sun.sun` or `sun.sun:elevation` | Relative path (MQTT entity) or fully-qualified HA entity ID (native). Add `:attributeName` to read a specific HA attribute instead of entity state. |
| `PlcHa.Name` | No | `Outdoor temperature` | Friendly name shown in HA. |
| `PlcHa.Model` | No | `TD10` | Device model (View FBs only). |
| `PlcHa.Manufacturer` | No | `Acme` | Device manufacturer (View FBs only). |
| `PlcHa.Version` | No | `1.2.3.0` | Firmware version (View FBs only). |
| `PlcHa.DeviceClass` | No | `temperature` | HA device class. |
| `PlcHa.Unit` | No | `°C` | Unit of measurement. |
| `PlcHa.Step` | No | `0.5` | Step for `number` entities. |
| `PlcHa.Min` | No | `0` | Minimum for `number` entities. |
| `PlcHa.Max` | No | `100` | Maximum for `number` entities. |
| `PlcHa.Icon` | No | `mdi:thermometer` | MDI icon. |
| `PlcHa.Enum` | Yes (select) | `E_OffOnTest` | TwinCAT enum type name for `select` / multistate entities. |

### Native binding (state)

Mapping value contains a `.`  binds to an existing HA entity, reads entity state.

```st
{attribute 'PlcHa.Mapping' := 'input_number.setpoint'}
fSetpoint : FB_Mfr_AValOp;
```

### Native binding (attribute)

Append `:attributeName` to read a specific HA entity attribute instead of state. Works for all non-physical MFFB types.

```st
{attribute 'PlcHa.Mapping' := 'sun.sun:elevation'}
{attribute 'PlcHa.Name' := 'Sun elevation'}
fSunElev : FB_Mfr_AVal;
```

```st
{attribute 'PlcHa.Mapping' := 'climate.zone1:current_temperature'}
{attribute 'PlcHa.Name' := 'Zone 1 actual temp'}
fZone1Temp : FB_Mfr_AVal;
```

If the named attribute is absent at runtime, the update is skipped and a warning is logged.

### MQTT entity (relative path)

No `.` in mapping value  creates and manages the entity via MQTT discovery.

```st
{attribute 'PlcHa.Mapping' := 'Test10'}
{attribute 'PlcHa.Name' := 'Test device No°10'}
fbDevice10 : FB_TestDevice;
```

## Configuration (`appsettings.json`)

```json
{
  "Plc": {
    "NetId": "5.30.166.30.1.1",
    "Port": 851
  },
  "CyclicUpdate": 1.0
}
```

## Running

```bash
dotnet run
```

Or deploy as a NetDaemon app via the provided `Dockerfile`.
