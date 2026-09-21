using System;
using CentralAuth;
using ConnectQueue.ApiFeatures;
using ConnectQueue.Modules;
using HarmonyLib;

namespace ConnectQueue.Patches;

[HarmonyPatch(typeof(PlayerAuthenticationManager), nameof(PlayerAuthenticationManager.ProcessAuthenticationResponse))]
[HarmonyPriority(Priority.First)]
internal static class QueueAuthPatch
{
    private static bool Prefix(PlayerAuthenticationManager __instance, AuthenticationResponse msg)
    {
        try
        {
            return ConnectQueueModule.Active?.TryHold(__instance, msg) != true;
        }
        catch (Exception error)
        {
            LogManager.Error($"The queue could not decide, letting the connection through: {error}");
            return true;
        }
    }
}

[HarmonyPatch(typeof(PlayerAuthenticationManager), nameof(PlayerAuthenticationManager.RejectAuthentication))]
[HarmonyPriority(Priority.First)]
internal static class QueueRejectPatch
{
    private static bool Prefix(PlayerAuthenticationManager __instance)
    {
        try
        {
            return ConnectQueueModule.Active?.IsHeld(__instance) != true;
        }
        catch (Exception error)
        {
            LogManager.Error($"The queue could not answer a rejection, letting it through: {error}");
            return true;
        }
    }
}