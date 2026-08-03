using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LightlessAutoPair;

internal static class ReflectionUtil
{
    internal const BindingFlags AllInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? ReadMember(object? instance, params string[] names)
    {
        if (instance is null)
            return null;

        var type = instance.GetType();
        foreach (var name in names)
        {
            try
            {
                var property = type.GetProperty(name, AllInstance);
                if (property is not null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(instance);

                var field = type.GetField(name, AllInstance);
                if (field is not null)
                    return field.GetValue(instance);
            }
            catch
            {
                // A third-party property getter is allowed to fail while the plugin is starting/stopping.
            }
        }

        foreach (var interfaceType in type.GetInterfaces())
        {
            foreach (var name in names)
            {
                try
                {
                    var property = interfaceType.GetProperty(name);
                    if (property is not null && property.GetIndexParameters().Length == 0)
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

        var method = instance.GetType().GetMethods(AllInstance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == 0);
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

        foreach (var property in type.GetProperties(AllInstance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
                continue;

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
}
