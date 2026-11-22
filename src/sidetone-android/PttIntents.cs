// SPDX-License-Identifier: AGPL-3.0-only
/**
* Sidetone for Android. PTT keys that reach Sidetone as intents, so they work with the screen off,
* the phone locked or Sidetone in the background (a key press itself only reaches the app on
* screen). Each maker does it differently:
*
*   Sonim (XP10, XP Pro, XP8): the firmware sends its PTT key to the app that declares a receiver
*     for com.sonim.intent.action.PTT_KEY_DOWN / PTT_KEY_UP (or Kodiak's com.kodiak.intent.action.
*     PTT_BUTTON, or the MCPTT key action, which carry the key event for the direction). Declaring
*     them (PttKeyReceiver, below) is also what lists Sidetone as a choice for the PTT key in
*     Sonim's Programmable Keys settings.
*   Zebra (TC5x and others): the Remote KeyEvent Service broadcasts the PTT and side keys as
*     com.symbol.button.L1, L2, R1, R2 with the key event, to apps holding
*     zebra.permission.KEY_EVENT_SERVICE. These go through only when the key is the learned PTT key
*     (two of them are the scan triggers).
*   Anything whose key programmer can send an intent: org.sidetone.app.PTT_DOWN / PTT_UP.
*
* Any app on the phone can send these, so they're taken only when Settings' "PTT when locked" is
* allowed (AppSettings.PttFromBroadcasts); otherwise one would key the transmitter and put the
* room's audio on the air.
*
* Every intent that arrives is written to the debug log ("ptt:" lines), so a device that sends
* something else can be found out from a tester's saved logs.
*/

using Android.App;
using Android.Content;
using Android.Views;

namespace Sidetone.Droid
{
    public static class PttIntents
    {
        public const string SonimDown = "com.sonim.intent.action.PTT_KEY_DOWN", SonimUp = "com.sonim.intent.action.PTT_KEY_UP";
        public const string Kodiak = "com.kodiak.intent.action.PTT_BUTTON";
        public const string Mcptt = "com.mcx.intent.action.CRITICAL_COMMUNICATION_CONTROL_KEY";
        public const string OwnDown = "org.sidetone.app.PTT_DOWN", OwnUp = "org.sidetone.app.PTT_UP";
        public static readonly string[] Zebra = { "com.symbol.button.L1", "com.symbol.button.L2", "com.symbol.button.R1", "com.symbol.button.R2" };

        /// <summary>Every action handled.</summary>
        public static string[] Actions => Zebra.Concat(new[] { SonimDown, SonimUp, Kodiak, Mcptt, OwnDown, OwnUp }).ToArray();

        /// <summary>A PTT intent, from the connection service's receiver or the declared one. Any thread.</summary>
        public static void Handle(Intent intent)
        {
            string action = intent?.Action;
            if (action == null)
                return;
            AndroidPlatform p = AndroidPlatform.Instance;
            if (!p.PttFromBroadcasts)
            {
                DebugLog.Write("ptt", $"{action}: ignored (Settings, PTT when locked, is off)");
                return;
            }
#pragma warning disable CA1422, CS0618 // the typed overload is Android 13+
            KeyEvent e = intent.GetParcelableExtra(Intent.ExtraKeyEvent) as KeyEvent;
#pragma warning restore CA1422, CS0618
            string key = e != null ? $" {KeyEvent.KeyCodeToString(e.KeyCode)}" : string.Empty;

            // the dedicated PTT intents: down and up are in the action, or in the key event
            bool? down = action switch
            {
                SonimDown or OwnDown => true,
                SonimUp or OwnUp => false,
                Kodiak or Mcptt => e?.Action == KeyEventActions.Down ? true : e?.Action == KeyEventActions.Up ? false : null,
                _ => null
            };
            if (down != null)
            {
                if (e == null || e.RepeatCount == 0)
                    DebugLog.Write("ptt", $"{action}{key} {(down.Value ? "down" : "up")}: {(p.PttKey(down.Value, e?.RepeatCount ?? 0) ? "keyed" : "no console to key")}");
                else
                    p.PttKey(down.Value, e.RepeatCount);
                return;
            }

            // Zebra's key broadcasts: only the learned PTT key (the others are scan triggers)
            if (e == null || (e.Action != KeyEventActions.Down && e.Action != KeyEventActions.Up))
            {
                DebugLog.Write("ptt", $"{action}: no key event in it");
                return;
            }
            bool learned = p.PttKeyCode == (int)e.KeyCode;
            if (e.RepeatCount == 0)
                DebugLog.Write("ptt", $"{action}:{key} {(e.Action == KeyEventActions.Down ? "down" : "up")}{(learned ? string.Empty : " (not the learned PTT key)")}");
            if (learned)
                p.PttKey(e.Action == KeyEventActions.Down, e.RepeatCount);
        }
    } // public static class PttIntents

    /// <summary>
    /// The PTT receiver declared in the manifest, which makers' firmware looks for (Sonim lists apps that have one
    /// for its PTT key, and sends the key to the chosen one). It works whenever Sidetone's process is running; with
    /// no console open, the key is noted in the debug log and nothing is keyed.
    /// </summary>
    [BroadcastReceiver(Name = "org.sidetone.app.PttKeyReceiver", Exported = true, Enabled = true)]
    [IntentFilter(new[] { PttIntents.SonimDown, PttIntents.SonimUp, PttIntents.Kodiak, PttIntents.Mcptt, PttIntents.OwnDown, PttIntents.OwnUp }, Priority = 999)]
    public sealed class PttKeyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            AndroidPlatform.Setup(context);
            PttIntents.Handle(intent);
        }
    } // public sealed class PttKeyReceiver
}
