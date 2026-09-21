using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CentralAuth;
using ConnectQueue.ApiFeatures;
using ConnectQueue.Internal;
using ConnectQueue.Patches;
using ConnectQueue.Ranks;
using Cryptography;
using HarmonyLib;
using Hints;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using MEC;
using NorthwoodLib;

namespace ConnectQueue.Modules;

internal sealed class QueueEntry
{
    internal PlayerAuthenticationManager Auth { get; }

    internal AuthenticationResponse Response { get; }

    internal string UserId { get; }

    internal DateTimeOffset TokenExpiry { get; }

    internal int Priority { get; }

    internal DateTime EnqueuedAt { get; }

    internal ReferenceHub Hub => Auth == null ? null : Auth._hub;

    internal bool Waiting =>
        Auth != null && Hub != null && Auth.InstanceMode == ClientInstanceMode.Unverified;

    internal bool TokenExpired => TokenExpiry != default && TokenExpiry < DateTimeOffset.UtcNow;

    internal QueueEntry(PlayerAuthenticationManager auth, AuthenticationResponse response, string userId, DateTimeOffset tokenExpiry, int priority)
    {
        Auth = auth;
        Response = response;
        UserId = userId;
        TokenExpiry = tokenExpiry;
        Priority = priority;
        EnqueuedAt = DateTime.UtcNow;
    }

    internal static bool Gone(PlayerAuthenticationManager auth)
    {
        return ReferenceEquals(auth, null);
    }
}

internal sealed class ConnectQueueModule
{
    private const int NoPriority = int.MaxValue;

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private static int _online = -1;
    
    private readonly List<QueueEntry> _entries = [];

    private readonly object _gate = new();

    private readonly Harmony _harmony;

    private readonly Dictionary<PlayerAuthenticationManager, DateTime> _releasing = new(ByReference.Instance);

    private CoroutineHandle _loop;

    internal static ConnectQueueModule Active { get; private set; }

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

    internal ConnectQueueModule(Harmony harmony)
    {
        _harmony = harmony;
    }

    internal void Start()
    {
        Active = this;

        Volatile.Write(ref _online, -1);

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

        if (settings.ReservedSlotSkip && RankSource.HasReservedSlot(userId)) return false;

        if (settings.AllowNorthwoodStaffSkip && IsNorthwoodStaff(auth, response, token)) return false;

        lock (_gate)
        {
            if (_entries.Count == 0 && HasFreeSlot()) return false;
            if (IndexOf(auth) >= 0) return true;

            _entries.Add(new QueueEntry(auth, response, userId, token?.ExpirationTime ?? default, PriorityOf(userId, settings)));
            Sort();
        }

        LogManager.Debug($"{Describe(userId)} is waiting for a slot.");

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

        string template = ConnectQueuePlugin.Settings?.QueueHint;
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
                _releasing[next.Auth] = DateTime.UtcNow;
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

        return online + _releasing.Count < CustomNetworkManager.slots;
    }

    private static void Recount()
    {
        if (!MainThread.IsMainThread) return;
        Volatile.Write(ref _online, ReferenceHub.GetPlayerCount(ClientInstanceMode.ReadyClient));
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
            return response.SignedAuthToken.TryGetToken("Authentication", out AuthenticationToken token, out _, out userId) ? token : null;
        }
        catch (Exception error)
        {
            LogManager.Debug($"Could not read an authentication token: {error.Message}");
            return null;
        }
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

        DateTime cutoff = DateTime.UtcNow - ReleaseTimeout;
        PlayerAuthenticationManager[] stale = [.. _releasing.Where(pending => pending.Key == null || pending.Key._hub == null || pending.Value < cutoff).Select(pending => pending.Key)];

        foreach (PlayerAuthenticationManager auth in stale) _releasing.Remove(auth);
    }

    private static string Describe(string userId)
    {
        return string.IsNullOrEmpty(userId) ? "An unknown player" : userId;
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