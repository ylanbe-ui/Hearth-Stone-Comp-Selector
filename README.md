# HDT Shop Wishlist Overlay

Hearthstone Deck Tracker plugin: highlights your Battlegrounds shop cards
based on an active wishlist/comp, plus an in-game comp builder panel.

## Requirements

- [Hearthstone Deck Tracker](https://hsreplay.net/downloads/) already installed.

## Install (prebuilt)

Grab the zip from the [latest release](../../releases/latest), extract it, and run `Install.bat`.

Manual install: copy `HDT-Shop-Wishlist-Overlay.dll`, `untapped-scry-dotnet.dll` and `Assets/`
into `%APPDATA%\HearthstoneDeckTracker\Plugins`.

## Updates

The plugin checks this repo's releases in the background and applies a newer version
automatically (once you're out of an active BG match), restarting HDT to load it. Manual
check: HDT's Plugins menu > Shop Wishlist Overlay > "Check for Updates...".

## Build from source

Requires a local HDT install; the build looks for `HearthstoneDeckTracker.exe`, `HearthDb.dll`
and `untapped-scry-dotnet.dll` under `deps/` (not included here - copy them from your own HDT
install folder). Then:

```
dotnet build HDT-Shop-Wishlist-Overlay.csproj -c Release
```

`install-only.bat` deploys the build output to your local Plugins folder and restarts HDT.

## Support

Optional: [♥ Support on PayPal](https://www.paypal.com/donate/?business=ylan.be%40gmail.com&currency_code=EUR) — no features are gated behind it.
