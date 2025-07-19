using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using TwinCAT.Ads;
using Utilities.Core;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeTypeResolvers;

namespace HassModel;


internal struct Rule
{
    public Rule() { }


    public string Name { init; get; }
    public string? DeviceClass { init; get; }
    public string? Icon { init; get; }
    public YamlSequenceNode Actions { init; get; }
    public IReadOnlyList<IMapping> Entities => entities;
    private List<IMapping> entities = new();


    public void AddEntities(IEnumerable<IMapping> entities) => this.entities.AddRange(entities);


    public override string ToString() => $"{Name} ({Entities.Count})";
}

public partial class PlcHaGatewayApp
{
    #region Constants
    static readonly string HmiExportFilePath = Path.Combine(ExportFolderPath, "{0}");
    static readonly int HmiDefaultWidth = 500;
    static readonly string HmiDefaultCardName = "General";
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
        var width = config.GetValue<int>("Export:Hmi:Width", HmiDefaultWidth);
        if (string.IsNullOrEmpty(image))
            throw new ArgumentException($"Unspecified HMI image filename!");
        image = string.Format(image, device.ToString());

        var stylesheet = config.GetValue<string>("Export:Hmi:Stylesheet");
        var fileName = Path.ChangeExtension(Path.GetFileName(image), "yaml");

        // Determine mappings:
        var mappings = device.Mappings
            .Select(m => (
                Mapping: m,
                DeviceClass: m.Symbol.TryGetMappingParameterAttribute(PlcMappingParameter.DeviceClass)?.Value.ToLower(),
                Icon: m.Symbol.TryGetMappingParameterAttribute(PlcMappingParameter.Icon)?.Value.Split(':').Last().ToLower()
            ))
            .ToList();

        // Determine configured rules:
        var cfgRules = config.GetRules();
        // Move entities to assigned rules:
        Func<string?, string?, bool> CompareAttribute = (s1, s2) => (s1 is not null) && (string.Equals(s1, s2));
        foreach (var rule in cfgRules)
            rule.AddEntities(mappings
                .PopWhere(m =>
                    CompareAttribute(m.DeviceClass, rule.DeviceClass) ||
                    CompareAttribute(m.Icon, rule.Icon))
                .Select(m => m.Mapping));
        // Select applied rules:
        var rules = cfgRules
            .Where(r => !r.Entities.IsEmpty())
            .ToArray();

        // Create cards for remaining entities:
        var defCard = config.GetValue<string>("Export:Hmi:Cards:Default", HmiDefaultCardName);
        Func<IMapping, string> GetCardName = (m) =>
        {
            if (m.Parent is null)
                return (defCard);
            else
                return (m.Parent.TryGetMappingParameterAttribute(PlcMappingParameter.Name)?.Value ?? m.Parent.InstanceName);
        };
        var cards = mappings
            .Select(m => (Mapping: m.Mapping, CardName: GetCardName(m.Mapping)))
            .GroupBy(m => m.CardName)
            .ToDictionary(
                grp => grp.Key,
                grp => grp
                    .Select(m => m.Mapping)
                    .ToArray()
            );

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
                                                new YamlScalarNode("width"), new YamlScalarNode($"{width}px")),
                                            new YamlScalarNode("config"), new YamlMappingNode(
                                                new YamlScalarNode("image"), new YamlScalarNode(image),
                                                new YamlScalarNode("stylesheet"), new YamlScalarNode(stylesheet),
                                                new YamlScalarNode("defaults"), new YamlMappingNode(
                                                    new YamlScalarNode("hover_action"), new YamlScalarNode("hover-info"),
                                                    new YamlScalarNode("tap_action"), new YamlScalarNode("more-info")),
                                                new YamlScalarNode("rules"), new YamlSequenceNode(
                                                    // Add rules:
                                                    rules.Select(CreateRuleNode)
                                                )),
                                            new YamlScalarNode("grid_options"), new YamlMappingNode(
                                                new YamlScalarNode("columns"), new YamlScalarNode("full"))))),
                                new YamlMappingNode(
                                    new YamlScalarNode("type"), new YamlScalarNode("grid"),
                                    new YamlScalarNode("cards"), new YamlSequenceNode(
                                        // Add cards:
                                        cards.Select(c => new YamlMappingNode(
                                            new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("type"), new YamlScalarNode("entities")),
                                            new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("title"), new YamlScalarNode(c.Key)),
                                            c.Value.ToEntitySequence()
                                        )))))))
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
    private YamlMappingNode CreateRuleNode(Rule rule) => CreateRuleNode(rule.Name, rule.Entities, rule.Actions);
    private YamlMappingNode CreateRuleNode(string name, IEnumerable<IMapping> mappings, YamlSequenceNode actions) => new YamlMappingNode(
        new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("name"), new YamlScalarNode(name)),
        mappings.ToEntitySequence(),
        new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode("state_action"), actions));
}

internal static partial class Ext
{
    public static KeyValuePair<YamlNode, YamlNode> ToEntitySequence(this IEnumerable<IMapping> source) => new KeyValuePair<YamlNode, YamlNode>(
        new YamlScalarNode("entities"), new YamlSequenceNode(source.Select(ToMappingNode))
    );
    public static YamlMappingNode ToMappingNode(this IMapping source) => new YamlMappingNode(
        new YamlScalarNode("entity"), new YamlScalarNode(source.FullyQualifiedId),
        new YamlScalarNode("element"), new YamlScalarNode($"cell-{source.EntityId}")
    );

    public static Rule[] GetRules(this IConfiguration source) => source
        .GetSection("Export:Hmi:Rules")
        .GetChildren()
        .Select(r => new Rule()
        {
            Name = r.GetValue<string>("Name")!,
            DeviceClass = r.GetValue<string>("DeviceClass")?.ToLower(),
            Icon = r.GetValue<string>("Icon")?.ToLower(),
            Actions = new YamlSequenceNode(r
                .GetSection("Actions")
                .GetChildren()
                .Select(GetActionNode)),
        })
        .ToArray();
    private static YamlMappingNode GetActionNode(this IConfigurationSection source)
    {
        var result = new YamlMappingNode();
        foreach (var entry in source.GetChildren())
        {
            var key = entry.Key.FromCamelCase();
            if (entry.Value is null)
                // Create and add nested node:
                result.Add(new YamlScalarNode(key), GetActionNode(entry));
            else
                result.Add(new YamlScalarNode(key), new YamlScalarNode(entry.Value!));
        }
        return (result);
    }
}