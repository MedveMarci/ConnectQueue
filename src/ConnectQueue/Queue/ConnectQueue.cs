using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CentralAuth;
using ConnectQueue.ApiManager;
using ConnectQueue.Ranks;
using Cryptography;
using HarmonyLib;
using Hints;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LiteNetLib;
using MEC;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using NorthwoodLib;

namespace ConnectQueue.Queue;

internal sealed class ConnectQueue
{
    private const int NoPriority = int.MaxValue;

    private const int ReservedPriority = -1;

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RefreshRetry = TimeSpan.FromSeconds(10);

    private const int MaxRefreshAttempts = 3;

    private static int _online = -1;

    private static int _reservedOnline;

    private readonly List<QueueEntry> _entries = [];

    private readonly object _gate = new();

    private readonly Harmony _harmony;

    private readonly Dictionary<PlayerAuthenticationManager, DateTime> _releasing = new(ByReference.Instance);

    private CoroutineHandle _loop;

    internal static ConnectQueue Active { get; private set; }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal ConnectQueue(Harmony harmony)
    {
        _harmony = harmony;
    }

    internal void Start()
    {
        Active = this;

        Volatile.Write(ref _online, -1);
        Volatile.Write(ref _reservedOnline, 0);

        PlayerEvents.PreAuthenticating += OnPreAuthenticating;
        PlayerEvents.Left += OnPlayerLeft;

        _harmony.PatchAll(Assembly.GetCallingAssembly());

        _loop = Timing.RunCoroutine(Run(), Segment.RealtimeUpdate);
    }

    internal void Stop()
    {
        PlayerEvents.PreAuthenticating -= OnPreAuthenticating;
        PlayerEvents.Left -= OnPlayerLeft;
        if (_loop.IsRunning) Timing.KillCoroutines(_loop);

        Drain(ConnectQueuePlugin.Settings?.QueueStoppedMessage);
        Active = null;
    }

    private void OnPreAuthenticating(PlayerPreAuthenticatingEventArgs args)
    {
        try
        {
            if (!args.IsAllowed) return;
            if (args.CanJoin) return;
            if (!CanAcceptAnother()) return;

            args.CanJoin = true;
        }
        catch (Exception error)
        {
            LogManager.Error($"The queue could not decide about {args.UserId}: {error}");
        }
    }

    private bool CanAcceptAnother()
    {
        Config settings = ConnectQueuePlugin.Settings;
        if (settings == null) return false;

        return settings.MaxSize <= 0 || Count < settings.MaxSize;
    }

    internal bool TryHold(PlayerAuthenticationManager auth, AuthenticationResponse response)
    {
        Config settings = ConnectQueuePlugin.Settings;
        if (settings == null || auth == null) return false;

        Recount();

        lock (_gate)
        {
            if (_releasing.Remove(auth)) return false;

            if (_entries.Count == 0 && HasFreeSlot()) return false;
        }

        AuthenticationToken token = ReadToken(response, out string userId);

        lock (_gate)
        {
            int waiting = IndexOf(auth);
            if (waiting >= 0)
            {
                _entries[waiting].TokenArrived(response, token?.ExpirationTime ?? default);
                LogManager.Debug($"{Describe(userId)} sent a fresh authentication token while waiting.");
                return true;
            }
        }

        bool reserved = settings.ReservedSlotSkip && RankSource.HasReservedSlot(userId);

        if (reserved)
        {
            bool room;

            lock (_gate)
            {
                room = HasReservedRoom();
            }

            if (room) return false;

            LogManager.Debug($"{Describe(userId)} holds a reserved slot, but the reserved slots are taken as well.");
        }

        if (settings.AllowNorthwoodStaffSkip && IsNorthwoodStaff(auth, response, token)) return false;

        IPEndPoint endpoint = EndpointOf(auth);
        string preauthUserId = PreauthUserIdOf(endpoint);

        if (preauthUserId == null)
            LogManager.Warn($"Could not read the preauthentication record of {Describe(userId)}. They will be turned away if they wait for more than 200 seconds.");

        lock (_gate)
        {
            if (_entries.Count == 0 && HasFreeSlot()) return false;
            if (IndexOf(auth) >= 0) return true;

            _entries.Add(new QueueEntry(auth, response, userId, token?.ExpirationTime ?? default, reserved ? ReservedPriority : PriorityOf(userId, settings), endpoint, preauthUserId));
            Sort();
        }

        LogManager.Debug($"{Describe(userId)} is waiting for a slot. {Lifetime(token)}");

        MainThread.RunNextTick(() => Advance());
        return true;
    }

