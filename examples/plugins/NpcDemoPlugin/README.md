# NPC Demo Plugin

Chat commands that exercise the NPC, navigation and bot API. Most act on the point under your crosshair. Type `/npcdemo` in game for the list.

No cheats are needed. Every reply also goes to the server console, prefixed `[NpcDemo]`. Commands work in the air: anything that starts from you starts from the navmesh point below you.

## Spawning

| Command | Description |
|---------|-------------|
| `/spawn [normal\|medic\|melee] [count] [enemy\|ally]` | Spawns lane troopers at the crosshair with `CNPC_Trooper.Create`. They join the nearest lane. |
| `/npc <subclass> [enemy\|ally\|neutral]` | Spawns any NPC by VData subclass name with `CAI_BaseNPC.Create`, such as `npc_boss_tier1` or `neutral_trooper_strong`. |

## Orders

| Command | Description |
|---------|-------------|
| `/goto` | NPCs within 3000 units walk to the crosshair in legs along a `Nav.FindPath` route (`MoveTo`, see `Walk.cs`), and the demo reports how many got there. Neutral camp creeps stay at their camps. |
| `/attack` | NPCs within 3000 units attack what you aim at (`SetEnemy`). |
| `/stop` | NPCs within 3000 units drop their orders (`Stop`). Also ends `/horde`, `/chaseme` and bot walking. |
| `/hold` | Toggles troopers standing still instead of walking their lanes (`SetSchedule`, `OnNpcSelectSchedule`). |

## Groups

| Command | Description |
|---------|-------------|
| `/horde [count]` | Spawns enemy melee troopers 700-1400 units away that chase you. Again to remove them. |
| `/chaseme` | Troopers within 3000 units chase you. Again to stop. |
| `/squad [count]` | Spawns allied troopers that follow you as minions (`SetMinion`, `SetSummoner`). Their kills are yours. Again to dismiss. |

## Information

| Command | Description |
|---------|-------------|
| `/info` | Describes the NPC you aim at, or the one nearest the crosshair. |
| `/npcs` | Counts the NPCs on the map by class. |

## Navigation

| Command | Description |
|---------|-------------|
| `/path` | Draws the path from you to the crosshair (`Nav.FindPath`), colored by how each leg is crossed. |
| `/nav` | Shows the nav area at the crosshair, and whether you can walk there in a straight line (`Nav.Raycast`). |
| `/randompoints` | Marks 10 random reachable points 300-1500 units away (`Nav.GetRandomPoint`). |
| `/block`, `/unblock` | Blocks a 300x300 box of the navmesh at the crosshair (`Nav.CreateBlocker`), or removes the blocks. |

## Bots

| Command | Description |
|---------|-------------|
| `/bot [hero] [enemy\|ally]` | Adds a bot hero at the crosshair (`Bots.Add`). |
| `/botai [on\|off]` | Turns the bot AI on or off (`Bots.SetAiEnabled`). |
| `/botcome` | Bots walk to you along a navmesh path (`NavPathFollower`, `Bots.SetMoveInput`). Turns the bot AI off. Again to stop. |
| `/botgo` | Bots walk to the crosshair on their brain's own path (`MoveTo`, `ApplyMovement`), and the demo reports how many got there. Turns the bot AI off. |

`/cleanup` removes every NPC, block and bot the demo made.
