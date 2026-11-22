# House Utility Tracker

A free Windows app that tracks your **gas cylinder** and **electricity bill**, and sends **WhatsApp reminders** before the gas runs out and before the bill is due.

- **Gas:** enter the date you received a cylinder and how many people live in the house. The app works out when it will run out.
- **Electricity:** enter the last bill date. The app reminds you before the 2-month billing period ends.
- **WhatsApp:** reminders are sent through [Green-API](https://green-api.com) (free Developer plan) or [CallMeBot](https://www.callmebot.com/blog/free-api-whatsapp-messages/) (free). A **Send test WhatsApp** button lets you check that messages arrive.
- All data stays on your own computer, in `data\db.json`.

Free to use, copy and change under the [MIT License](LICENSE).

## Download and run (no install needed)

1. Go to the [**Releases**](../../releases/latest) page and download **`HouseUtilityTracker.zip`**.
2. Right-click the zip → **Extract All**.
3. Open the extracted folder and double-click **`HouseUtility.exe`**.
   If Windows shows "Windows protected your PC", click **More info → Run anyway**. This appears because the app isn't code-signed.
4. The app opens in your browser at http://localhost:5050. Keep the black window open (minimised is fine) for reminders to be sent.

Requires Windows 10 or 11. The .NET Framework 4.x it needs is already part of Windows.

### Build it yourself instead

Download the source (**Code → Download ZIP**), extract it, double-click `build.bat`, then run `dist\HouseUtility.exe`. The build uses the C# compiler that comes with Windows.

## WhatsApp setup

### Option A: Green-API (recommended)
1. Register at https://console.green-api.com and confirm your email.
2. Click **Create an instance**, choose the free **Developer** plan, and wait about 2 minutes.
3. Open the instance, show the **QR code**, and scan it from WhatsApp → **Linked devices** → **Link a device**.
4. Copy **apiUrl**, **idInstance** and **apiTokenInstance** into the app's Settings and choose **Green-API**.
5. Enter the number that should receive reminders (e.g. `+919876543210`), click **Save settings**, then **Send test WhatsApp**.

Messages are sent *from* the WhatsApp you linked. If you send to that same number, they land in your own "Message yourself" chat, usually with no notification sound. Link a second number to get a real alert. The free plan can message up to 3 chats.

### Option B: CallMeBot
1. Open https://www.callmebot.com/blog/free-api-whatsapp-messages/ and save the number shown there as a contact.
2. Send it this WhatsApp message: `I allow callmebot to send me messages`
3. Paste the API key from its reply into Settings and choose **CallMeBot**.

CallMeBot sometimes pauses new sign-ups when its bot is full.

## How the dates are calculated

- **Gas:** `days per cylinder = cylinder kg ÷ (members × kg per person per day)`.
  The defaults are 14.2 kg and 0.1 kg per person per day, so 4 people get about 35 days. You can change these in Settings. Once you have recorded 2 or more cylinders, the app also shows your actual average.
- **Electricity:** `due date = last bill date + billing cycle` (60 days by default).
- Each reminder is sent once per cylinder or bill. The app checks every 30 minutes while it is running. A bill marked **Paid** gets no reminder.

## Project structure

```
src/Server.cs          BACKEND  - web server, REST API, JSON database, reminder scheduler, WhatsApp sender
src/public/index.html  FRONTEND - the page you see in the browser (calls the backend /api/*)
build.bat              Compiles the backend into dist/HouseUtility.exe
dist/data/db.json      Your data, created on first run (never uploaded to git)
```

## REST API

| Method | Path | Body |
|---|---|---|
| GET | `/api/data` | Returns all data and the calculated status |
| POST | `/api/settings` | `{Provider, Phone, ApiKey, GreenApiUrl, GreenIdInstance, GreenApiToken, Members, CylinderKg, KgPerPersonPerDay, GasReminderDays, ElecCycleDays, ElecReminderDays}` |
| POST | `/api/gas` | `{Date: "yyyy-MM-dd", Note}` |
| POST | `/api/electricity` | `{Date: "yyyy-MM-dd", Amount}` |
| POST | `/api/electricity/paid` | `{id}` (switches paid on or off) |
| POST | `/api/gas/delete`, `/api/electricity/delete` | `{id}` |
| POST | `/api/test-whatsapp` | Sends a test message |
| POST | `/api/check-now` | Runs the reminder check immediately |

Command-line options: `HouseUtility.exe --port=5051` (use another port) and `--no-browser` (don't open the browser automatically).