    internal bool IsHeld(PlayerAuthenticationManager auth)
    {
        if (auth == null) return false;

        lock (_gate)
        {
            return IndexOf(auth) >= 0;
        }
    }

    private IEnumerator<float> Run()
    {
        while (true)
        {
            try
            {
                Tick();
            }
            catch (Exception error)
            {
                LogManager.Error($"The queue loop failed: {error}");
            }

            yield return Timing.WaitForSeconds(1f);
        }
    }

    private void Tick()
    {
        QueueEntry[] waiting = Advance();

        Config settings = ConnectQueuePlugin.Settings;
        if (settings == null) return;

        KeepPreauthAlive(waiting);

        RefreshTokens(waiting);

        string template = settings.QueueHint;
        if (string.IsNullOrWhiteSpace(template)) return;

        for (int position = 0; position < waiting.Length; position++)
        {
            ReferenceHub hub = waiting[position].Hub;
            if (hub == null || hub.hints == null) continue;

            string text = template.Replace("{position}", (position + 1).ToString()).Replace("{total}", waiting.Length.ToString());

            hub.hints.Show(new TextHint(text, null, null, 1.5f));
        }
    }

    private QueueEntry[] Advance()
    {
        QueueEntry next = null;
        QueueEntry[] waiting;

        Recount();

        lock (_gate)
        {
            Prune();

            if (_entries.Count > 0 && HasFreeSlot())
            {
                next = _entries[0];
                _entries.RemoveAt(0);
                _releasing[next.Auth] = DateTime.UtcNow + ReleaseTimeout;
            }

            waiting = [.. _entries];
        }

        if (next != null) Release(next);
        return waiting;
    }

    private void OnPlayerLeft(PlayerLeftEventArgs args)
    {
        ReferenceHub hub = args?.Player?.ReferenceHub;
        if (hub == null) return;

        lock (_gate)
        {
            _entries.RemoveAll(entry => ReferenceEquals(entry.Hub, hub));

            PlayerAuthenticationManager auth = hub.authManager;
            if (!QueueEntry.Gone(auth)) _releasing.Remove(auth);
        }

        MainThread.RunNextTick(() => Advance());
    }

    private void Release(QueueEntry entry)
    {
        MainThread.Run(() => ReleaseNow(entry));
    }

