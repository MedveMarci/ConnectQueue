# ConnectQueue ![Downloads](https://img.shields.io/github/downloads/MedveMarci/ConnectQueue/total)

An SCP: Secret Laboratory LabApi plugin which holds connections in a queue when the server is full, ordered by the
ranks.

# Features

- When the server is full, joining players are held at the authentication step instead of being kicked. As soon as a
  slot frees up, the player at the front of the queue is let in automatically - no reconnecting needed.
- Queue order comes from the server's own RemoteAdmin groups (`config_remoteadmin.txt`), so there is no separate rank
  list to maintain. Players in the same group are let in in the order they arrived.
- Reserved slot holders (`UserIDReservedSlots.txt`) and verified Northwood staff skip the queue entirely. Both can be
  turned off in the config. Reserved slot holders are let past while the server is below `max_players` plus
  `reserved_slots`, the same limit the game applies; beyond that they wait at the front of the queue. Optionally they
  can be made not to take up a `max_players` slot at all.
- Waiting players get a hint showing their position and how many people are ahead of them. The text is fully
  customizable.
- Authentication tokens are refreshed while waiting. If the refresh does not come through, the player is
  told to reconnect.
- **Optional [CedMod](https://cedmod.nl/) integration** - when CedMod is installed, ranks and reserved slots are read
  from it as well. No extra setup is needed, and the plugin works exactly the same without it.

# Installation

- Download `ConnectQueue.dll` and `0Harmony.dll` from
  the [latest release](https://github.com/MedveMarci/ConnectQueue/releases/latest).
- Move `ConnectQueue.dll` to => /SCP Secret Laboratory/LabApi/plugins/global/
- Move `0Harmony.dll` to => /SCP Secret Laboratory/LabApi/dependencies/global/
- Start the server once to generate the config, then edit it and restart.

# Configuration

| Option                       | Default                                          | Description                                                                                                                |
|------------------------------|--------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| `debug`                      | `false`                                          | Debug logging. Useful while setting the queue up.                                                                          |
| `max_size`                   | `20`                                             | How many connections may wait at once. `0` means no limit.                                                                 |
| `group_priority`             | `[owner, admin, moderator]`                      | RemoteAdmin group keys, highest priority first. Anybody whose group is not listed waits behind everybody whose group is.   |
| `reserved_slot_skip`         | `true`                                           | Let anybody holding a reserved slot past the queue, up to `max_players` + `reserved_slots` players.                        |
| `reserved_slots_free`        | `false`                                          | Do not count reserved slot holders against `max_players`, so the server may go above it. Off is the game's own behaviour.  |
| `allow_northwood_staff_skip` | `true`                                           | Let verified Northwood staff past the queue.                                                                               |
| `ced_mod_integration`        | `true`                                           | Also read ranks and reserved slots from CedMod when it is installed.                                                       |
| `queue_hint`                 | `<b>The server is full.</b>...`                  | Shown every second to a waiting player. Supports `{position}` and `{total}`. Leave empty to show nothing.                  |
| `queue_stopped_message`      | `The queue has stopped. Please reconnect.`       | Shown to everybody still waiting when the plugin is disabled or the server shuts down.                                     |
| `token_expired_message`      | `Your authentication expired while waiting. ...` | Shown to a player whose session token ran out while they waited.                                                           |

## Placeholders

These can be used in `queue_hint`:

| Placeholder  | Description                           |
|--------------|---------------------------------------|
| `{position}` | The player's place in the queue       |
| `{total}`    | How many players are waiting in total |

# For Support

<div align="left">
<a href='https://discord.gg/KmpA8cfaSA'><img src='https://www.allkpop.com/upload/2021/01/content/262046/1611711962-discord-button.png' height="100"></a>
</div>
