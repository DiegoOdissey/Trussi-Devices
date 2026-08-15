# TrussiApp
<p align="center">
  <img src="https://i.imgur.com/If1GAt0.png">
</p>

A custom WinUI application that controls Trussi devices (which are currently not for sale, obviously). Every device appears as a card in the home screen, connected devices will be automatically recognized and marked with a green dot. Each device can be highly customized, and it's possible to assign for each button a determined action. Right now the buttons can be paired with those actions:

- **Media Control**: Control media playback like videos and music, for example Previous, Pause/Play and Next.
- **Open Application**: Open an .exe file
- **Shortcut**: Execute a keyboard shortcut combination (Alt + F4, Shift + F5, etc...)
- **Open website**: Open a website with the OS's default browser, given the link.

Knob buttons have different options:
- **Main volume**: The knob will control Windows volume
- **Application volume**: The knob will control a specific process name (which can be used to control Discord's volume, Gaming, Browser, etc...)

Knobs featuring a press button have also the normal buttons options.

Devices featuring screens (like the AxiDeck) have an option to show a label for each button on said screen.

If you want a custom TrussiDevice feel free to contact me on Discord (@diegoodssey)!

---

## Repository Structure

```
AxiDeck/
├── firmware/          Arduino sketch (.ino) — runs on the Nano
├── TrussiApp/            C# WinUI 3 desktop app (current)
└── TrussiApp-python/     Python tray app (deprecated)
```

---

## AxiDeck Firmware

<p align="center">
  <img height=400 src="https://i.imgur.com/c3I0nDB.png">
</p>

The AxiDeck firmware is written for **Arduino Nano**. It currently features:

- Serial handshake (`CONNECT`)
- Current time syncronization
- Currert song (Spotify/Apple Music/Youtube) synchronization, which appears on Screen #1 (Left)
- Knob and button event reporting back to the host
- OLED screen managing with TCA multiplexer

The firmware can be flashed with the Arduino IDE.

### External libraries required:
- Wire
- Adafuit_GFX
- Adafruit_SSD1306

---

## TrussiApp — Windows Desktop (C# / WinUI 3)

The current host application. Runs on Windows 10/11.

**Requirements**
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/) runtime
- .NET 9

**Run from source**

```bash
cd TrussiApp
dotnet run
```

**Features**
- Auto-detects the devices on any COM port
- Serial handshake + periodic synchronization
- Reconnects automatically on disconnect
- Developer mode (live log debugging)

## License

MIT
