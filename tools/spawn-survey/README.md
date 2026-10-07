# Spawn survey

Finds out which entity names survive being spawned, by spawning each one alone on a real
dedicated server, and which console variables a dedicated server never registers. The
generator of `DeadworksManaged.Game` reads the result
(`managed/DeadworksManaged.GameGen/spawn-survey.json`) to decide which spawn functions and
console variables to emit and what each function's documentation says.

- `SpawnSurvey/` is the plugin. It spawns the names in its list one at a time, logs what
  happened to each, and ends the server silently when a spawn faults.
- `survey.py` runs the servers, restarts one that died or hung at the name after the one
  that killed it, and writes the result.

How to run it is in `managed/DeadworksManaged.Game/README.md`, under Regenerating.

The servers crash on purpose, dozens of times. Use a private copy of the server, not one
people play on. They run on a Windows desktop of their own, so nothing they open appears on
yours.

A result per name is one of:

| | |
|---|---|
| `lived` | Still there half a second after spawning. |
| `vanished` | Created, then removed itself. |
| `refused` | The game did not create it. |
| `crashed` | The server died; `at` is the module and offset of the fault when the plugin caught it. |
| `hung` | The server stopped answering and was killed. |

The model note on a result depends on the order names were tried in: a model an earlier
spawn loaded stays loaded.
