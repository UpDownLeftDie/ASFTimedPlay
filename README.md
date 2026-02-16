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
!timedplay [Bots] <AppID1,AppID2,...> <Minutes1,Minutes2,...>
```

### Examples

```
!timedplay 440 60                    # Play TF2 for 60 minutes on current bot
!timedplay 440,570 30,45            # Play TF2 for 30 min, Dota 2 for 45 min
!timedplay 440,570 30               # Play both games for 30 minutes each
!timedplay Bot1 440 60              # Play TF2 for 60 minutes on Bot1
!timedplay Bot1,Bot2 440 60         # Play TF2 for 60 minutes on both bots
!timedplay 440,570 30,*             # Play TF2 for 30 min, then idle Dota 2
```

### Control Commands

```
!timedplay stop                     # Stop playing timed games (keeps idle games)
!timedplay stopall                  # Stop everything (including idling)
!timedplay status                   # Check current status of games and timers
!tp                                 # Short for !timedplay
!i                                  # Same as !idle
```

## Technical Details

### Timer Implementation

- Uses `System.Threading.Timer` with 60-second intervals
- Tracks timer start times to detect drift
- Pauses timers when bot is disconnected or not playing the target game
- Resumes timers when bot reconnects and starts playing

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
