# ASF Timed Play Plugin

A plugin for ArchiSteamFarm that allows you to play games for a specific amount of time and then automatically switch to the next game or start idling.

## Features

- **Timed Game Play**: Play games for a specified number of minutes
- **Concurrent or sequential**: By default, play multiple games at once (up to 32); add the `sequential` flag to play one game at a time instead
- **Idle Support**: Automatically start idling a specified game after all timed games are complete
- **Multi-Bot Support**: Control multiple bots with a single command
- **Persistent State**: Games and timers persist across ASF restarts
- **Status Monitoring**: Check the current status of games and timers

## Commands

### Basic Usage

```
!timedplay [Bots] <AppID1,AppID2,...> <Duration1,Duration2,...> [sequential|seq]
```

- **Bots**: Omit for current bot; use a bot name, comma-separated names, or **ASF** to target all bots (same as built-in ASF commands).
- **Durations**: plain minutes (`MM`), `HH:MM`, `DD:HH:MM`, or unit form (`10h45m`, `1d 2h`). Use `*` as a duration to mark the rest as idle-after games (e.g. `30,*` = first game timed, then idle the others).
- **Sequential mode**: Add `sequential` or `seq` at the end to play one game at a time instead of all at once. Useful if you prefer the older behavior or need to avoid concurrent play.

### Examples

```
!timedplay 440 60                   # Play TF2 for 60 minutes on current bot
!timedplay 440 8:30                 # Play TF2 for 8 hours 30 minutes (HH:MM)
!timedplay 440 1:9:20               # Play TF2 for 1 day 9h 20m (DD:HH:MM)
!timedplay 440 2h                   # Play TF2 for 2 hours (unit form)
!timedplay 440,570 30,45            # Play TF2 30 min, Dota 2 for 45 min
!timedplay 440,570 8:30,70          # TF2 for 8h30m, Dota 2 for 70 min
!timedplay 440,570 30               # Play both games for 30 minutes each
!timedplay Bot1 440 60              # Play TF2 for 60 minutes on Bot1
!timedplay Bot1,Bot2 440 60         # Play TF2 for 60 minutes on both bots
!timedplay ASF 440 60               # Play TF2 for 60 minutes on all bots
!timedplay 440,570 30,*             # Play TF2 for 30 min, then idle Dota 2
!timedplay 440,570 2h,45,*          # 440 for 2h, 570 for 45 min, then idle both
!timedplay 440,570 30,45 sequential   # Play TF2 for 30 min, then Dota 2 for 45 min (one at a time)
!timedplay 440,100 8:30,70 seq        # Same as sequential
```

### Idle command (`!idle`)

```
!idle [Bots] <AppID1,AppID2,...>
```

Start idling the given games (no time limit). Use when the bot is free or to queue idling when busy. **Bots**: omit for current bot; use a bot name, comma-separated names, or **ASF** for all bots.

```
!idle 440,570                       # Idle TF2 and Dota 2 on current bot
!idle Bot1 440,570                  # Idle on Bot1
!idle ASF 440,570                   # Idle on all bots
!idle stop                          # Stop idling on current bot
!idle stop ASF                      # Stop idling on all bots
```

Alias: `!i` (same as `!idle`).

### Control Commands

```
!timedplay stop [Bots]              # Stop timed games (Bots optional; use ASF for all)
!timedplay status [Bots]            # Check status (Bots optional; use ASF for all)
!timedplay debug                    # Output version, config path, and config JSON (for bug reports)
!tp                                 # Short for !timedplay
```

## Technical Details

### Concurrent vs sequential mode

- **Default (concurrent)**: All listed games are played at the same time, up to ASF’s limit of 32. When a game’s time is up, it is dropped and the rest keep running until their time is up.
- **Sequential** (`sequential` or `seq`): One game is played at a time. When its time is up, the next game in the list starts. The choice is stored in config for that session.

### Timer Implementation

- Uses `System.Threading.Timer` with 60-second intervals
- Tracks timer start times to detect drift
- Pauses timers when bot is disconnected or not playing the target game
- Resumes timers when bot reconnects and starts playing

### Idle behavior (`!idle`)

- **Interruptible**: Idling does not block ASF or the account. Card farming, redeeming, or playing on another device will take over; the plugin does not force the idle games.
- **Resume when free**: You can run `!idle` even when a bot is farming or redeeming; the idle list is saved and idling starts when the bot is free. A background check runs every 2 minutes to resume idling when the account is no longer in use elsewhere or by ASF.
- **No interrupt when in use**: The plugin uses ASF’s `IsPlayingPossible` (account free). It never calls Play when the account is occupied (e.g. user on another system), so it does not interrupt. Once the account is free again, the next 2‑minute check will resume idling.
- **!idle stop**: Stops idling and clears the idle list for that bot.

### Configuration

- Configuration is stored in `ASFTimedPlay.json`
- Persists across ASF restarts
- Automatically cleans up empty entries

### Bot Integration

- Integrates with ASF's card farming system
- Respects ASF's game prioritization
- Works alongside other ASF plugins

## Installation

1. Download the plugin files
2. Place them in your ASF plugins directory
3. Restart ASF
4. Use the commands to start playing games

## Troubleshooting

### Getting help / reporting issues

Run **`!timedplay debug`** and share the output when asking for help or reporting a bug. It prints the plugin version, config file path, whether the config file exists, how many timers/entries are active, and the full config JSON. This information helps diagnose problems without exposing secrets (the config only stores game IDs and durations).

### Games not running for the full time

- Check if the bot is actually playing the game (use `!timedplay status`)
- Verify the bot is connected and logged on
- Check ASF logs for timer drift warnings
- **Try sequential mode**: If you use multiple games at once, run the same games with `sequential` (or `seq`) at the end of the command. If the issue goes away, the problem may be specific to concurrent play (e.g. Steam/ASF behavior with several games at once).

### Timer appears paused

- The timer pauses when the bot is disconnected or when ASF reports the bot is farming *different* games than the timed set.
- The timer only counts down when the bot is **connected** and **not** "Playing not available" (ASF's `IsConnectedAndLoggedOn` and `!PlayingBlocked`). When an account is in use elsewhere, ASF shows it as offline, so the timer stays paused. See ASF's `Commands.ResponseStatus` and [Plugins development](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins-development) for API usage.
- Use `!timedplay status` to check timer state.

### Stop command not working

- Try `!timedplay stop ASF` to stop everything
- Check ASF logs for any errors
- Restart ASF if necessary

### Testing with sequential mode

If behavior is odd with multiple games (wrong times, games stopping early, or timer issues), try the same command with **`sequential`** or **`seq`** added at the end. Sequential mode plays one game at a time and can help narrow down whether the issue is with concurrent play or with the plugin in general.

## License

This plugin is open source and available under the same license as ArchiSteamFarm.
