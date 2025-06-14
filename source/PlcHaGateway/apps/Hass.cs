
// [Legacy code]

// AnalogMapping.SetValue:
/*
switch (Info.EntityType)
{
    /// <see href="??">
    case EntityType.Sensor: throw new NotImplementedException("TODO");
    /// <see href="https://www.home-assistant.io/integrations/input_number/#actions">
    case EntityType.InputNumber: Entity.CallService("set_value", new { value = value }); break;
    default: throw SetValueNotSupported;
}
*/

// BooleanMapping.SetValue:
/*

switch (Info.EntityType)
{
    /// <see href="??">
    case EntityType.BinarySensor: throw new NotImplementedException("TODO");
    /// <see href="https://www.home-assistant.io/integrations/input_boolean/#actions">
    case EntityType.InputBoolean: Entity.CallService((value == true) ? "turn_on" : "turn_off"); break;
    default: throw SetValueNotSupported;
}
*/

// MultistateMapping.SetValue:
/*
switch (Info.EntityType)
{

    /// <see href="??">
    case EntityType.Sensor: throw new NotImplementedException("TODO");
    /// <see href="https://www.home-assistant.io/integrations/input_select/#actions">
    case EntityType.InputMultistate: Entity.CallService("select_option", new { option = Options[value!.Value] }); break;

    default: throw SetValueNotSupported;
}
*/