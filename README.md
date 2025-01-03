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

ASFTimedPlay is a plugin for ArchiSteamFarm that allows you set your bots to play games for a certain amount of time.

---

## Usage

`!playfor [Bots] <AppID1,AppID2,...> <Minutes>`
Minutes can be a single number or a comma separated list of numbers (one for each appID)

### Examples

- `!playfor ASF 400 60` - All bots play appID 400 for 60 minutes
- `!playfor ASF 400,440 60` - All bots plays appID 400 for 60 minutes, then appID 440 for 60 minutes
- `!playfor botname 400,440 60,10` - Only botname plays appID 400 for 60 minutes, then appID 440 for 10 minutes
