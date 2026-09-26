# Instructions for Getting Started

This is a data visualization project for Meta Quest, built in Unity. Sheets of data
stand in front of you in passthrough, you reshape them with your hands, and a voice
assistant can drive the same tools when asked.

## Prerequisites

- [ ] A Meta Quest 3 with a USB-C cable
- [ ] A Google Gemini API key on a paid tier
- [ ] The API key file, `api.key`, from the project owner
- [ ] Internet access on the headset

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

### Connect to the Financial Database

Every sheet is drawn live from a database in the cloud: the API runs on AWS
Lambda and reads MongoDB Atlas, and the app already knows its address. It only
needs the key.

1. Ask the project owner for `api.key`.
2. Put it at `Assets/StreamingAssets/api.key`. Git ignores this path, so the key
   stays on your machine and a fresh clone never carries one.

Without it the app opens with nothing listed and says so in a notice.

To point the Editor at an API running on your own computer instead, put its
address on one line in `Assets/StreamingAssets/api.url` (also git-ignored), such
as `http://127.0.0.1:8000`. A headset build refuses plain `http://`, so this is
for the Editor; `pipeline/README.md` covers running and deploying the API and
rebuilding the data.

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
means the database could not be reached — check the headset's internet connection and
`api.key` — not that the build failed.

## FAQ

**Does it cost anything?** Yes, to pay for any usage of the Google Gemini API.

**Can I try it without a headset?** Not meaningfully — hand input and passthrough are the interface.