    private void ReleaseNow(QueueEntry entry)
    {
        if (!entry.Waiting)
        {
            lock (_gate)
            {
                _releasing.Remove(entry.Auth);
            }

            return;
        }

        if (entry.RefreshPending)
        {
            TryAskForFreshToken(entry);

            lock (_gate)
            {
                if (_releasing.ContainsKey(entry.Auth)) _releasing[entry.Auth] = DateTime.UtcNow + RefreshTimeout;
            }

            LogManager.Debug($"Holding a slot for {Describe(entry.UserId)} until their fresh authentication token arrives.");
            return;
        }

        if (entry.TokenExpired)
        {
            lock (_gate)
            {
                _releasing.Remove(entry.Auth);
            }

            LogManager.Warn($"{Describe(entry.UserId)} waited until their authentication token expired.");
            Reject(entry.Auth, ConnectQueuePlugin.Settings?.TokenExpiredMessage, entry.UserId);
            return;
        }

        try
        {
            entry.Auth._authenticationRequested = true;
            entry.Auth._timeoutTimer = 0f;
            entry.Auth.ProcessAuthenticationResponse(entry.Response);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _releasing.Remove(entry.Auth);
            }

            LogManager.Error($"Could not let {Describe(entry.UserId)} in: {error}");
        }
    }

    private static void KeepPreauthAlive(QueueEntry[] waiting)
    {
        foreach (QueueEntry entry in waiting)
        {
            if (entry.Endpoint == null || entry.PreauthUserId == null) continue;

            CustomLiteNetLib4MirrorTransport.UserIds[entry.Endpoint] = new PreauthItem(entry.PreauthUserId);
        }
    }

    private static IPEndPoint EndpointOf(PlayerAuthenticationManager auth)
    {
        try
        {
            NetworkConnectionToClient connection = auth.connectionToClient;
            if (connection == null) return null;

            return LiteNetLib4MirrorServer.Peers.TryGetValue(connection.connectionId, out NetPeer peer) ? peer?.EndPoint : null;
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read the endpoint of a connection: {error.Message}");
            return null;
        }
    }

    private static string PreauthUserIdOf(IPEndPoint endpoint)
    {
        if (endpoint == null) return null;

        return CustomLiteNetLib4MirrorTransport.UserIds.TryGetValue(endpoint, out PreauthItem preauth) ? preauth.UserId : null;
    }

    private static void RefreshTokens(QueueEntry[] waiting)
    {
        foreach (QueueEntry entry in waiting)
            if (entry.NeedsRefresh(TimeSpan.FromSeconds(60)))
                TryAskForFreshToken(entry);
    }

    private static void TryAskForFreshToken(QueueEntry entry)
    {
        if (entry.RefreshAttempts >= MaxRefreshAttempts) return;
        if (entry.RefreshPending && DateTime.UtcNow - entry.RefreshAskedAt < RefreshRetry) return;
        if (!entry.Waiting) return;

        try
        {
            entry.Auth._timeoutTimer = 0f;
            entry.Auth.RequestAuthentication();
            entry.RefreshAsked();

            LogManager.Debug($"Asked {Describe(entry.UserId)} for a fresh authentication token ({entry.RefreshAttempts}/{MaxRefreshAttempts}).");
        }
        catch (Exception error)
        {
            LogManager.Error($"Could not ask {Describe(entry.UserId)} for a fresh authentication token: {error}");
        }
    }

    private void Drain(string reason)
    {
        QueueEntry[] stranded;

        lock (_gate)
        {
            stranded = [.. _entries];
            _entries.Clear();
            _releasing.Clear();
        }

        foreach (QueueEntry entry in stranded)
            if (entry.Waiting)
                Reject(entry.Auth, reason, entry.UserId);
    }

    private static void Reject(PlayerAuthenticationManager auth, string reason, string userId)
    {
        MainThread.Run(() =>
        {
            try
            {
                auth.RejectAuthentication(reason ?? string.Empty, userId);
            }
            catch (Exception error)
            {
                LogManager.Debug($"Could not turn away {Describe(userId)}: {error.Message}");
            }
        });
    }

    private bool HasFreeSlot()
    {
        int online = Volatile.Read(ref _online);

        if (online < 0) return false;

        // _reservedOnline stays at zero unless reserved slot holders are told not to take up a
        // normal slot, so this is the game's own rule in the default configuration.
        return online - Volatile.Read(ref _reservedOnline) + _releasing.Count < CustomNetworkManager.slots;
    }

    private bool HasReservedRoom()
    {
        int online = Volatile.Read(ref _online);

        if (online < 0) return false;

        return online + _releasing.Count < CustomNetworkManager.slots + CustomNetworkManager.reservedSlots;
    }

    private static void Recount()
    {
        if (!MainThread.IsMainThread) return;

        Volatile.Write(ref _online, ReferenceHub.GetPlayerCount(ClientInstanceMode.ReadyClient));
        Volatile.Write(ref _reservedOnline, ConnectQueuePlugin.Settings?.ReservedSlotsFree == true ? CountReservedOnline() : 0);
    }

    private static int CountReservedOnline()
    {
        int reserved = 0;

        foreach (ReferenceHub hub in ReferenceHub.AllHubs)
        {
            if (hub == null || hub.Mode != ClientInstanceMode.ReadyClient) continue;
            if (RankSource.HasReservedSlot(hub.authManager?.UserId)) reserved++;
        }

        return reserved;
    }

    private static int PriorityOf(string userId, Config settings)
    {
        List<string> order = settings.GroupPriority;
        if (string.IsNullOrEmpty(userId) || order == null || order.Count == 0) return NoPriority;

        string group = RankSource.GroupOf(userId);
        if (string.IsNullOrEmpty(group)) return NoPriority;

        for (int index = 0; index < order.Count; index++)
            if (string.Equals(order[index], group, StringComparison.OrdinalIgnoreCase))
                return index;

        return NoPriority;
    }

    private static bool IsNorthwoodStaff(PlayerAuthenticationManager auth, AuthenticationResponse response, AuthenticationToken authToken)
    {
        if (auth == null || authToken == null || response.SignedBadgeToken == null) return false;

        ReferenceHub hub = auth._hub;
        if (hub == null || hub.nicknameSync == null || auth.SaltedUserId == null) return false;

        try
        {
            if (!response.SignedBadgeToken.TryGetToken("Badge request", out BadgeToken badge, out _, out _) || badge == null) return false;

            if (!badge.Staff) return false;
            if (!string.Equals(badge.Serial, authToken.Serial, StringComparison.Ordinal)) return false;
            if (!string.Equals(badge.UserId, Sha.HashToString(Sha.Sha512(auth.SaltedUserId)), StringComparison.Ordinal)) return false;

            return string.Equals(StringUtils.Base64Decode(badge.Nickname), hub.nicknameSync.MyNick, StringComparison.Ordinal);
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not verify a badge token: {error.Message}");
            return false;
        }
    }

    private static AuthenticationToken ReadToken(AuthenticationResponse response, out string userId)
    {
        userId = null;
        if (response.SignedAuthToken == null) return null;

        try
        {
            bool read = response.SignedAuthToken.TryGetToken("Authentication", out AuthenticationToken token, out _, out userId);

            userId = RemoveSalt(userId);
            return read ? token : null;
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read an authentication token: {error.Message}");
            return null;
        }
    }

    private static string RemoveSalt(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return userId;

        int salt = userId.IndexOf('$');
        return salt < 0 ? userId : userId.Substring(0, salt);
    }

    private int IndexOf(PlayerAuthenticationManager auth)
    {
        for (int index = 0; index < _entries.Count; index++)
            if (ReferenceEquals(_entries[index].Auth, auth))
                return index;

        return -1;
    }

    private void Sort()
    {
        _entries.Sort((left, right) =>
        {
            int byPriority = left.Priority.CompareTo(right.Priority);
            return byPriority != 0 ? byPriority : left.EnqueuedAt.CompareTo(right.EnqueuedAt);
        });
    }

    private void Prune()
    {
        _entries.RemoveAll(entry => !entry.Waiting);

        if (_releasing.Count == 0) return;

        DateTime now = DateTime.UtcNow;
        PlayerAuthenticationManager[] stale = [.. _releasing.Where(pending => pending.Key == null || pending.Key._hub == null || pending.Value < now).Select(pending => pending.Key)];

        foreach (PlayerAuthenticationManager auth in stale) _releasing.Remove(auth);
    }

    private static string Describe(string userId)
    {
        return string.IsNullOrEmpty(userId) ? "An unknown player" : userId;
    }

    private static string Lifetime(AuthenticationToken token)
    {
        if (token == null) return "Their authentication token could not be read.";

        double left = (token.ExpirationTime - DateTimeOffset.UtcNow).TotalSeconds;
        double total = (token.ExpirationTime - token.IssuanceTime).TotalSeconds;

        return $"Their authentication token has {left:F0}s left of the {total:F0}s it was issued for.";
    }

    private sealed class ByReference : IEqualityComparer<PlayerAuthenticationManager>
    {
        internal static readonly ByReference Instance = new();

        public bool Equals(PlayerAuthenticationManager left, PlayerAuthenticationManager right)
        {
            return ReferenceEquals(left, right);
        }

        public int GetHashCode(PlayerAuthenticationManager value)
        {
            return RuntimeHelpers.GetHashCode(value);
        }
    }
}