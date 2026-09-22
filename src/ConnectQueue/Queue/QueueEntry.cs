using System;
using System.Net;
using CentralAuth;

namespace ConnectQueue.Queue;

internal sealed class QueueEntry
{
    internal PlayerAuthenticationManager Auth { get; }

    internal AuthenticationResponse Response { get; private set; }

    internal string UserId { get; }

    internal DateTimeOffset TokenExpiry { get; private set; }

    internal DateTime RefreshAskedAt { get; private set; }

    internal bool RefreshPending { get; private set; }

    internal int RefreshAttempts { get; private set; }

    internal int Priority { get; }

    internal DateTime EnqueuedAt { get; }

    internal IPEndPoint Endpoint { get; }

    internal string PreauthUserId { get; }

    internal ReferenceHub Hub => Auth == null ? null : Auth._hub;

    internal bool Waiting =>
        Auth != null && Hub != null && Auth.InstanceMode == ClientInstanceMode.Unverified;

    internal bool TokenExpired => TokenExpiry != default && TokenExpiry < DateTimeOffset.UtcNow;

    internal QueueEntry(PlayerAuthenticationManager auth, AuthenticationResponse response, string userId, DateTimeOffset tokenExpiry, int priority, IPEndPoint endpoint, string preauthUserId)
    {
        Auth = auth;
        Response = response;
        UserId = userId;
        TokenExpiry = tokenExpiry;
        Priority = priority;
        Endpoint = endpoint;
        PreauthUserId = preauthUserId;
        EnqueuedAt = DateTime.UtcNow;
    }

    internal bool NeedsRefresh(TimeSpan margin)
    {
        return TokenExpiry != default && TokenExpiry - DateTimeOffset.UtcNow <= margin;
    }

    internal void RefreshAsked()
    {
        RefreshAskedAt = DateTime.UtcNow;
        RefreshPending = true;
        RefreshAttempts++;
    }

    internal void TokenArrived(AuthenticationResponse response, DateTimeOffset tokenExpiry)
    {
        Response = response;
        TokenExpiry = tokenExpiry;
        RefreshPending = false;
        RefreshAttempts = 0;
    }

    internal static bool Gone(PlayerAuthenticationManager auth)
    {
        return ReferenceEquals(auth, null);
    }
}