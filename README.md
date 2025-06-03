# Tc3_MiniFrame

**TwinCAT library** that provides a very basic framework to lineup on a "standard" set of POUs and to provide some primitive functionality.


# PlcHa Gateway

**C# daemon** that connects _Home Assistant_ and _TwinCAT PLC_ for synchronization of process data at runtime.

## Attributes

Decorate _PLC variables_ with the following attributes to enable synchronization:

### Mapping

Creates a mapping between _PLC variable_ and _HA entity_.

`{attribute 'PlcHa.Mapping' := 'sensor.target_haEntity_name'}`

### Source

Specifies a [mapping's](#mapping) source to read an entity's value from.

`{attribute 'PlcHa.Source' := 'name'}`

Supported names are:

- `Ha`
  Read value from _Home Assistant_ and write it to _TwinCAT PLC_.

- `Plc`
  Read value from _TwinCAT PLC_ and write it to _Home Assistant_.

> If undefined, the source is set to `Plc` per default.
