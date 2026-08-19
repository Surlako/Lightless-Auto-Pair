using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LightlessAutoPair;

internal static class ReflectionUtil
{
    internal const BindingFlags AllInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Lightless lives in a collectible plugin context. ConditionalWeakTable
    // provides cached reflection metadata without keeping that context alive
    // after Lightless unloads or updates.
    private static readonly ConditionalWeakTable<Type, TypeMetadataCache> Metadata = new();

    public static object? ReadMember(object? instance, params string[] names)
    {
        if (instance is null)
            return null;

        var metadata = Metadata.GetValue(instance.GetType(), static type => new TypeMetadataCache(type));
        foreach (var name in names)
        {
            var member = metadata.GetDirectMember(name);

            if (member.Property is not null)
            {
                try
                {
                    return member.Property.GetValue(instance);
                }
                catch
                {
                    // A third-party getter may be temporarily unavailable.
                }
            }

            if (member.Field is not null)
            {
                try
                {
                    return member.Field.GetValue(instance);
                }
                catch
                {
                    // A third-party field may be temporarily unavailable.
                }
            }
        }

        foreach (var name in names)
        {
            foreach (var property in metadata.GetInterfaceProperties(name))
            {
                try
                {
                    return property.GetValue(instance);
                }
                catch
                {
                    // Ignore explicit-interface getters that are temporarily unavailable.
                }
            }
        }

        return null;
    }

    public static string ReadString(object? instance, params string[] names)
        => ReadMember(instance, names)?.ToString()?.Trim() ?? string.Empty;

    public static bool? ReadBool(object? instance, params string[] names)
    {
        var value = ReadMember(instance, names);
        return value switch
        {
            bool boolean => boolean,
            _ when bool.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static IEnumerable<object> Enumerate(object? value)
    {
        if (value is null || value is string)
            yield break;

        if (value is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            if (item is not null)
                yield return item;
        }
    }

    public static object? InvokeNoArgs(object? instance, string name)
    {
        if (instance is null)
            return null;

        var metadata = Metadata.GetValue(instance.GetType(), static type => new TypeMetadataCache(type));
        var method = metadata.GetNoArgumentMethod(name);
        return method?.Invoke(instance, null);
    }

    public static string CollectText(object? root, int maxDepth = 2)
    {
        var parts = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CollectTextRecursive(root, 0, maxDepth, parts, visited);
        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
    }

    private static void CollectTextRecursive(
        object? value,
        int depth,
        int maxDepth,
        List<string> parts,
        HashSet<object> visited)
    {
        if (value is null || depth > maxDepth)
            return;

        if (value is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text.Trim());
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is DateTime || value is TimeSpan || value is Guid)
        {
            parts.Add(value.ToString() ?? string.Empty);
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
            return;

        var metadata = Metadata.GetValue(type, static itemType => new TypeMetadataCache(itemType));
        foreach (var property in metadata.ReadableProperties)
        {
            try
            {
                CollectTextRecursive(property.GetValue(value), depth + 1, maxDepth, parts, visited);
            }
            catch
            {
                // Ignore volatile third-party getters.
            }
        }
    }

    private sealed class TypeMetadataCache
    {
        private readonly Type type;
        private readonly ConcurrentDictionary<string, DirectMember> directMembers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, InterfaceProperties> interfaceProperties = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, MethodLookup> noArgumentMethods = new(StringComparer.Ordinal);

        public TypeMetadataCache(Type type)
        {
            this.type = type;

            try
            {
                ReadableProperties = type.GetProperties(AllInstance)
                    .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                    .ToArray();
            }
            catch
            {
                ReadableProperties = [];
            }
        }

        public PropertyInfo[] ReadableProperties { get; }

        public DirectMember GetDirectMember(string name)
            => directMembers.GetOrAdd(name, FindDirectMember);

        public PropertyInfo[] GetInterfaceProperties(string name)
            => interfaceProperties.GetOrAdd(name, FindInterfaceProperties).Properties;

        public MethodInfo? GetNoArgumentMethod(string name)
            => noArgumentMethods.GetOrAdd(name, FindNoArgumentMethod).Method;

        private DirectMember FindDirectMember(string name)
        {
            PropertyInfo? property = null;
            FieldInfo? field = null;

            try
            {
                property = type.GetProperty(name, AllInstance);
                if (property?.GetIndexParameters().Length != 0)
                    property = null;
            }
            catch
            {
                // Leave ambiguous or unavailable properties uncached as absent.
            }

            try
            {
                field = type.GetField(name, AllInstance);
            }
            catch
            {
                // Leave unavailable fields uncached as absent.
            }

            return new DirectMember(property, field);
        }

        private InterfaceProperties FindInterfaceProperties(string name)
        {
            try
            {
                var properties = type.GetInterfaces()
                    .Select(interfaceType => interfaceType.GetProperty(name))
                    .Where(property => property is not null && property.GetIndexParameters().Length == 0)
                    .Cast<PropertyInfo>()
                    .ToArray();
                return new InterfaceProperties(properties);
            }
            catch
            {
                return new InterfaceProperties([]);
            }
        }

        private MethodLookup FindNoArgumentMethod(string name)
        {
            try
            {
                return new MethodLookup(type.GetMethods(AllInstance)
                    .FirstOrDefault(candidate =>
                        candidate.Name == name && candidate.GetParameters().Length == 0));
            }
            catch
            {
                return new MethodLookup(null);
            }
        }
    }

    private readonly record struct DirectMember(PropertyInfo? Property, FieldInfo? Field);
    private sealed record InterfaceProperties(PropertyInfo[] Properties);
    private readonly record struct MethodLookup(MethodInfo? Method);
}
