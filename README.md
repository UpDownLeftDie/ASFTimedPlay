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
!playfor [Bots] <AppID1,AppID2,...> <Minutes1,Minutes2,...>
```

### Examples

```
!playfor 440 60                    # Play TF2 for 60 minutes on current bot
!playfor 440,570 30,45            # Play TF2 for 30 min, Dota 2 for 45 min
!playfor 440,570 30               # Play both games for 30 minutes each
!playfor Bot1 440 60              # Play TF2 for 60 minutes on Bot1
!playfor Bot1,Bot2 440 60         # Play TF2 for 60 minutes on both bots
!playfor 440,570 30,*             # Play TF2 for 30 min, then idle Dota 2
```

### Control Commands

```
!playfor stop                     # Stop playing timed games (keeps idle games)
!playfor stopall                  # Stop everything (including idling)
!playfor status                   # Check current status of games and timers
```

### Command Aliases (Better Autocomplete)

```
!pf                              # Same as !playfor (better autocomplete)
!i                               # Same as !idle (better autocomplete)
```

## Recent Fixes

### 1. Timer Accuracy Issues

- **Problem**: Timer was using 61-second intervals instead of 60 seconds, causing significant drift over long periods
- **Fix**: Changed timer interval to exactly 60 seconds and improved timer logic
- **Impact**: Games will now run for the exact time specified (e.g., 10000 hours will actually run for 10000 hours)

### 2. Bot Name in Responses

- **Problem**: Commands always showed the default bot name in responses, even when affecting different bots
- **Fix**: Response now uses the correct bot name based on which bot is being acted upon
- **Impact**: Clearer feedback about which bot is performing actions

### 3. Stop Command Improvements

- **Problem**: Stop commands didn't properly clear all timer state
- **Fix**: Enhanced stop commands to properly dispose timers and clear paused states
- **Impact**: More reliable stopping of games and timers

### 4. Added Status Command

- **New Feature**: `!playfor status` command to check current game and timer status
- **Shows**: Remaining minutes for each game, timer status (active/paused/inactive), idle game status
- **Impact**: Better visibility into plugin state

### 5. Enhanced Logging and Debugging

- **Added**: Detailed logging for timer updates and game state changes
- **Added**: Timer drift detection and warnings
- **Added**: Better error handling and debugging information
- **Impact**: Easier troubleshooting of timing issues

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

- Check if the bot is actually playing the game (use `!playfor status`)
- Verify the bot is connected and logged on
- Check ASF logs for timer drift warnings

### Timer appears paused

- The timer pauses when the bot is disconnected or not playing the target game
- It will automatically resume when the bot reconnects and starts playing
- Use `!playfor status` to check timer state

### Stop command not working

- Try `!playfor stopall` to stop everything
- Check ASF logs for any errors
- Restart ASF if necessary

## License

This plugin is open source and available under the same license as ArchiSteamFarm.
