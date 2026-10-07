A data visualization app for Meta Quest 3, built in Unity. Sheets of company
financials stand in front of you in passthrough, one industry at a time or all of
them at once, and you reshape them with your hands. A voice assistant, Ada, can
drive the same tools when asked.

## What You Can Do

Six tools sit on the tool panel. Pick one, then point at the sheet:

| Tool | What it does |
|---|---|
| **Filter** | Hide or show companies (rows) and metrics (columns). A hidden line comes back where it was. |
| **Sort** | Reorder rows or columns, by hand or by their numbers. |
| **Profile** | Raise a whole row or column as a strip floating above the sheet. |
| **Move**, **Rotate**, **Scale** | Place the sheet in the room. |

Undo takes back any of them. Everything the tools do, Ada can do by voice. Ask
Ada to sort by a metric, raise the best company, read out a number or switch
industries.

## What's in the Repository

| Path | What it holds |
|---|---|
| `Assets/Source/` | The app's C# code: `Main` starts the scene and camera rig, `Data` fetches and holds sheets, `Sheet` draws them, `Tools` and `UI` are the hand interface, `InteractionAPI` holds the hand poses, `Audio` wraps the native microphone and speaker plugins, `Poster` builds the figures `Assets/Editor/PosterCapture.cs` renders for the poster, and `Gemini` is the voice assistant and the tools it calls. |
| `Assets/Scenes/File Reader.unity` | The one scene the app runs. |
| `pipeline/` | The Python side: rebuilds the MongoDB database from the raw export in S3 (48,429 rows, cleaned to 1,249 companies across fiscal 2019–2020) and serves it as an API on AWS Lambda. See `pipeline/README.md`. |

The app ships no data. Every sheet is fetched from the API when it is opened.

# Getting Started

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
   it with 6000.4.0f1. The first import takes several minutes.
3. Open `Assets/Scenes/File Reader.unity`.

## Configure the Project's Codebase

The app reads three one-line files from `Assets/StreamingAssets/`. Git ignores
all three, so they stay on your machine and a fresh clone never carries them.

| File | Needed? | Without it |
|---|---|---|
| `api.key` | Yes | The app opens with nothing listed and says so in a notice. |
| `gemini.key` | For voice | Every sheet still works; only the assistant fails to start. |
| `api.url` | No | The app uses the deployed API, which is what you want. |

Everything in `StreamingAssets` is packed into the build, so a built `.apk` carries
both keys. Don't share an `.apk` with anyone you wouldn't give the keys to.

### Connect to the Financial Database

Every sheet is drawn live from a database in the cloud: the API runs on AWS
Lambda and reads MongoDB Atlas, and the app already knows its address. It only
needs the key.

1. Ask the project owner for `api.key`.
2. Put it at `Assets/StreamingAssets/api.key`.

To point the app at an API running on your own computer instead, put its
address on one line in `Assets/StreamingAssets/api.url`, such as
`http://127.0.0.1:8000`. That file goes into headset builds too, and on the
headset `127.0.0.1` is the headset itself. Use your computer's network address
for a headset build, and delete the file when you are done so later builds go
back to the deployed API. `pipeline/README.md` covers running and deploying the
API and rebuilding the data.

### Add Your Gemini API Key

Create a file at `Assets/StreamingAssets/gemini.key` holding your API key on one line and
nothing else.

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
means the database could not be reached — check the headset's internet connection,
`api.key`, and that no stale `api.url` is left over — not that the build failed.

## FAQ

**Does it cost anything?** Yes, to pay for any usage of the Google Gemini API.

**Can I try it without a headset?** Not meaningfully — hand input and passthrough are the interface.

**Where does the data come from, and how do I change it?** A financial export in a
private S3 bucket, rebuilt into MongoDB Atlas by `pipeline/rebuild.py`. See
`pipeline/README.md`.
