using System;
using CentralAuth;
using ConnectQueue.ApiManager;
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
            return Queue.ConnectQueue.Active?.TryHold(__instance, msg) != true;
        }
        catch (Exception error)
        {
            LogManager.Error($"The queue could not decide, letting the connection through: {error}");
            return true;
        }
    }
}