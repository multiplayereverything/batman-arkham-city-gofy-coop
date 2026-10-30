#nullable enable
// Keyboard input for the chat box and the host menu. The console interaction sees every key and character before
// the player controller; returning true keeps the key from the game.
using BmSDK;
using BmSDK.Engine;

namespace BatCoop.Game;

static class InputHooks
{
    public static long Keys, Chars, Swallowed;

    [Redirect(typeof(_Console), nameof(_Console.InputKey))]
    static bool InputKey(_Console self, int ControllerId, FName Key, GameObject.EInputEvent Event, float AmountDepressed, bool bGamepad)
    {
        Keys++;
        var ui = BatCoopScript.Instance?.Ui;
        if (ui != null && Event is GameObject.EInputEvent.IE_Pressed or GameObject.EInputEvent.IE_Released or GameObject.EInputEvent.IE_Repeat)
        {
            bool pressed = Event != GameObject.EInputEvent.IE_Released;
            try
            {
                if (ui.OnKey(Key.ToString() ?? "", pressed)) { Swallowed++; return true; }
            }
            catch { }
        }
        else if (ui is { Capturing: true }) return true; // mouse and stick axes too, while typing
        return self.InputKey(ControllerId, Key, Event, AmountDepressed, bGamepad);
    }

    [Redirect(typeof(_Console), nameof(_Console.InputChar))]
    static bool InputChar(_Console self, int ControllerId, FString Unicode)
    {
        Chars++;
        var ui = BatCoopScript.Instance?.Ui;
        if (ui != null)
        {
            var text = Unicode.ToString() ?? "";
            try
            {
                if (ui.TakeOpeningChar(text)) return true;
                if (ui.OnChar(text)) return true;
            }
            catch { }
        }
        return self.InputChar(ControllerId, Unicode);
    }
}
