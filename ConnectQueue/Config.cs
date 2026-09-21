using System.Collections.Generic;
using System.ComponentModel;

namespace ConnectQueue;

public class Config
{
    [Description("Debug logging. Useful while setting the queue up.")]
    public bool Debug { get; set; } = false;

    [Description("How many connections may wait at once. 0 means no limit.")]
    public int MaxSize { get; set; } = 20;

    [Description("RemoteAdmin group names, highest priority first. A player whose group is not listed " + "waits behind everybody whose group is, and players of the same group are let in " + "in the order they arrived. These are the group keys from config_remoteadmin.txt.")]
    public List<string> GroupPriority { get; set; } = ["owner", "admin", "moderator"];

    [Description("Let anybody holding a reserved slot past the queue. Reserved slots are read from " + "UserIDReservedSlots.txt")]
    public bool ReservedSlotSkip { get; set; } = true;

    [Description("Let verified Northwood staff past the queue.")]
    public bool AllowNorthwoodStaffSkip { get; set; } = true;

    [Description("Also read ranks and reserved slots from CedMod when it is installed.")]
    public bool CedModIntegration { get; set; } = true;

    [Description("Shown every second to a waiting player. {position} is their place in the queue, " + "{total} is how many are waiting. Leave empty to show nothing.")]
    public string QueueHint { get; set; } = "<b>The server is full.</b>\nYou are <b>{position}.</b> in the queue ({total} waiting).";

    [Description("Shown to everybody still waiting when the plugin is disabled or the server shuts down.")]
    public string QueueStoppedMessage { get; set; } = "The queue has stopped. Please reconnect.";

    [Description("Shown to a player whose session token ran out while they waited.")]
    public string TokenExpiredMessage { get; set; } = "Your authentication expired while waiting. Please reconnect.";
}