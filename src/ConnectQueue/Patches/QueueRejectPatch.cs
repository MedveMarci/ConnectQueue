using System;
using CentralAuth;
using ConnectQueue.ApiManager;
using HarmonyLib;

namespace ConnectQueue.Patches;

[HarmonyPatch(typeof(PlayerAuthenticationManager), nameof(PlayerAuthenticationManager.RejectAuthentication))]
[HarmonyPriority(Priority.First)]
internal static class QueueRejectPatch
{
    private static bool Prefix(PlayerAuthenticationManager __instance)
    {
        try
        {
            return Queue.ConnectQueue.Active?.IsHeld(__instance) != true;
        }
        catch (Exception error)
        {
            LogManager.Error($"The queue could not answer a rejection, letting it through: {error}");
            return true;
        }
    }
}