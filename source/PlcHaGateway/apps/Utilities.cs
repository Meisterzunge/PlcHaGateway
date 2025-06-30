
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Utilities.Core;


internal static class UtilAssembly
{
    /// <inheritdoc cref="Assembly.Location"/>
    public static string GetLocation(Assembly? aAssembly = null)
    {
        if (aAssembly == null)
            aAssembly = Assembly.GetExecutingAssembly();
        return (aAssembly.Location);
    }
    /// <inheritdoc cref="GetLocation(Assembly)"/>
    public static string GetLocationFolder(Assembly aAssembly = null)
    {
        if (aAssembly == null)
            aAssembly = Assembly.GetExecutingAssembly();
        return (Path.GetDirectoryName(GetLocation(aAssembly)));
    }
    /// <inheritdoc cref="GetDefinedTypesOf{TAttrib}(Assembly[])"/>
    /// <param name="assembly">Assembly to query.</param>
    /// <param name="includeReferencedAssemblies">Queries types of referenced assemblies.</param>
    public static IEnumerable<Type> GetDefinedTypesOf<TAttrib>(Assembly? assembly = null, bool includeReferencedAssemblies = false)
        where TAttrib : Attribute
    {
        if (assembly == null)
            assembly = Assembly.GetExecutingAssembly();

        Assembly[] assemblies;
        if (includeReferencedAssemblies)
            assemblies = assembly.GetReferencedAssemblies()
                .Select(asm => Assembly.Load(asm))
                .Append(assembly)
                .ToArray();
        else
            assemblies = new[] { assembly };
        return (GetDefinedTypesOf<TAttrib>(assemblies));
    }
    /// <summary>
    /// Determines defined types that declear an attrute of the specified type.
    /// </summary>
    /// <typeparam name="TAttrib">Conditional attribute type.</typeparam>
    /// <param name="assemblies">Assemblies to query.</param>
    /// <returns>Enumeration of matching types.</returns>
    public static IEnumerable<Type> GetDefinedTypesOf<TAttrib>(params Assembly[] assemblies)
        where TAttrib : Attribute
    {
        return (assemblies
            .SelectMany(asm => asm.DefinedTypes)
            .Where(t => t.UnderlyingSystemType.HasCustomAttribute<TAttrib>()));
    }
    /// <summary>
    /// Determines defined types that declare an attrute of the specified type to create a (Type|Attribute) map from.
    /// </summary>
    /// <inheritdoc cref="GetDefinedTypesOf"/>
    /// <returns>Dictionary of matching types, grouped per type.</returns>
    public static Dictionary<Type, TAttrib> GetDefinedTypeMap<TAttrib>(Assembly assembly = null)
        where TAttrib : Attribute
    {
        return (GetDefinedTypesOf<TAttrib>(assembly).ToDictionary(
            t => t,
            t => t.GetCustomAttribute<TAttrib>()
        ));
    }
    /// <inheritdoc cref="GetDefinedTypeMap"/>
    /// <param name="keySelector">Selector to chose a attribute member that will be used as key.</param>
    /// <returns>Dictionary of matching types, grouped per chosen attribute member.</returns>
    public static Dictionary<TKey, Type> GetDefinedTypeMap<TAttrib, TKey>(Func<TAttrib, TKey> keySelector, Assembly assembly = null)
        where TAttrib : Attribute
    {
        return (GetDefinedTypesOf<TAttrib>(assembly).ToDictionary(
            t => keySelector(t.GetCustomAttribute<TAttrib>()),
            t => t
        ));
    }
}


