# Accounting Platform

A data visualization app for Meta Quest 3, built in Unity. Sheets of company
financials stand in front of you in passthrough, one industry at a time or all of
them at once, and you reshape them with your hands. A voice assistant, Ada, can
drive the same tools when asked.

- [What You Can Do](#what-you-can-do)
- [What's in the Repository](#whats-in-the-repository)
- [Getting Started](#getting-started)
- [Using the App](#using-the-app)
- [FAQ](#faq)

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

## What's in the Repository

| Path | What it holds |
|---|---|
| `Assets/Source/` | The app's C# code: `Main` starts the scene and camera rig, `Data` fetches and holds sheets, `Sheet` draws them, `Tools` and `UI` are the hand interface, `InteractionAPI` holds the hand poses, `Audio` wraps the native microphone and speaker plugins, `Poster` builds the figures `Assets/Editor/PosterCapture.cs` renders for the poster, and `Gemini` is the voice assistant and the tools it calls. |
| `Assets/Scenes/File Reader.unity` | The one scene the app runs. |
| `Assets/StreamingAssets/` | Where your key files go (see [Configure the Project](#configure-the-project)). |
| `Assets/Settings/Build Profiles/Quest Default.asset` | The build profile for the headset. |
| `pipeline/` | The Python side: rebuilds the MongoDB database from the raw export in S3 (48,429 rows, cleaned to 1,249 companies across fiscal 2019–2020) and serves it as an API on AWS Lambda. See `pipeline/README.md`. |

The app ships no data. Every sheet is fetched from the API when it is opened.

## Getting Started

### Prerequisites

- [ ] A Meta Quest 3 with a USB-C cable
- [ ] A computer (macOS or Windows) that can run Unity 6
- [ ] A Google Gemini API key on a paid tier (only needed for voice)
- [ ] The API key file, `api.key`, from the project owner
- [ ] Internet access on the headset

### Prepare the Meta Quest 3

1. Pair the headset with the Meta Horizon app on your phone.
2. Create or join an organization at developer.meta.com and verify your account.
3. In the Horizon app, open your headset's settings, turn on Developer Mode, and restart the headset.
4. Plug the headset into your computer, put it on, and accept **Allow USB debugging** — check *Always allow from this computer*.
5. Run Space Setup. The app requires passthrough and reads your room's scene data.

### Download and Set Up Unity

1. Install Unity Hub on your computer and sign in or create an account.
2. Install the Unity version **6000.4.0f1**. Other versions, newer ones included, may change the project on import.
3. During installation, check **Android Build Support** and both of its sub-modules, **OpenJDK** and **Android SDK & NDK Tools**.

### Download and Set Up the Project

1. Clone the repository, or download the ZIP and unpack it.
2. In Unity Hub choose **Add → Add project from disk**, select the cloned folder, and open
   it with 6000.4.0f1. The first import takes several minutes.
3. Open `Assets/Scenes/File Reader.unity`.

### Configure the Project

The app reads three one-line files from `Assets/StreamingAssets/`. Git ignores
all three, so they stay on your machine and a fresh clone never carries them.

| File | Needed? | Without it |
|---|---|---|
| `api.key` | Yes | The app opens with nothing listed and shows a **No Data** notice. |
| `gemini.key` | For voice | Every sheet still works; only the assistant fails to start. |
| `api.url` | No | The app uses the deployed API, which is what you want. |

Each file holds its value on one line and nothing else — no quotes, no
`KEY=` prefix. Everything in `StreamingAssets` is packed into the build, so a
built `.apk` carries both keys. Don't share an `.apk` with anyone you wouldn't
give the keys to.

#### Connect to the Financial Database

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

#### Add Your Gemini API Key

1. Create a key at Google AI Studio (aistudio.google.com) and turn on billing for its project.
2. Put it at `Assets/StreamingAssets/gemini.key`.

### Building to the Meta Quest 3

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

## Using the App

You use the app with your bare hands; controllers are not needed.

1. **Open the tool panel.** Raise a hand and look at it. A round button floats over it —
   press it with a fingertip of the other hand to open or close the tool panel.
2. **Pick a sheet.** The list beside you holds one entry per industry, plus
   **All Industries** at the top. The app opens on All Industries, which shows the two
   largest companies from each industry. Press an entry to open it; it loads the first
   time you open it.
3. **Pick a tool** on the panel, then touch the sheet:
   - **Filter** — choose **By Company** or **By Metric**, then press names in the list,
     or poke a bar, to hide or show that line.
   - **Sort** — pinch a row or column by its handle and drag it to where you want it.
   - **Profile** — press a bar and sweep your finger along the row or column you want
     raised. Its summary appears above it.
   - **Move**, **Rotate**, **Scale** — grab the sheet and move your hand.
4. **Undo** — the **Undo** and **Undo All** buttons sit at the top of the panel.
5. **Talk to Ada.** Press the **Assistant** tile on the panel. It reads
   *Connecting…* for a moment, then Ada greets you. Speak normally; there is no
   wake word. Press the tile again to turn Ada off.

### Reading a Sheet

- **Rows are companies; columns are financial ratios**, grouped into five kinds:
  liquidity, efficiency, solvency, profitability and valuation.
- **Each metric is two bars side by side**, one for fiscal 2019 and one for fiscal 2020.
- **Bar height is scaled within each metric.** A tall bar means large *for that metric*;
  don't compare bar heights across different metrics.
- **A bar below the base plane is a negative value.**
- **Colour is the company's industry.** It is the same on every sheet and can't be changed.

## FAQ

### General

**What is this app for?**
Exploring and comparing the financial ratios of public companies in 3D. Instead of
scrolling a spreadsheet, you stand in front of it, hide what you don't need, reorder
what's left, and raise the rows you care about — by hand or by asking Ada.

**Where does the data come from?**
A financial export in a private S3 bucket, cleaned to 1,249 companies across ten
industries for fiscal 2019 and 2020, then loaded into MongoDB Atlas by
`pipeline/rebuild.py`. The app reads it live through the API, so it always shows the
database as it stands now. See `pipeline/README.md` to change or rebuild it.

**Can I load my own spreadsheet?**
Not from inside the app. The sheets come from the financial database; to change what
they hold, change the data in the pipeline.

**Does it cost anything?**
The app and the financial database are free to you. Voice uses the Google Gemini API,
which bills your Google account for what Ada uses.

**Why does the Gemini key need a paid tier?**
Ada can look things up on Google (news, filings, market figures you ask about), and
Google only allows that search on billing-enabled keys; a free-tier key is rejected
when the session starts. To use a free-tier key, open the scene, find the
`GeminiClient` component, and untick **Enable Web Search**. Ada then works without
looking anything up.

**Can I try it without a headset?**
Not meaningfully — hand input and passthrough are the interface.

**Do I need controllers?**
No. Everything is done with hand tracking. If the headset doesn't see your hands, make
sure hand tracking is turned on under the headset's **Settings → Movement Tracking**.

### Setup and Building

**I don't have `api.key`. Can I still use the app?**
Not with the deployed database — it refuses requests without the key. Ask the project
owner, or run the API on your own computer (see `pipeline/README.md`), which doesn't ask
for one.

**Unity says my version doesn't match.**
Install exactly **6000.4.0f1** from Unity Hub's **Installs → Install Editor → Archive**
and open the project with it.

**My headset doesn't appear in the Build Profiles device list.**
Check that Developer Mode is on, the cable carries data (not only charge), and you
accepted **Allow USB debugging** inside the headset. Unplug, replug, and look in the
headset for the prompt again. Pressing **Refresh** next to the device list helps.

**The build failed with an Android SDK or JDK error.**
The Android modules were likely skipped. In Unity Hub, open **Installs**, click the gear
next to 6000.4.0f1, choose **Add modules**, and add **Android Build Support** with
**OpenJDK** and **Android SDK & NDK Tools**.

**How do I open the app again after taking the headset off?**
Open the App Library in the headset and change the filter to **Unknown Sources**. The
app is listed there as **Accounting-Platform**.

**Do I have to rebuild after changing a key file?**
Yes. Key files are packed into the build, so a change only reaches the headset with the
next **Build And Run**.

### Using the App

**I don't see the tool panel button.**
Raise your hand and keep it in view of the headset. The button only shows
while the headset can track that hand.

**The list beside me is empty, or I see a "No Data" notice.**
The app couldn't reach the database. Check the headset's Wi-Fi, that `api.key` is in
`Assets/StreamingAssets/`, and that no leftover `api.url` points at a server that isn't
running. Then rebuild.

**Why don't I see every company in an industry?**
A sheet holds a limited number of companies so it stays readable. Ask Ada for a
narrower slice — for example "show me only companies with revenue over a billion" —
and Ada will open a sheet of just those.

**A company or metric I expected isn't on the sheet.**
It is probably filtered out. Open **Filter**, choose **By Company** or **By Metric**,
and show it again — or ask Ada to bring it back.

**Can I sort by value with my hands?**
By hand you drag lines into the order you want. Sorting by a number — "sort companies
by return on assets" — is done by asking Ada.

**If I switch sheets, do I lose my edits?**
No. Each sheet keeps its own edits and its own undo history, and switching back
restores them.

**I put the sheet somewhere awkward. How do I get it back?**
Use **Move**, **Rotate** or **Scale** to place it again, or press **Undo** to step back.

### Ada, the Voice Assistant

**How do I talk to Ada?**
Turn Ada on with the **Assistant** tile on the tool panel and speak. Ada listens
until turned off; there is no button to hold.

**What can I ask Ada?**
Anything you'd do with the tools, and questions about the numbers. For example:
- "Open the retail industry."
- "Sort the companies by current ratio."
- "Show me just the profitability ratios."
- "Which company has the highest debt to equity?"
- "Raise the top three companies."
- "Did revenue grow from 2019 to 2020?"
- "Undo that."

**Ada doesn't hear me, or the tile says the assistant is off.**
- **Microphone denied** — re-enable it in the headset under **Settings → Privacy &
  Safety → App Permissions → Microphone**, then restart the app.
- **Couldn't stay connected** — check the headset's internet, and that `gemini.key` holds
  a valid key on a billing-enabled project (or that web search is off; see above).
- **No `gemini.key`** — create it and rebuild.

**Ada said it is "nearly full", or stopped on its own.**
A conversation can only hold so much. When it fills up Ada stops; press the
**Assistant** tile again to start a fresh conversation. The sheets are untouched.

**Ada does things too fast (or too slowly) for me to follow.**
The **Assistant** section of the tool panel sets how fast Ada's edits play out:
**0.5 m/s**, **1 m/s**, or **Instant**.

**Is Ada's answer always right?**
Ada is told to read every number from the sheet before stating it rather than from
memory, which keeps it accurate, but it is a language model and can still be wrong.
Check anything important on the sheet itself with **Profile**.

**Is what I say sent anywhere?**
Yes. While Ada is on, your voice and what's on the sheet are streamed to Google's Gemini
API. Turn Ada off when you don't want to be heard.

### Troubleshooting

**Where can I see what went wrong?**
With the headset plugged in, open **Window → Analysis → Android Logcat** in Unity.
Messages from the app start with tags such as `[IndustryCatalog]` (the database) and
`[Gemini]` (the assistant).

**Who do I ask for help?**
The project owner, who also holds `api.key` and access to the database and AWS account.
