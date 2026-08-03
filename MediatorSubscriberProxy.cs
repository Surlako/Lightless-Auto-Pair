using System;
using System.Reflection;

namespace LightlessAutoPair;

public class MediatorSubscriberProxy : DispatchProxy
{
    internal object? MediatorInstance { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_Mediator")
            return MediatorInstance;

        if (targetMethod is null || targetMethod.ReturnType == typeof(void))
            return null;

        return targetMethod.ReturnType.IsValueType
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
    }
}
