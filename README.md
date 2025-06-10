# Tc3_MiniFrame

**TwinCAT library** that provides a very basic framework to lineup on a "standard" set of POUs and to provide some primitive functionality.

## Function Blocks

There are several _mini framework function blocks_ (MFFB) in the `Tc3_MiniFrame` library that can be used to create mappings:

- `FB_Mfr_AI` | Analog input
- `FB_Mfr_AO` | Analog output
- `FB_Mfr_AVal` | Analog value for display
- `FB_Mfr_AValOp` | Analog value for operation

- `FB_Mfr_BI` | Binary input
- `FB_Mfr_BO` | Binary output
- `FB_Mfr_BVal` | Binary value for display
- `FB_Mfr_BValOp` | Binary value for operation

- `FB_Mfr_MVal` | Multistate value for display
- `FB_Mfr_MValOp` | Multistate value for operation

- `FB_Mfr_View` | View

  Should be used when aggregating MFFB's.

  > The top most view is automatically considered a _virtual device_ wich will be mapped as such to the MQTT integration!
    It will group all nested MFFB's.

## Attributes

Decorate the declared function blocks with the following attributes to associate it with the desired _Home Assistant entity_.

Once created, a mapping will be used to keep the symbol and entity synchronous.

### Mapping

Creates a mapping between _PLC variable_ and _HA entity_.

Possible declaraions:

- Map some **explicit** entity:

  Specify the full qualified path of the target entity.

  `{attribute 'PlcHa.Mapping' := 'full_haEntity_path'}`

- Map some **relative** entity:

  Useful when aggregating at least two MFFB's (e.g. `FB_Mfr_View`.`FB_Mfr_BI`).
  The target **entity's full qualified path will be concatenated** from all the aggregated MFFB's mapping attribute decorations.
  
  `{attribute 'PlcHa.Mapping' := 'ha_entity_name'}`

> Note that declaring the **entity type** (like `binary_sensor`) is optional.
  It will be infered from the MFFB type if not specified.

### Enum

Specifies a [multistate mapping's](#function-blocks) enumeration type.
Each entitie's state is mapped to it's distinct PLC context counter part.

`{attribute 'PlcHa.Enum' := 'plc_enum_datatype'}`

### Source

Specifies a [mapping's](#mapping) source to read an entity's value from.

`{attribute 'PlcHa.Source' := 'context'}`

Supported contexts are:

- `Ha`
  Read value from _Home Assistant_ and write it to _TwinCAT PLC_.

- `Plc`
  Read value from _TwinCAT PLC_ and write it to _Home Assistant_.

> If undefined, the source is set to `Plc` per default.


# PlcHa Gateway

**C# daemon** that connects _Home Assistant_ and _TwinCAT PLC_ for synchronization of process data at runtime.

## Entity configuration generator

TODO: creates a `*.yaml` of all MFFB's, found in PLC.

TODO: multistate entity:
state are generated from the enum DataType. but only state that begins with capital letters!
e.g. `invalid` or `_invalid` will be ignored.

## Development

For _visual studio code_ create a `appsettings.Development.json` file to maintain developer settings excluded from the git repository:

```
{
    "HomeAssistant": {
        "Host": "__hass_hostName_or_ipAddress__",
        "Port": 8123,
        "Ssl": false,
        "Token": "__hass_api_token__"
    },
    "Plc": {
        "NetId": "127.0.0.1.1.1",
        "Port": 851
    }
}
```
