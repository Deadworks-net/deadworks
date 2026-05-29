# DiagnosticsPlugin

Example plugin for exercising visitor paths and common admin-style commands.

It mounts:

- fast usercmd callbacks via `OnFastProcessUsercmds`
- fast net-message visitors for pause state/request and chat `svc_UserMessage` inner ids
- chat command serialization through `OnChatMessage` / command registration
- selected game events and entity touch callbacks

Commands:

- `/heal N`
- `/damage N`
- `/teleport` or `/tp`
- `/respawn`

This plugin is meant for local diagnostics, not production servers. High-frequency entity touch events are summarized every 5 seconds by default; set `DEADWORKS_DIAGNOSTICS_VERBOSE_TOUCH=1` for per-touch logging.
