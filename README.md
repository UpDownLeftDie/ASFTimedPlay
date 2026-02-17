# ASF Timed Play Plugin

A plugin for ArchiSteamFarm that allows you to play games for a specific amount of time and then automatically switch to the next game or start idling.

## Features

- **Timed Game Play**: Play games for a specified number of minutes
- **Sequential Play**: Automatically switch to the next game when time is up
- **Idle Support**: Automatically start idling a specified game after all timed games are complete
- **Multi-Bot Support**: Control multiple bots with a single command
- **Persistent State**: Games and timers persist across ASF restarts
- **Status Monitoring**: Check the current status of games and timers

## Commands

### Basic Usage

```
!timedplay [Bots] <AppID1,AppID2,...> <Duration1,Duration2,...>
```

**Durations**: plain minutes (`MM`), `HH:MM`, `DD:HH:MM`, or unit form (`10h45m`, `1d 2h`). Use `*` as a duration to mark the rest as idle-after games (e.g. `30,*` = first game timed, then idle the others).

### Examples

```
!timedplay 440 60                   # Play TF2 for 60 minutes on current bot
!timedplay 440 8:30                 # Play TF2 for 8 hours 30 minutes (HH:MM)
!timedplay 440 1:9:20               # Play TF2 for 1 day 9h 20m (DD:HH:MM)
!timedplay 440 2h                   # Play TF2 for 2 hours (unit form)
!timedplay 440,570 30,45           # Play TF2 30 min, Dota 2 for 45 min
!timedplay 440,570 8:30,70         # TF2 for 8h30m, Dota 2 for 70 min
!timedplay 440,570 30               # Play both games for 30 minutes each
!timedplay Bot1 440 60              # Play TF2 for 60 minutes on Bot1
!timedplay Bot1,Bot2 440 60         # Play TF2 for 60 minutes on both bots
!timedplay 440,570 30,*            # Play TF2 for 30 min, then idle Dota 2
!timedplay 440,570 2h,45,*         # 440 for 2h, 570 for 45 min, then idle both
```

### Idle command (`!idle`)

```
!idle [Bots] <AppID1,AppID2,...>
```

Start idling the given games (no time limit). Use when the bot is free or to queue idling when busy.

```
!idle 440,570                       # Idle TF2 and Dota 2 on current bot
!idle Bot1 440,570                  # Idle on Bot1
!idle stop                          # Stop idling and clear idle list
```

Alias: `!i` (same as `!idle`).

### Control Commands

```
!timedplay stop                     # Stop playing timed games (keeps idle games)
!timedplay stopall                  # Stop everything (including idling)
!timedplay status                   # Check current status of games and timers
!tp                                 # Short for !timedplay
```

## Technical Details

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

### Games not running for the full time

- Check if the bot is actually playing the game (use `!timedplay status`)
- Verify the bot is connected and logged on
- Check ASF logs for timer drift warnings

### Timer appears paused

- The timer pauses when the bot is disconnected or not playing the target game
- It will automatically resume when the bot reconnects and starts playing
- Use `!timedplay status` to check timer state

### Stop command not working

- Try `!timedplay stopall` to stop everything
- Check ASF logs for any errors
- Restart ASF if necessary

## License

This plugin is open source and available under the same license as ArchiSteamFarm.
