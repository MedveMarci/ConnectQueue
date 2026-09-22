using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ConnectQueue.ApiManager;

namespace ConnectQueue.Ranks;

internal static class CedMod
{
    private const string AssemblyName = "CedMod";
    private const string QuerySystemTypeName = "CedMod.Addons.QuerySystem.QuerySystem";
    private const string PermissionProviderTypeName = "CedMod.Addons.QuerySystem.WS.PermissionProvider";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();

    private static FieldInfo _reservedSlotIds;
    private static PropertyInfo _permissions;
    private static PropertyInfo _membersList;
    private static PropertyInfo _memberUserId;
    private static PropertyInfo _memberGroup;
    private static DateTime _lastAttempt = DateTime.MinValue;
    private static bool _resolved;
    private static bool _announced;

    internal static string GroupOf(string userId)
    {
        if (string.IsNullOrEmpty(userId) || !Resolve()) return null;

        foreach (object member in Members())
        {
            if (!string.Equals(_memberUserId.GetValue(member) as string, userId, StringComparison.Ordinal)) continue;

            string group = _memberGroup.GetValue(member) as string;
            return string.IsNullOrWhiteSpace(group) ? null : group;
        }

        return null;
    }

    internal static bool HasReservedSlot(string userId)
    {
        if (string.IsNullOrEmpty(userId) || !Resolve() || _reservedSlotIds == null) return false;

        try
        {
            return _reservedSlotIds.GetValue(null) is ICollection<string> reserved && reserved.Contains(userId);
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read CedMod's reserved slots: {error.Message}");
            return false;
        }
    }

    private static IEnumerable<object> Members()
    {
        try
        {
            object permissions = _permissions.GetValue(null);
            if (permissions == null) return [];

            return _membersList.GetValue(permissions) is IEnumerable members ? members.Cast<object>().ToArray() : [];
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read CedMod's member list: {error.Message}");
            return [];
        }
    }

    private static bool Resolve()
    {
        lock (Gate)
        {
            if (_resolved) return true;
            if (DateTime.UtcNow - _lastAttempt < RetryInterval) return false;

            _lastAttempt = DateTime.UtcNow;

            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly == null) return false;

            try
            {
                Type querySystem = assembly.GetType(QuerySystemTypeName, false);
                Type provider = assembly.GetType(PermissionProviderTypeName, false);
                if (querySystem == null || provider == null) return Unsupported();

                _reservedSlotIds = querySystem.GetField("ReservedSlotUserids", BindingFlags.Public | BindingFlags.Static);
                _permissions = provider.GetProperty("Permissions", BindingFlags.Public | BindingFlags.Static);
                if (_reservedSlotIds == null || _permissions == null) return Unsupported();

                _membersList = _permissions.PropertyType.GetProperty("MembersList");
                Type member = _membersList?.PropertyType.GetGenericArguments().FirstOrDefault();
                _memberUserId = member?.GetProperty("UserId");
                _memberGroup = member?.GetProperty("Group");
                if (_memberUserId == null || _memberGroup == null) return Unsupported();
            }
            catch (Exception error)
            {
                LogManager.Warn($"CedMod is installed but could not be read: {error.Message}");
                return false;
            }

            _resolved = true;
            LogManager.Info("CedMod found; ranks and reserved slots will be read from it as well.");
            return true;
        }
    }

    private static bool Unsupported()
    {
        if (_announced)
            return false;
        _announced = true;
        LogManager.Warn("CedMod is installed but this version's internals are not the ones this plugin knows. " + "Ranks will come from the game's own configuration only.");

        return false;
    }
}