internal static class UtilType
{
    public static bool HasCustomAttribute<T>(this Type type, bool inherit = false)
        where T : Attribute
    {
        var attr = type.GetCustomAttributes(typeof(T), inherit);
        return (attr?.Count() > 0);
    }
    public static T GetCustomAttribute<T>(this Type type, bool inherit = false)
        where T : Attribute
    {
        T attrib;
        if (TryGetCustomAttribute<T>(type, out attrib, inherit))
            return (attrib);
        else
            throw new FieldAccessException(string.Format("Failed to get custom attribute of type '{0}'!", typeof(T).Name));
    }
    public static bool TryGetCustomAttribute<T>(this Type type, out T result, bool inherit = false)
        where T : Attribute
    {
        var attr = type.GetCustomAttributes(typeof(T), inherit);
        if ((attr != null) && (attr.Count() == 1))
            result = (T)attr.First();
        else
            result = null;
        return (result is not null);
    }
}
internal static class UtilEnum
{
    /// <summary>
    /// Creates a mapping dictionary of enumerated values that are decorated with a certain attribute.
    /// </summary>
    /// <typeparam name="TEnum">Enumerated type.</typeparam>
    /// <typeparam name="TAttrib">Attribute type.</typeparam>
    public static IReadOnlyDictionary<TEnum, TAttrib> GetCustomAttributes<TEnum, TAttrib>(bool inherit = false)
        where TEnum : struct, Enum
        where TAttrib : Attribute
    {
        return (GetCustomAttributes<TEnum, TAttrib, TEnum, TAttrib>(
            (e, a) => e,
            (e, a) => a,
            inherit
        ));
    }
    /// <inheritdoc cref="GetCustomAttributes"/>
    /// <param name="keySelector">Selector to obtain key.</param>
    /// <param name="valueSelector">Selector to obtain value.</param>
    public static IReadOnlyDictionary<TKey, TVal> GetCustomAttributes<TEnum, TAttrib, TKey, TVal>(Func<TEnum, TAttrib, TKey> keySelector, Func<TEnum, TAttrib, TVal> valueSelector, bool inherit = false)
        where TEnum : struct, Enum
        where TAttrib : Attribute
    {
        var res = new Dictionary<TKey, TVal>();
        foreach (var val in Enum.GetValues<TEnum>())
        {
            if (val.TryGetCustomAttribute<TAttrib, TEnum>(out var attrib, inherit))
                res.Add(keySelector(val, attrib), valueSelector(val, attrib));
        }
        return (res);
    }
    
    public static string GetDescription(Type enumType, int value, bool fallback = true) => GetDescription((Enum)Enum.ToObject(enumType, value), fallback);
    public static string GetDescription(Enum value, bool fallback = true)
    {
        GetDescription(value.GetType().GetField(value.ToString()), out var sResult, fallback);
        return (sResult);
    }
    public static bool GetDescription(FieldInfo info, out string description, bool fallback = true)
    {
        var attrib = info.GetCustomAttribute<DescriptionAttribute>(false);
        if (attrib != null)
            description = attrib.Description;
        else
        {
            if (fallback)
                description = info.Name;
            else
            {
                description = string.Empty;
                return (false);
            }
        }
        return (true);
    }
}

internal static class ExtType
{
    /// <summary>
    /// Resolves all properties that are decorated with a <typeparamref name="TAttr"/> attribute.
    /// A dictionary of resolved properties and attributes will be returned afterwards.
    /// </summary>
    public static Dictionary<PropertyInfo, TAttr> GetPropertyMap<TAttr>(this Type source)
        where TAttr : Attribute
    {
        return (GetPropertyMap<TAttr, TAttr>(source, t => t.attrib));
    }
    /// <summary>
    /// Resolves all properties that are decorated with a <typeparamref name="TAttr"/> attribute.
    /// A dictionary of resolved properties and selected members will be returned afterwards.
    /// </summary>
    public static Dictionary<PropertyInfo, T> GetPropertyMap<TAttr, T>(this Type source, Func<(PropertyInfo prop, TAttr attrib), T?> selector)
        where TAttr : Attribute
    {
        return (source
            .GetProperties()
            .Select(p => (prop: p, attrib: p.TryGetCustomAttribute<TAttr>()))
            .Where(t => t.attrib is not null)
            .Select(t => (prop: t.prop, val: selector(t)))
            .Where(t => t.val is not null)
            .ToDictionary(
                t => t.prop,
                t => t.val
            )!);
    }
}
internal static class ExtMemberInfo
{
    public static T GetCustomAttribute<T>(this PropertyInfo pInfo) where T : Attribute
    {
        if (TryGetCustomAttribute<T>(pInfo, out T _tAttrib))
            return (_tAttrib);
        else
            throw new FieldAccessException(string.Format("Failed to get custom attribute of type '{0}'!", typeof(T).Name));
    }
    public static bool TryGetCustomAttribute<T>(this MemberInfo mInfo, out T tResult) where T : Attribute
    {
        var _dAttr = mInfo.GetCustomAttributes<T>();
        if ((_dAttr != null) && (_dAttr.Count() >= 1))
            tResult = _dAttr.First<T>();
        else
            tResult = null;
        return (tResult != null);
    }
    public static T? TryGetCustomAttribute<T>(this MemberInfo member) where T : Attribute
    {
        return (TryGetCustomAttribute<T>(member, out var res) ? res : null);
    }
}
internal static class ExtField
{
    public static bool TryGetAttribute<T>(this FieldInfo field, out T attribute, bool inherit = false)
        where T : Attribute
    {
        var _oAttrib = field.GetCustomAttributes(typeof(T), inherit);
        if (_oAttrib.Length == 1)
            attribute = (T)_oAttrib[0];
        else
            attribute = null;
        return (attribute != null);
    }
    public static T GetAttribute<T>(this FieldInfo field, bool inherit = false)
        where T : Attribute
    {
        if (TryGetAttribute(field, out T tResult, inherit))
            return (tResult);
        else
            throw new KeyNotFoundException(string.Format("Did not found attribute of type '{0}'!", typeof(T).Name));
    }
}
internal static class ExtByte
{
    public static string ToHashString(this byte[] source)
    {
        var builder = new StringBuilder();
        {
            source.ForEach(b => builder.Append(b.ToString("x2")));
        }
        return (builder.ToString());
    }
}
internal static class ExtEnum
{
    public static TAttrib GetCustomAttribute<TAttrib, TEnum>(this TEnum value, bool inherit = false)
        where TAttrib : Attribute
        where TEnum : struct, Enum
    {
        return (value.GetField().GetAttribute<TAttrib>(inherit));
    }
    public static bool TryGetCustomAttribute<TAttrib, TEnum>(this TEnum value, out TAttrib attribute, bool inherit = false)
        where TAttrib : Attribute
        where TEnum : struct, Enum
    {
        return (value.GetField().TryGetAttribute(out attribute, inherit));
    }

