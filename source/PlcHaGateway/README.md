# PlcHaGateway

NetDaemon 5 (.NET 9) daemon bridging a **TwinCAT PLC** (via ADS) with **Home Assistant** (MQTT / native entities).

## Architecture

```mermaid
flowchart LR
  plc[TwinCAT PLC] -- ADS --> gw[PlcHaGateway]
  gw -- MQTT --> haMqtt[Home Assistant\n(MQTT entities)]
  gw -- native --> haNative[Home Assistant\n(entity state + attributes)]
```

## PLC Mapping Attributes

Apply these `{attribute}` pragmas to mapped TwinCAT symbol declarations. Supported targets are MiniFrame MFFBs and primitive child variables declared inside a view hierarchy:

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

### Parameter overrides in virtual devices

Mapped parameters are still collected from the mapped entity itself. In addition, any parent on the mapped symbol path can override a descendant mapping parameter by targeting that symbol in braces:

- Syntax: `PlcHa.<Parameter>[<RelativeTargetPath>]`
- Example parameter names: `PlcHa.Enum[ManOvrd]`, `PlcHa.Enum[OuterAggregate.ManOvrd]`
- Scope: override lookup is available for symbols inside a `FB_Mfr_View` virtual-device subtree (the virtual device itself and its descendants)
- Search path: from the mapped symbol upward through the full parent/inheritance tree to the virtual-device root (inclusive), never beyond
- Precedence: the most upward/outer matching parent override wins; if none exists, the mapped symbol attribute/default behavior is used

Example chain: initial declaration, first override, second override.

1. Initial declaration in the template (`FB_UnitBool`)

```st
FUNCTION_BLOCK FB_UnitBool EXTENDS FB_Mfr_View
VAR
  {attribute 'PlcHa.Mapping'}
  {attribute 'PlcHa.Name' := 'Manual override'}
  {attribute 'PlcHa.Enum' := 'E_OnOffA'}
  ManOvrd : FB_Mfr_MValOp;
END_VAR
```

2. First override on the direct child instance (`VlvLoad`) within some outer aggregate (`FB_Aggregate`)

```st
FUNCTION_BLOCK FB_Aggregate EXTENDS FB_Mfr_View
VAR
  {attribute 'PlcHa.Mapping'}
  {attribute 'PlcHa.Name' := 'Load valve'}
  {attribute 'PlcHa.Enum[ManOvrd]' := 'E_OpnClsA'}
  VlvLoad : FB_UnitBool;
END_VAR
```

3. Second override on a higher parent that wraps the `OuterAggregate`

```st
FUNCTION_BLOCK FB_Plant EXTENDS FB_Mfr_View
VAR
  {attribute 'PlcHa.Mapping'}
  {attribute 'PlcHa.Name' := 'Aggregate'}
  {attribute 'PlcHa.Enum[VlvLoad.ManOvrd]' := 'E_UpDownA'}
  Agg : FB_Aggregate;
END_VAR
```

Effective enum on `ManOvrd`: `E_UpDownA` (outermost matching override wins).
Override search stops at `FB_Plant` because it is the virtual-device root.

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

### Custom view subclasses

Root discovery also supports custom function blocks that derive from `Tc3_MiniFrame.FB_Mfr_View`, including multi-level inheritance chains.

- The root view instance itself may omit `PlcHa.Mapping`.
- Discovery only applies to view roots. Arbitrary wrapper or container types are not discovered.
- At least one descendant member must carry `PlcHa.Mapping`.
- Inheritance support is limited to `FB_Mfr_View` roots.
- Descendant mapped members may be supported MiniFrame MFFBs or primitive PLC variables such as `BOOL`, numeric primitives, or enums.
- Primitive child mappings are exposed as read-only value-style entities inferred from datatype: `BOOL` -> `binary_sensor`, numeric primitives -> `sensor`, enums -> enum-style `sensor`.

Example:

```st
fbRoom : FB_CustomRoomView;

FUNCTION_BLOCK FB_CustomRoomView EXTENDS FB_Mfr_View
VAR
  {attribute 'PlcHa.Mapping' := 'window_open'}
  {attribute 'PlcHa.Name' := 'Window open'}
  bWindowOpen : BOOL;

  {attribute 'PlcHa.Mapping' := 'temperature'}
  rTemperature : REAL;
END_VAR
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
