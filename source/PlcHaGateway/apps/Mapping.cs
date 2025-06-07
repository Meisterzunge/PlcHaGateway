using System.Linq;
using NetDaemon.HassModel.Entities;

namespace HassModel;


public interface IMapping
{
    public Entity Entity { get; }
}
public abstract class Mapping<T> : IMapping
    where T : Entity
{
    internal Mapping(Entity entity)
    {
        this.Entity = (T)entity;
    }


    #region Properties.Management
    Entity IMapping.Entity => Entity;
    public T Entity { get; }
    #endregion
}
public class InputNumnerMapping : Mapping<NumericEntity>
{
    internal InputNumnerMapping(NumericEntity entity) : base(entity) { }


    #region Properties
    public double? value
    {
        get => Entity.State;
        set => Entity.CallService("set_value", new { value = value });
    }
    #endregion
}

public sealed class MappingFactory
{
    public static IMapping CreateMapping(Entity entity)
    {
        var entityType = entity.EntityId.Split('.').First();
        switch (entityType)
        {
            case "input_number": return (new InputNumnerMapping(entity.AsNumeric()));

            default: throw new NotSupportedException($"Failed to create mapping for not supported entity type '{entityType}'!");
        }
    }
}