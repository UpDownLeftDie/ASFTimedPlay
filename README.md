# ASFTimedPlay

[![Steam donate](https://img.shields.io/badge/Steam-donate-000000.svg?logo=steam)](https://steamcommunity.com/tradeoffer/new/?partner=17031731&token=UO4iqr4V)
[![Patreon](https://img.shields.io/badge/Patreon-support-000000.svg?logo=patreon)](https://www.patreon.com/camkitties)
[![Twitch](https://img.shields.io/badge/Twitch-CamKitties-000000.svg?logo=twitch)](https://www.twitch.tv/camkitties)
[![Twitch](https://img.shields.io/badge/Twitch-UpDownLeftDie-000000.svg?logo=twitch)](https://www.twitch.tv/updownleftdie)

<!-- [![BTC donate](https://img.shields.io/badge/BTC-donate-f7931a.svg?logo=bitcoin)](https://www.blockchain.com/explorer/addresses/btc/3HwcgZbtoF5vSxJkNUvThVSJipKi7r5EqU)
[![ETH donate](https://img.shields.io/badge/ETH-donate-3c3c3d.svg?logo=ethereum)](https://www.blockchain.com/explorer/addresses/eth/0xA1F7Ba62C5a3A8b93Fe6656936192432F328a366)
[![LTC donate](https://img.shields.io/badge/LTC-donate-a6a9aa.svg?logo=litecoin)](https://live.blockcypher.com/ltc/address/MJCeBEZUsNgDhRhqbLFfPiDcf7CSrdvmZ3)
[![USDC donate](https://img.shields.io/badge/USDC-donate-2775ca.svg?logo=cashapp)](https://etherscan.io/address/0xCf42D9F53F974CBd7c304eF0243CAe8e029885A8) -->

---

## Description

ASFTimedPlay is a plugin for ArchiSteamFarm that allows you to set your bots to play games for specific durations and optionally idle a game afterwards.

---

## Commands

`!playfor` - Play games for a specified duration
`!idle` - Idle a game after playing for a specified duration

### PlayFor Command

`!playfor [Bots] <AppID1,AppID2,...> <Minutes1,Minutes2,...>`

The PlayFor command lets you set up timed game sessions. You can specify multiple games and their durations, with an optional idle game at the end.

#### PlayFor Command Examples

- `!playfor ASF 400 60` - All bots play AppID 400 for 60 minutes
- `!playfor ASF 400,440 60` - All bots play AppID 400 and 440 for 60 minutes each
- `!playfor botname 400,440 60,10` - Bot "botname" plays AppID 400 for 60 minutes, then AppID 440 for 10 minutes
- `!playfor ASF 400,440,500 60,30,*` - All bots play AppID 400 for 60 minutes, AppID 440 for 30 minutes, then idle AppID 500
- `!playfor stop` - Stops all PlayFor sessions on the current bot

### Idle Command

`!idle [Bots] <AppID>`

The Idle command sets up a game to be idled during bot downtime (when not farming cards or performing other tasks).

#### Idle Command Examples

- `!idle ASF 400` - All bots will idle AppID 400 during downtime
- `!idle botname 440` - Bot "botname" will idle AppID 440 during downtime
- `!idle stop` - Stops idling on the current bot

---

## Features

- Play multiple games for specified durations
- Automatically switch between games based on configured times
- Optional idle game after completing timed sessions
- Persistent configuration that survives bot restarts
- Automatic resumption of sessions after disconnections
- Compatible with ASF's card farming and other features
