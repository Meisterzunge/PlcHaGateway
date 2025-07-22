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

### Virtual device

The top most view is automatically considered a _virtual device_ wich will be mapped as such to the MQTT integration!
It will group all nested [MFFB's](#function-blocks).

## Attributes

Decorate the declared function blocks with the following attributes to associate it with the desired _Home Assistant entity_.

Once created, a mapping will be used to keep the symbol and entity synchronous.

[Operational function blocks](#function-blocks) will be synchronized from _Home Assistant_ to _TwinCAT PLC_ and vice versa.
All other [operational function blocks](#function-blocks) will synchronize from _TwinCAT PLC_ to _Home Assistant_ only!

### Mapping

Creates a mapping between _PLC variable_ and _HA entity_.

**Syntax:** 

- Map some **native** entity:

  Specify the full qualified path, including the [domain](https://www.home-assistant.io/docs/configuration/entities_domains/#domains), of the target entity.

  `{attribute 'PlcHa.Mapping' := 'domain.haEntity_path'}`

  > Use this mapping when binding to some **existing entity** wich may be part of some [integration](https://www.home-assistant.io/integrations) (e.g. [sun](https://www.home-assistant.io/integrations/sun)).

  > [Physical MFFB's](#function-blocks) (e.g. `FB_Mfr_AO` or `FB_Mfr_BI`) are not allowed for _native_ mappings!

- Map some **relative** entity:

  Specify the entity path.

  `{attribute 'PlcHa.Mapping' := 'haEntity_path'}`

  When aggregating at least two [MFFB's](#function-blocks) (e.g. `FB_Mfr_View`.`FB_Mfr_BI`) the resulting entity's **full qualified path will be concatenated** from all the aggregated [MFFB's](#function-blocks) _mapping_ attribute decorations.
  
  > The entities [domain](https://www.home-assistant.io/docs/configuration/entities_domains/#domains) will be infered from the [MFFB](#function-blocks) type used.

### Name

Specifies a mapped entities _friendly name_.

- **Syntax:** `{attribute 'PlcHa.Name' := 'name'}`
- **Use-cases:**
  All entities, wich are in case all [MFFB's](#function-blocks), but `FB_Mfr_View`.
  Only exception is the [virtul device view](#virtual-device).

> This attribute is mandatory!
  If omitted, the [MFFB's](#function-blocks) instance name will be used as fallback.

### Model

Specifies a mapped devices _model name_.

- **Syntax:** `{attribute 'PlcHa.Model' := 'model'}`
- **Use-cases:** [virtul device](#virtual-device)

> This attribute is mandatory!
  If omitted, the _TwinCAT_ project name will be used as fallback.

### Manufacturer

Specifies a mapped devices _manufacturer name_.

- **Syntax:** `{attribute 'PlcHa.Manufacturer' := 'manufacturer'}`
- **Use-cases:** [virtul device](#virtual-device)

> This attribute is optional.

### Version

Specifies a mapped devices _version_.

- **Syntax:** `{attribute 'PlcHa.Version' := 'version'}`
- **Use-cases:** [virtul device](#virtual-device)

> This attribute is optional.
  If omitted, but a [global version structure](https://infosys.beckhoff.com/english.php?content=../content/1033/tc3_plc_intro/714823819.html&id=) was declared within the _TwinCAT_ it will be used as fallback.

### Enum

Specifies a [multistate mapping's](#function-blocks) enumeration type.
Each entitie's state is mapped to it's distinct PLC context counter part.

- **Syntax:** `{attribute 'PlcHa.Enum' := 'plc_enum_datatype'}`
- **Use-cases:** `FB_Mfr_MVal`, `FB_Mfr_MValOp`

### Specific attributes

There are specific attributes that can be used to describe _home assistant entities_ more precisely:

- `deviceclass` (e.g. [binary_sensor](https://www.home-assistant.io/integrations/binary_sensor/#device-class), [sensor](https://www.home-assistant.io/integrations/sensor#device-class))

- [`icon`](https://www.home-assistant.io/docs/frontend/icons)

> _Home assistant_ will apply each attribute when creating mapped entities.


# PlcHa Gateway

**C# daemon** that connects _Home Assistant_ and _TwinCAT PLC_ for synchronization of process data at runtime.

TODO: multistate entity:
state are generated from the enum DataType. but only state that begins with capital letters!
e.g. `invalid` or `_invalid` will be ignored.

## Export

### HMI export

Creates a `*.yaml` file (one per declared [device](#virtual-device)) wich contains a _ready-to-use_ configuration for a [floorplan](https://experiencelovelace.github.io/ha-floorplan) page.

Pages visualize any [MFFB's](#function-blocks) that are declared within a [device](#virtual-device) as ether _graphical_ or _textual_ entity: 

- **Graphical entity:**

  Entity will be associated with a certain _element_ of the _svg image_.
  This can be a _text element_ to render a numeric value, a _rectangle element_ to render a boolean value's `off` and `on` states, or something else.

- **Textual entity:**
  
  Entity will be represented as one of multiple _textual elements_ in a list beside the page's _svg image_.

> Refer to the [floorplan documentation](https://experiencelovelace.github.io/ha-floorplan/docs/usage) how to configure entities via `yaml`.

#### Configuration

Take advantage of exports via `appsettings.json` configuration.

Here's an **exemple** that:

- declares a `Pumps` action to render associated _rectangle element's_ background dependant on it's entities state.
- maps all [MFFB's](#function-blocks) that declared the `pump` [icon attribute](#specific-attributes).
- overrides the default card's name to `Common`.

```
{
    "Export": {
        "Hmi": {
            "Enable": true,
            "Image": "/local/hmi.{0}.svg",
            "Stylesheet": "/local/hmi.css",
            "Rules": [ {
                "Name": "Pumps",
                "Icon": "pump",
                "Actions": [ {
                    "Service": "floorplan.class_set",
                    "ServiceData": {
                        "Class": "background-${entity.state}"
                } } ]
            } ],
            "Cards": {
                "Default": "Common"
            }
        }
    }
}
```

The **important fields** explained:

- **Image:**

  Filepath of the `*.svg` image to be used.

  The `{0}` is a placeholder for the _page name_, wich will be inferred from the corresponding device.

- **Rules:**

  Defines [rules](https://experiencelovelace.github.io/ha-floorplan/docs/usage/#rules) to visualize [MFFB's](#function-blocks).

  - **Assign entities to rules:**

    Depending on the decorated `Icon` or `DeviceClass` [attribute](#specific-attributes), an [MFFB](#function-blocks) will be rendered as _graphical_ [entity](#hmi-export) if the attributes matches with a rule.
    If not, wich is the default, it will be rendered as _textual_ [entity](#hmi-export).

    Any [MFFB](#function-blocks) is tried to assign

  - **Actions:**
    
    [Actions](https://experiencelovelace.github.io/ha-floorplan/docs/usage/#actions) specify how to render [entities](#hmi-export) by utilizing specific [services](https://experiencelovelace.github.io/ha-floorplan/docs/usage/#services).

    To define an [action](https://experiencelovelace.github.io/ha-floorplan/docs/usage/#actions) in `appsettings.json`, just translate it's individual `yaml` notation into `json` syntax.

    > [Actions](https://experiencelovelace.github.io/ha-floorplan/docs/usage/#actions) will not be validated and written _as declared_!

    > Feel free to use _camel-case_ notations, wich translates automatically into the _floorplan_ conform style.
      E.g. `ServiceData` translates into `service_data`

- **Cards:**
  
  Cards contain all _textual_ [entities](#hmi-export) hence they are not assigned to any rules.

  Aggregated [MFFB](#function-blocks) (e.g. `device`._`parent`_.`entity`) will be grouped by their parents. And there will be one card per group.
  A default card takes all remaining, unparented [MFFB's](#function-blocks).

  - **Default:**
    
    Specifies the default's cards name, wich is `general` if unspecified.

  
### Mapping export

Creates a `*.json` file wich is intended to provide all [mappings](#mapping) in a general format.

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
