# Accounting Platform

A data visualization app for Meta Quest 3, built in Unity, for exploring and comparing the
financial ratios of public companies in 3D.

Sheets of company financials stand in front of you in passthrough, one industry at a time or all of
them at once, and you reshape them with your hands. A voice assistant, Ada, can
drive the same tools when asked. The data covers 1,249 companies across ten industries
for fiscal 2019 and 2020.

To run it you need a Quest 3 and an API key from the project owner. Hand input and
passthrough are the interface, so there's no meaningful way to try it without a headset.

https://github.com/user-attachments/assets/8bc7e6bd-d413-4244-b450-090d7d574d3e

## Quick Start

You need a Meta Quest 3 with a USB-C cable, a computer (macOS or Windows) that can run Unity 6,
and `api.key` from the project owner. Voice also needs a Google Gemini API key on a paid tier.

1. **Prepare the headset:** turn on Developer Mode, accept **Allow USB debugging**, and run Space Setup.
2. **Install Unity 6000.4.0f1** through Unity Hub, with **Android Build Support**, **OpenJDK** and **Android SDK & NDK Tools**.
3. **Open the project:** clone the repository, add it in Unity Hub, and open `Assets/Scenes/Main.unity`.
4. **Add your keys:** put `api.key` (and `gemini.key`, for voice) in `Assets/StreamingAssets/`. A built `.apk` carries both, so don't share it with anyone you wouldn't give the keys to.
5. **Build:** in **File → Build Profiles**, select **Quest Default** and your headset, then click **Build And Run**. The first build takes 10 to 30 minutes.

Done when you are standing in passthrough with the industries listed beside you. The
[setup guide](docs/setup.md) has every step in full.

## What You Can Do

Six tools sit on the tool panel. Pick one, then reach out and touch the sheet:

| Tool | What it does |
|---|---|
| **Filter** | Hide or show companies (rows) and metrics (columns). A hidden line comes back where it was. |
| **Sort** | Grab a row or column and drag it to a new place. |
| **Profile** | Raise a whole row or column as a strip floating above the sheet, with its count, minimum, maximum, average and sum. |
| **Move**, **Rotate**, **Scale** | Place the sheet in the room. |

**Undo** takes back the last edit and **Undo All** takes back every edit on the
open sheet. Everything the tools do, Ada can do by voice, and a little more:
ask Ada to sort by a metric, raise the best company, read out a number, compare
two years, or switch industries.

## Documentation

- [Setup](docs/setup.md): prepare the headset, install Unity, add your keys, and build.
- [Using the App](docs/usage.md): the hand controls, and how to read a sheet.
- [FAQ](docs/faq.md): common questions and fixes for setup, the tools and Ada.
- [Architecture](docs/architecture.md): how the pieces connect, the repository layout, and the tech stack.
- [Backend](Backend/README.md): rebuild the data, and run, deploy and test the API.

## Status

Complete. Version 1.0 is feature-complete for its purpose: exploring fiscal
2019–2020 ratios for 1,249 public companies on Meta Quest 3. No further
features are planned.

## License

Released under the [MIT License](LICENSE). Third-party code keeps its own
license: TextMesh Pro and the NuGet packages in `Assets/Packages/`, and the Meta XR
SDKs listed in `Packages/manifest.json`.
