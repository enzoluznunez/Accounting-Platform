# FAQ

Answers about what the app is, setting it up, using it, and Ada, the voice assistant.

- [General](#general)
- [Setup and Building](#setup-and-building)
- [Using the App](#using-the-app)
- [Ada, the Voice Assistant](#ada-the-voice-assistant)
- [Troubleshooting](#troubleshooting)

## General

### What is this app for?

Exploring and comparing the financial ratios of public companies in 3D. Instead of
scrolling a spreadsheet, you stand in front of it, hide what you don't need, reorder
what's left, and raise the rows you care about — by hand or by asking Ada.

### Where does the data come from?

A financial export in a private S3 bucket, cleaned to 1,249 companies across ten
industries for fiscal 2019 and 2020, then loaded into MongoDB Atlas by
`Backend/rebuild.py`. The app reads it live through the API, so it always shows the
database as it stands now. See [`Backend/README.md`](../Backend/README.md) to change or rebuild it.

### Can I load my own spreadsheet?

Not from inside the app. The sheets come from the financial database; to change what
they hold, change the data in the pipeline.

### Does it cost anything?

The app and the financial database are free to you. Voice uses the Google Gemini API,
which bills your Google account for what Ada uses.

### Why does the Gemini key need a paid tier?

Ada can look things up on Google (news, filings, market figures you ask about), and
Google only allows that search on billing-enabled keys; a free-tier key is rejected
when the session starts. To use a free-tier key, open the scene, find the
`GeminiClient` component, and untick **Enable Web Search**. Ada then works without
looking anything up.

### Can I try it without a headset?

Not meaningfully — hand input and passthrough are the interface.

### Do I need controllers?

No. Everything is done with hand tracking. If the headset doesn't see your hands, make
sure hand tracking is turned on under the headset's **Settings → Movement Tracking**.

## Setup and Building

### I don't have `api.key`. Can I still use the app?

Not with the deployed database — it refuses requests without the key. Ask the project
owner, or run the API on your own computer (see [`Backend/README.md`](../Backend/README.md)), which doesn't ask
for one.

### Unity says my version doesn't match.

Install exactly **6000.4.0f1** from Unity Hub's **Installs → Install Editor → Archive**
and open the project with it.

### My headset doesn't appear in the Build Profiles device list.

Check that Developer Mode is on, the cable carries data (not only charge), and you
accepted **Allow USB debugging** inside the headset. Unplug, replug, and look in the
headset for the prompt again. Pressing **Refresh** next to the device list helps.

### The build failed with an Android SDK or JDK error.

The Android modules were likely skipped. In Unity Hub, open **Installs**, click the gear
next to 6000.4.0f1, choose **Add modules**, and add **Android Build Support** with
**OpenJDK** and **Android SDK & NDK Tools**.

### How do I open the app again after taking the headset off?

Open the App Library in the headset and change the filter to **Unknown Sources**. The
app is listed there as **Accounting-Platform**.

### Do I have to rebuild after changing a key file?

Yes. Key files are packed into the build, so a change only reaches the headset with the
next **Build And Run**.

## Using the App

### I don't see the tool panel button.

Raise your hand and keep it in view of the headset. The button only shows
while the headset can track that hand.

### The list beside me is empty, or I see a "No Data" notice.

The app couldn't reach the database. Check the headset's Wi-Fi, that `api.key` is in
`Assets/StreamingAssets/`, and that no leftover `api.url` points at a server that isn't
running. Then rebuild.

### Why don't I see every company in an industry?

A sheet holds a limited number of companies so it stays readable. Ask Ada for a
narrower slice — for example "show me only companies with revenue over a billion" —
and Ada will open a sheet of just those.

### A company or metric I expected isn't on the sheet.

It is probably filtered out. Open **Filter**, choose **By Company** or **By Metric**,
and show it again — or ask Ada to bring it back.

### Can I sort by value with my hands?

By hand you drag lines into the order you want. Sorting by a number — "sort companies
by return on assets" — is done by asking Ada.

### If I switch sheets, do I lose my edits?

No. Each sheet keeps its own edits and its own undo history, and switching back
restores them.

### I put the sheet somewhere awkward. How do I get it back?

Use **Move**, **Rotate** or **Scale** to place it again, or press **Undo** to step back.

## Ada, the Voice Assistant

### How do I talk to Ada?

Turn Ada on with the **Assistant** tile on the tool panel and speak. Ada listens
until turned off; there is no button to hold.

### What can I ask Ada?

Anything you'd do with the tools, and questions about the numbers. For example:

- "Open the retail industry."
- "Sort the companies by current ratio."
- "Show me just the profitability ratios."
- "Which company has the highest debt to equity?"
- "Raise the top three companies."
- "Did revenue grow from 2019 to 2020?"
- "Undo that."

### Ada doesn't hear me, or the tile says the assistant is off.

- **Microphone denied** — re-enable it in the headset under **Settings → Privacy &
  Safety → App Permissions → Microphone**, then restart the app.
- **Couldn't stay connected** — check the headset's internet, and that `gemini.key` holds
  a valid key on a billing-enabled project (or that web search is off; see
  [Why does the Gemini key need a paid tier?](#why-does-the-gemini-key-need-a-paid-tier)).
- **No `gemini.key`** — create it and rebuild.

### Ada said it is "nearly full", or stopped on its own.

A conversation can only hold so much. When it fills up Ada stops; press the
**Assistant** tile again to start a fresh conversation. The sheets are untouched.

### Ada does things too fast (or too slowly) for me to follow.

The **Assistant** section of the tool panel sets how fast Ada's edits play out:
**0.5 m/s**, **1 m/s**, or **Instant**.

### Is Ada's answer always right?

Ada is told to read every number from the sheet before stating it rather than from
memory, which keeps it accurate, but it is a language model and can still be wrong.
Check anything important on the sheet itself with **Profile**.

### Is what I say sent anywhere?

Yes. While Ada is on, your voice and what's on the sheet are streamed to Google's Gemini
API. Turn Ada off when you don't want to be heard.

## Troubleshooting

### Where can I see what went wrong?

With the headset plugged in, open **Window → Analysis → Android Logcat** in Unity.
Messages from the app start with tags such as `[IndustryCatalog]` (the database) and
`[Gemini]` (the assistant).

### Who do I ask for help?

The project owner, who also holds `api.key` and access to the database and AWS account.
