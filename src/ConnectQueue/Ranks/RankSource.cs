using System;
using System.Collections.Generic;
using ConnectQueue.ApiManager;

namespace ConnectQueue.Ranks;

internal static class RankSource
{
    private static bool CedModEnabled => ConnectQueuePlugin.Settings?.CedModIntegration == true;

    internal static string GroupOf(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        try
        {
            Dictionary<string, string> members = ServerStatic.PermissionsHandler?.Members;
            if (members != null && members.TryGetValue(userId, out string group) && !string.IsNullOrWhiteSpace(group))
                return group;
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read the RemoteAdmin members list: {error.Message}");
        }

        return CedModEnabled ? CedMod.GroupOf(userId) : null;
    }

    internal static bool HasReservedSlot(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        try
        {
            if (ReservedSlot.Users.Contains(userId.Trim())) return true;
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read the reserved slots list: {error.Message}");
        }

        return CedModEnabled && CedMod.HasReservedSlot(userId);
    }
}