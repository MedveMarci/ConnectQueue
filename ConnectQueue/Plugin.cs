using System;
using System.Collections.Generic;
using ConnectQueue.ApiFeatures;
using ConnectQueue.Internal;
using ConnectQueue.Modules;
using HarmonyLib;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Loader.Features.Plugins;
using MEC;
using Version = System.Version;

namespace ConnectQueue;

public class ConnectQueuePlugin : Plugin<Config>
{
    private Harmony _harmony;
    private CoroutineHandle _pump;
    private ConnectQueueModule _queue;

    internal static ConnectQueuePlugin Singleton { get; set; }

    internal static Config Settings => Singleton?.Config;

    public override string Name => "ConnectQueue";

    public override string Author => "MedveMarci";

    public override string Description =>
        "Holds connections in a queue when the server is full, ordered by the ranks the server already knows.";

    public override Version Version { get; } = new(1, 0, 0);

    public override Version RequiredApiVersion => new(LabApiProperties.CompiledVersion);

    public override bool IsTransparent => true;

    public override void Enable()
    {
        Singleton = this;
        MainThread.Capture();

        _harmony = new Harmony($"hu.funzone.connectqueue.{ServerStatic.ServerPort}");
        _queue = new ConnectQueueModule(_harmony);
        _queue.Start();

        _pump = Timing.RunCoroutine(PumpLoop(), Segment.RealtimeUpdate);

        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
    }

    public override void Disable()
    {
        if (_pump.IsRunning) Timing.KillCoroutines(_pump);

        _queue?.Stop();
        _queue = null;

        MainThread.Pump();
        MainThread.Clear();

        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;

        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        Singleton = null;
    }

    private static IEnumerator<float> PumpLoop()
    {
        while (true)
        {
            try
            {
                MainThread.Pump();
            }
            catch (Exception exception)
            {
                LogManager.Error($"Pump loop threw: {exception}");
            }

            yield return Timing.WaitForOneFrame;
        }
    }

    public static void OnWaitingForPlayers()
    {
        VersionManager.CheckForUpdates();
    }
}