    public static FieldInfo GetField<T>(this T value)
        where T : struct, Enum
    {
        return (GetField((Enum)value));
    }
    public static FieldInfo GetField(this Enum value)
    {
        return (value.GetType().GetField(value.ToString()));
    }
    public static string GetDescription<T>(this T value, bool fallback = true)
        where T : struct, Enum
    {
        return (GetDescription((Enum)value, fallback));
    }
    public static string GetDescription(this Enum value, bool fallback = true)
    {
        UtilEnum.GetDescription(value.GetField(), out var result, fallback);
        return (result);
    }
}
internal static class ExtList
{
    public static bool TryPop<T>(this IList<T> source, T item)
    {
        var idx = source.IndexOf(item);
        if (idx == -1)
            return (false);
        else
        {
            source.RemoveAt(idx);
            return (true);
        }
    }
    public static bool TryPop<T>(this IList<T> source, out T item, Predicate<T> condition)
    {
        var idx = source.FirstIndexOf(condition);
        if (idx == -1)
        {
            item = default;
            return (false);
        }
        else
        {
            item = source[idx];
            source.RemoveAt(idx);
            return (true);
        }
    }
    /// <summary>
    /// Pops all items, that matches a certain <paramref name="condition"/>, out of a list.
    /// </summary>
    /// <returns>An enumeration of all popped items.</returns>
    public static IEnumerable<T> PopWhere<T>(this IList<T> source, Predicate<T> condition)
    {
        for (int i = 0; i < source.Count; i++)
        {
            var item = source.ElementAt(i);
            if (condition(item))
            {
                yield return (item);
                source.RemoveAt(i--);
            }
        }
        yield break;
    }
}
internal static class ExtDictionary
{
    public static bool TryGetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, U value, out T key) => source.TryGetKeyOf((val) => val.Equals(value), out key);
    public static bool TryGetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, Predicate<U> valueSelector, out T key)
    {
        foreach (var item in source)
        {
            if (valueSelector(item.Value))
            {
                key = item.Key;
                return (true);
            }
        }
        key = default;
        return (false);
    }
    public static T GetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, U value)
    {
        if (source.TryGetKeyOf(value, out T key))
            return (key);
        else
            throw new NotImplementedException($"Failed to obtain key for missing value '{value}'!");
    }
    public static T GetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, Predicate<U> valueSelector)
    {
        if (source.TryGetKeyOf(valueSelector, out T key))
            return (key);
        else
            throw new NotImplementedException($"Failed to obtain key due to conditional missmatch!");
    }

    /// <summary>
    /// Adds a key/value-pair if the key is new.
    /// </summary>
    public static bool TryAdd<T, U>(this IDictionary<T, U> source, T key, U value)
    {
        if (source.ContainsKey(key))
            return (false);
        else
        {
            source.Add(key, value);
            return (true);
        }
    }
    /// <summary>
    /// Adds a new key/value-pair if the key is new. An existing key/value-pair is updated by the new value.
    /// </summary>
    public static void AddOrUpdate<T, U>(this IDictionary<T, U> source, T key, U value)
    {
        if (!TryAdd(source, key, value))
            source[key] = value;
    }
    /// <summary>
    /// Adds a new key/value-pair if the key is new. An existing key/value-pair is updated.
    /// </summary>
    /// <param name="addValue">The function used to generate a value for an absent key.</param>
    /// <param name="updateValueFactory">The function used to generate a new value for an existing key based on the key's existing value.</param>
    public static void AddOrUpdate<T, U>(this IDictionary<T, U> source, T key, U addValue, Func<T, U, U> updateValueFactory)
    {
        if (source.TryGetValue(key, out var _uVal))
            source[key] = updateValueFactory(key, _uVal);
        else
            source.Add(key, addValue);
    }
}
internal static class ExtEnumerable
{
    public static IEnumerable<T> First<T>(this IEnumerable<T> source, int count)
    {
        T[] res = new T[count];
        int i = 0;
        foreach (T item in source)
        {
            if (i < count)
                res[i++] = item;
            else
                break;
        }
        return (res);
    }
    public static IEnumerable<T> Last<T>(this IEnumerable<T> source, int count)
    {
        int srcCount = Enumerable.Count(source);
        count = Math.Min(count, srcCount);

        int i = 0;
        int start = (srcCount - count);
        T[] res = new T[count];
        foreach (T item in source)
        {
            if (i >= start)
                res[i - start] = item;
            i++;
        }
        return (res);
    }
    public static void ForEach<T>(this IEnumerable<T> source, Action<T> action)
    {
        if (source is null)
            return;
        foreach (T item in source)
            action(item);
    }
    public static void ForEach<T>(this IEnumerable source, Action<T> action)
    {
        if (source is null)
            return;
        foreach (T item in source)
            action(item);
    }
    /// <summary>
    /// Performs an <paramref name="action"/> on all enumerated items.
    /// </summary>
    /// <remarks>
    /// The enumeration it self is not changed at all!
    /// </remarks>
    public static IEnumerable<T> Touch<T>(this IEnumerable<T> source, Action<T> action)
    {
        if (source is not null)
        {
            foreach (var item in source)
                action(item);
        }
        return (source);
    }
    public static bool IsEmpty<T>(this IEnumerable<T> source)
    {
        return ((source == null) || (!source.Any()));
    }
    public static int IndexOf<T>(this IEnumerable<T> source, T value)
    {
        int idx = 0;
        var comparer = EqualityComparer<T>.Default; // or pass in as a parameter
        foreach (T item in source)
        {
            if (comparer.Equals(item, value))
                return idx;
            else
                idx++;
        }
        return (-1);
    }
    public static int FirstIndexOf<T>(this IEnumerable<T> source, Predicate<T> predicate)
    {
        int i = 0;
        foreach (var pair in source)
        {
            if (predicate(pair))
                return (i);
            else
                i++;
        }
        return (-1);
    }
    public static int LastIndexOf<T>(this IEnumerable<T> source, Predicate<T> predicate)
    {
        int idx = -1;
        int i = 0;
        foreach (var pair in source)
        {
            if (predicate(pair))
                idx = i;
            i++;
        }
        return (idx);
    }
    /// <summary>
    /// Filters a sequence of values that are not null.
    /// </summary>
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source)
        where T : class
    {
        return (SelectWhereNotNull<T, T>(source, (i) => i));
    }
    /// <summary>
    /// Filters a sequence of values that are not null to project them into a new form.
    /// </summary>
    public static IEnumerable<TRes> SelectWhereNotNull<T, TRes>(this IEnumerable<T> source, Func<T, TRes> selector)
        where TRes : class?
    {
        foreach (var item in source)
        {
            var res = selector(item);
            if (res is not null)
                yield return (res);
        }
        yield break;
    }
}
internal static class ExtVersion
{
    public static double ToDouble(this Version source) => Convert.ToDouble($"{source.Major}.{source.Minor}{source.Build}{source.Revision}");
}