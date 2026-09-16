# Instructions for Getting Started

This is a data visualization project for Meta Quest, built in Unity. Sheets of data
stand in front of you in passthrough, you reshape them with your hands, and a voice
assistant can drive the same tools when asked.

## Prerequisites

- [ ] A Meta Quest 3 with a USB-C cable
- [ ] A Google Gemini API key on a paid tier
- [ ] PostgreSQL and Python 3.13 on your computer
- [ ] A Wi-Fi network the headset and your computer both join

## Prepare the Meta Quest 3

1. Pair the headset with the Meta Horizon app on your phone.
2. Create or join an organization at developer.meta.com and verify your account.
3. In the Horizon app, open your headset's settings, turn on Developer Mode, and restart the headset.
4. Plug the headset into your computer, put it on, and accept **Allow USB debugging** — check *Always allow from this computer*.
5. Run Space Setup. The app requires passthrough and reads your room's scene data.

## Download and Set Up Unity

1. Install Unity Hub on your computer and sign in or create an account.
2. Install the Unity version **6000.4.0f1**.
3. During installation, check **Android Build Support** and both of its sub-modules, **OpenJDK** and **Android SDK & NDK Tools**.

## Download and Set Up the Project

1. Clone the repository, or download the ZIP and unpack it.
2. In Unity Hub choose **Add → Add project from disk**, select the cloned folder, and open
   it with 6000.4.0f1.
3. Open `Assets/Scenes/File Reader.unity`.

## Configure the Project's Codebase

### Connect with the PostgreSQL Server

Every sheet is drawn live from the database, so this has to be running before you open the app.
Both the database and the service run on your own computer, and the headset reaches them across
your Wi-Fi; nothing is hosted anywhere.

1. Install PostgreSQL, then create the database the service expects: `createdb nasba`.
2. Build its tables and load it by following `pipeline/README.md`. The financial export that
   seeds it is not in this repository — ask the project owner for it.
3. Install the service's dependencies, in a virtual environment so your system Python is left
   alone: `python3 -m venv pipeline/.venv && pipeline/.venv/bin/pip install -r pipeline/requirements.txt`.
4. From inside `pipeline/`, serve it on your network: `.venv/bin/uvicorn api:app --host 0.0.0.0 --port 8000`.
5. Create a file at `Assets/StreamingAssets/api.url` holding one line — your computer's address
   on that network, such as `http://192.168.1.42:8000`. `ipconfig getifaddr en0` prints it on
   macOS, `ipconfig` on Windows. Git ignores this file, because the answer differs per machine.
6. Keep the headset and the computer on the same Wi-Fi.

Without a reachable service the app opens with nothing listed and says so in a notice.

### Add Your Gemini API Key

Create a file at `Assets/StreamingAssets/gemini.key` holding your API key on one line and
nothing else. Git ignores this path, so your key stays on your machine and a fresh clone
never carries one.

Without it the app still runs and every sheet still works; only the assistant fails to start.

## Building to the Meta Quest 3

1. Connect the headset by USB and put it on, so it stays awake.
2. Open **File → Build Profiles**, select **Quest Default**, and choose your headset in the
   device list. Platform, architecture, and SDK levels are already set in that profile —
   change nothing.
3. Click **Build And Run**. The first build takes 10 to 30 minutes; later builds are far
   quicker.
4. In the headset, accept the microphone prompt at launch. Denying it leaves everything
   working except voice.

Done when you are standing in passthrough with the industries listed beside you. An empty list
means the service could not be reached, not that the build failed.

## FAQ

**Does it cost anything?** Yes, to pay for any usage of the Google Gemini API.

**Can I try it without a headset?** Not meaningfully — hand input and passthrough are the interface.
