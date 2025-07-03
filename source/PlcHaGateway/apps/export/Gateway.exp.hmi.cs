using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Utilities.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace HassModel;


public partial class PlcHaGatewayApp
{
    #region Constants
    static readonly string HmiExportFilePath = Path.Combine(ExportFolderPath, "{0}");
    #endregion


    /// <summary>
    /// Creates export files containing HMI (Home Assistant, Floorplan) visualizations (one per device).
    /// </summary>
    private Task CreateHmiExportAsync(CancellationToken cancellationToken) => Task.WhenAll(DeviceMappings.Select(d => CreateHmiExportAsync(d, cancellationToken)));
    /// <summary>
    /// Creates export file containing HMI (Home Assistant, Floorplan) visualization.
    /// </summary>
    private Task CreateHmiExportAsync(VirtualDevice device, CancellationToken cancellationToken)
    {
        LogEvent.Gw.LogTrace($"Generate HMI export '{device}'.");

        // Apply settings:
        var image = config.GetValue<string>("Export:Hmi:Image");
        if (string.IsNullOrEmpty(image))
            throw new ArgumentException($"Unspecified HMI image filename!");
        image = string.Format(image, device.ToString());

        var stylesheet = config.GetValue<string>("Export:Hmi:Stylesheet");
        var fileName = Path.ChangeExtension(Path.GetFileName(image), "yaml");

        // Determine rules:
        var mappings = device.Mappings
            .GroupBy(m => m.Symbol.TryGetMappingParameterAttribute(PlcMappingParameter.DeviceClass)?.Value.ToLower() ?? "")
            .ToDictionary(
                grp => grp.Key,
                grp => grp.ToArray()
            );
        var rules = new List<YamlMappingNode>();
        if (mappings.TryPop("temperature", out var tempMappings))
            rules.Add(CreateRuleNode("Temperature", tempMappings,
                SetTemperatureService(),
                SetClassService("static-temp")));
        
        // TODO: Add all other/remaining mappings
        RESUME;
        
        rules.Add(CreateRuleNode("Pump", device.Mappings[2..4], SetClassService("background-${entity.state}")));

        // VALVE
        // (BETA) ...

        // STW
        // (BETA) ...

        // ALL UNKNOWN HERE
        // (BETA) ...

        // Create YAML:
        var stream = new YamlStream(
            new YamlDocument(
                new YamlMappingNode(

                new YamlScalarNode("views"), new YamlSequenceNode(
                    new YamlMappingNode(
                    new YamlScalarNode("title"), new YamlScalarNode(device.Name),
                    new YamlScalarNode("type"), new YamlScalarNode("sections"),
                    new YamlScalarNode("max_columns"), new YamlScalarNode("4"),
                    new YamlScalarNode("sections"), new YamlSequenceNode(
                        new YamlMappingNode(
                        new YamlScalarNode("type"), new YamlScalarNode("grid"),
                        new YamlScalarNode("column_span"), new YamlScalarNode("2"),
                        new YamlScalarNode("cards"), new YamlSequenceNode(
                            new YamlMappingNode(
                            new YamlScalarNode("type"), new YamlScalarNode("custom:floorplan-card"),
                            new YamlScalarNode("styles"), new YamlMappingNode(
                                new YamlScalarNode("width"), new YamlScalarNode("500px")),
                            new YamlScalarNode("config"), new YamlMappingNode(
                                new YamlScalarNode("image"), new YamlScalarNode(image),
                                new YamlScalarNode("stylesheet"), new YamlScalarNode(stylesheet),
                                new YamlScalarNode("defaults"), new YamlMappingNode(
                                new YamlScalarNode("hover_action"), new YamlScalarNode("hover-info"),
                                new YamlScalarNode("tap_action"), new YamlScalarNode("more-info")),
                                new YamlScalarNode("rules"), new YamlSequenceNode(
                                    rules
                            )),
                            new YamlScalarNode("grid_options"), new YamlMappingNode(
                                new YamlScalarNode("columns"), new YamlScalarNode("full"))))),
                        new YamlMappingNode(
                        new YamlScalarNode("type"), new YamlScalarNode("grid"),
                        new YamlScalarNode("cards"), new YamlSequenceNode(
                            new YamlMappingNode(
                            new YamlScalarNode("type"), new YamlScalarNode("entities"),
                            new YamlScalarNode("title"), new YamlScalarNode("Bischen Elektro"),
                            new YamlScalarNode("entities"), new YamlSequenceNode(
                                new YamlMappingNode(
                                new YamlScalarNode("entity"), new YamlScalarNode("sensor.shelly_uv01_phase_b_active_power_average")),
                                new YamlMappingNode(
                                new YamlScalarNode("entity"), new YamlScalarNode("sensor.shelly_uv01_cumulated_active_power_average")))),
                            new YamlMappingNode(
                            new YamlScalarNode("type"), new YamlScalarNode("entities"),
                            new YamlScalarNode("title"), new YamlScalarNode("Etwas Sonne"),
                            new YamlScalarNode("entities"), new YamlSequenceNode(
                                new YamlMappingNode(
                                new YamlScalarNode("entity"), new YamlScalarNode("sensor.sun_next_dawn")),
                                new YamlMappingNode(
                                new YamlScalarNode("entity"), new YamlScalarNode("sensor.sun_next_dusk")))))))))
            )));

        // Write file:
        var path = string.Format(HmiExportFilePath, fileName);
        var writer = new StringWriter();
        {
            stream.Save(writer, false);
        }
        File.WriteAllText(path, writer.ToString());

        return (Task.CompletedTask);
    }
    private YamlMappingNode CreateRuleNode(string name, IEnumerable<IMapping> mappings, params YamlMappingNode[] actions) => new YamlMappingNode(
        new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("name"), new YamlScalarNode(name)),
        mappings.ToEntitySequence(),
        new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("state_action"), new YamlSequenceNode(actions)));

    private YamlMappingNode SetTemperatureService() => new YamlMappingNode(
        new YamlScalarNode("service"), new YamlScalarNode("floorplan.text_set"),
        new YamlScalarNode("service_data"), new YamlScalarNode(@"${ (entity.state !== undefined) ? (Math.round(entity.state * 10) / 10 + ""°"") : ""unknown"" }") { Style = ScalarStyle.Literal }
    );
    private YamlMappingNode SetClassService(string className) => new YamlMappingNode(
        new YamlScalarNode("service"), new YamlScalarNode("floorplan.class_set"),
        new YamlScalarNode("service_data"), new YamlMappingNode(
            new YamlScalarNode("class"), new YamlScalarNode(className)));
}

internal static partial class Ext
{
    public static KeyValuePair<YamlNode, YamlNode> ToEntitySequence(this IEnumerable<IMapping> source) => new KeyValuePair<YamlNode, YamlNode>(
        new YamlScalarNode("entities"),
        new YamlSequenceNode(source.Select(ToMappingNode))
    );
    public static YamlMappingNode ToMappingNode(this IMapping source) => new YamlMappingNode(
        new YamlScalarNode("entity"), new YamlScalarNode(source.FullyQualifiedId),
        new YamlScalarNode("element"), new YamlScalarNode($"cell-{source.EntityId}")
    );
}