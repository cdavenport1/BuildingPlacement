using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderExternalActivationGate
{
    private const float RefreshIntervalSeconds = 0.25f;

    private float nextRefreshAt;
    private bool initialized;
    private bool isActive = true;
    private string? lastWarning;

    internal bool IsActive
    {
        get
        {
            Refresh();
            return isActive;
        }
    }

    private void Refresh()
    {
        if (initialized && Time.unscaledTime < nextRefreshAt)
        {
            return;
        }

        initialized = true;
        nextRefreshAt = Time.unscaledTime + RefreshIntervalSeconds;
        isActive = Evaluate();
    }

    private bool Evaluate()
    {
        if (!CommanderSettings.LinkToExternalCommanderMode)
        {
            lastWarning = null;
            return true;
        }

        string pluginGuid = CommanderSettings.ExternalCommanderPluginGuid.Trim();
        if (string.IsNullOrWhiteSpace(pluginGuid))
        {
            return WarnAndReturnFalse("External commander linking is enabled, but General/ExternalCommanderPluginGuid is empty.");
        }

        if (!Chainloader.PluginInfos.TryGetValue(pluginGuid, out BepInEx.PluginInfo pluginInfo))
        {
            return WarnAndReturnFalse($"External commander plugin '{pluginGuid}' is not loaded.");
        }

        Type? targetType = ResolveTargetType(pluginInfo);
        if (targetType == null)
        {
            string configuredTypeName = CommanderSettings.ExternalCommanderTypeName.Trim();
            return WarnAndReturnFalse(
                string.IsNullOrWhiteSpace(configuredTypeName)
                    ? $"Could not resolve an external commander type from plugin '{pluginGuid}'."
                    : $"Could not find external commander type '{configuredTypeName}'.");
        }

        string stateMemberName = CommanderSettings.ExternalCommanderStateMemberName.Trim();
        if (string.IsNullOrWhiteSpace(stateMemberName))
        {
            return WarnAndReturnFalse("External commander linking is enabled, but General/ExternalCommanderStateMemberName is empty.");
        }

        MemberInfo? stateMember = FindReadableMember(targetType, stateMemberName);
        if (stateMember == null)
        {
            return WarnAndReturnFalse($"Could not find external commander member '{stateMemberName}' on type '{targetType.FullName}'.");
        }

        object? target = null;
        if (!IsStatic(stateMember))
        {
            target = ResolveTargetInstance(targetType, pluginInfo);
            if (target == null)
            {
                return WarnAndReturnFalse($"Could not resolve an instance of external commander type '{targetType.FullName}'.");
            }
        }

        if (!TryReadMemberValue(stateMember, target, out object? value, out string? error))
        {
            return WarnAndReturnFalse(error ?? $"Could not read external commander member '{stateMemberName}'.");
        }

        if (!TryInterpretStateValue(value, out bool active, out string? interpretError))
        {
            return WarnAndReturnFalse(interpretError ?? $"Could not interpret external commander member '{stateMemberName}'.");
        }

        lastWarning = null;
        return active;
    }

    private static Type? ResolveTargetType(BepInEx.PluginInfo pluginInfo)
    {
        string configuredTypeName = CommanderSettings.ExternalCommanderTypeName.Trim();
        if (!string.IsNullOrWhiteSpace(configuredTypeName))
        {
            return AccessTools.TypeByName(configuredTypeName);
        }

        return pluginInfo.Instance?.GetType();
    }

    private static object? ResolveTargetInstance(Type targetType, BepInEx.PluginInfo pluginInfo)
    {
        if (pluginInfo.Instance != null && targetType.IsInstanceOfType(pluginInfo.Instance))
        {
            return pluginInfo.Instance;
        }

        string configuredInstanceMember = CommanderSettings.ExternalCommanderInstanceMemberName.Trim();
        string[] candidateNames = string.IsNullOrWhiteSpace(configuredInstanceMember)
            ? new[] { "Instance", "instance", "Current", "CurrentInstance" }
            : new[] { configuredInstanceMember };

        foreach (string candidateName in candidateNames)
        {
            MemberInfo? instanceMember = FindReadableMember(targetType, candidateName);
            if (instanceMember == null)
            {
                continue;
            }

            object? owner = IsStatic(instanceMember)
                ? null
                : pluginInfo.Instance != null && targetType.IsInstanceOfType(pluginInfo.Instance)
                    ? pluginInfo.Instance
                    : null;
            if (!IsStatic(instanceMember) && owner == null)
            {
                continue;
            }

            if (TryReadMemberValue(instanceMember, owner, out object? value, out _ ) && value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static MemberInfo? FindReadableMember(Type targetType, string memberName)
    {
        return (MemberInfo?)AccessTools.Property(targetType, memberName)
            ?? AccessTools.Field(targetType, memberName)
            ?? AccessTools.Method(targetType, memberName, Type.EmptyTypes);
    }

    private static bool TryReadMemberValue(MemberInfo member, object? target, out object? value, out string? error)
    {
        try
        {
            switch (member)
            {
                case PropertyInfo property:
                    value = property.GetValue(target, null);
                    error = null;
                    return true;
                case FieldInfo field:
                    value = field.GetValue(target);
                    error = null;
                    return true;
                case MethodInfo method when method.GetParameters().Length == 0:
                    value = method.Invoke(target, null);
                    error = null;
                    return true;
                default:
                    value = null;
                    error = $"Member '{member.Name}' is not a readable field, property, or parameterless method.";
                    return false;
            }
        }
        catch (Exception exception)
        {
            value = null;
            error = $"Reading external commander member '{member.Name}' failed: {exception.Message}";
            return false;
        }
    }

    private static bool TryInterpretStateValue(object? value, out bool active, out string? error)
    {
        string expectedValue = CommanderSettings.ExternalCommanderExpectedValue.Trim();
        if (!string.IsNullOrWhiteSpace(expectedValue))
        {
            string actualValue = value?.ToString() ?? string.Empty;
            active = string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase);
            error = null;
            return true;
        }

        switch (value)
        {
            case null:
                active = false;
                error = null;
                return true;
            case bool boolValue:
                active = boolValue;
                error = null;
                return true;
            case string stringValue:
                active = !string.IsNullOrWhiteSpace(stringValue);
                error = null;
                return true;
            case sbyte signedByteValue:
                active = signedByteValue != 0;
                error = null;
                return true;
            case byte byteValue:
                active = byteValue != 0;
                error = null;
                return true;
            case short shortValue:
                active = shortValue != 0;
                error = null;
                return true;
            case ushort unsignedShortValue:
                active = unsignedShortValue != 0;
                error = null;
                return true;
            case int intValue:
                active = intValue != 0;
                error = null;
                return true;
            case uint unsignedIntValue:
                active = unsignedIntValue != 0;
                error = null;
                return true;
            case long longValue:
                active = longValue != 0;
                error = null;
                return true;
            case ulong unsignedLongValue:
                active = unsignedLongValue != 0;
                error = null;
                return true;
            case float floatValue:
                active = Mathf.Abs(floatValue) > float.Epsilon;
                error = null;
                return true;
            case double doubleValue:
                active = Math.Abs(doubleValue) > double.Epsilon;
                error = null;
                return true;
            case decimal decimalValue:
                active = decimalValue != decimal.Zero;
                error = null;
                return true;
            case Enum enumValue:
                object defaultValue = Activator.CreateInstance(enumValue.GetType())!;
                active = !Equals(enumValue, defaultValue);
                error = null;
                return true;
            case Behaviour behaviour:
                active = behaviour.isActiveAndEnabled;
                error = null;
                return true;
            case GameObject gameObject:
                active = gameObject.activeInHierarchy;
                error = null;
                return true;
            case UnityEngine.Object unityObject:
                active = unityObject != null;
                error = null;
                return true;
            default:
                active = true;
                error = null;
                return true;
        }
    }

    private static bool IsStatic(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => field.IsStatic,
            PropertyInfo property => (property.GetGetMethod(true) ?? property.GetSetMethod(true))?.IsStatic == true,
            MethodInfo method => method.IsStatic,
            _ => false
        };
    }

    private bool WarnAndReturnFalse(string warning)
    {
        if (!string.Equals(lastWarning, warning, StringComparison.Ordinal))
        {
            CommanderPlugin.Log.LogWarning(warning);
            lastWarning = warning;
        }

        return false;
    }
}