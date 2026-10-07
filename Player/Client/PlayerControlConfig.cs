using System;
using System.Collections.Generic;
using UnityEngine;

namespace Player.Client
{
    /// <summary>
    /// Local-machine-only player control bindings.
    ///
    /// This is presentation/input configuration only. It is intentionally persisted with
    /// PlayerPrefs and is never serialized to the GameServer. Gameplay authority continues
    /// to validate the resulting requests/state through the existing networking paths.
    /// </summary>
    public enum PlayerControlAction
    {
        MoveForward = 0,
        MoveBackward = 1,
        MoveLeft = 2,
        MoveRight = 3,
        Jump = 4,
        Sprint = 5,
        Interact = 6,
        ToggleCombatStance = 7,
        Crouch = 8,
        OpenInventory = 9,
        ReleaseCursor = 10,
        OpenSkills = 11,
        OpenMap = 12,
        OpenSocial = 13,
        OpenMenu = 14,
        FocusChat = 15,
        OpenEmotes = 16,
        ReloadOrRespawn = 17,
        PickupNearest = 18,
        InteractionAction2 = 19,
        InteractionAction3 = 20,
        InteractionAction4 = 21,
        DropSelected = 22,
        OpenAchievements = 23,

        HotbarSlot1 = 100,
        HotbarSlot2 = 101,
        HotbarSlot3 = 102,
        HotbarSlot4 = 103,
        HotbarSlot5 = 104,
        HotbarSlot6 = 105,
        HotbarSlot7 = 106,
        HotbarSlot8 = 107,
        HotbarSlot9 = 108,
        HotbarSlot10 = 109,
    }

    public static class PlayerControlConfig
    {
        private const string PreferencePrefix = "MMO.PlayerControls.V1.";

        private static readonly Dictionary<PlayerControlAction, KeyCode> Defaults =
            new Dictionary<PlayerControlAction, KeyCode>
            {
                { PlayerControlAction.MoveForward, KeyCode.W },
                { PlayerControlAction.MoveBackward, KeyCode.S },
                { PlayerControlAction.MoveLeft, KeyCode.A },
                { PlayerControlAction.MoveRight, KeyCode.D },
                { PlayerControlAction.Jump, KeyCode.Space },
                { PlayerControlAction.Sprint, KeyCode.LeftShift },
                { PlayerControlAction.Interact, KeyCode.E },
                { PlayerControlAction.ToggleCombatStance, KeyCode.Tab },
                { PlayerControlAction.Crouch, KeyCode.C },
                { PlayerControlAction.OpenInventory, KeyCode.I },
                { PlayerControlAction.ReleaseCursor, KeyCode.LeftAlt },
                { PlayerControlAction.OpenSkills, KeyCode.P },
                { PlayerControlAction.OpenMap, KeyCode.M },
                { PlayerControlAction.OpenSocial, KeyCode.O },
                { PlayerControlAction.OpenMenu, KeyCode.Escape },
                { PlayerControlAction.FocusChat, KeyCode.Return },
                { PlayerControlAction.OpenEmotes, KeyCode.None },
                { PlayerControlAction.ReloadOrRespawn, KeyCode.R },
                { PlayerControlAction.PickupNearest, KeyCode.F },
                { PlayerControlAction.InteractionAction2, KeyCode.R },
                { PlayerControlAction.InteractionAction3, KeyCode.F },
                { PlayerControlAction.InteractionAction4, KeyCode.Q },
                { PlayerControlAction.DropSelected, KeyCode.G },
                { PlayerControlAction.OpenAchievements, KeyCode.Y },

                { PlayerControlAction.HotbarSlot1, KeyCode.Alpha1 },
                { PlayerControlAction.HotbarSlot2, KeyCode.Alpha2 },
                { PlayerControlAction.HotbarSlot3, KeyCode.Alpha3 },
                { PlayerControlAction.HotbarSlot4, KeyCode.Alpha4 },
                { PlayerControlAction.HotbarSlot5, KeyCode.Alpha5 },
                { PlayerControlAction.HotbarSlot6, KeyCode.Alpha6 },
                { PlayerControlAction.HotbarSlot7, KeyCode.Alpha7 },
                { PlayerControlAction.HotbarSlot8, KeyCode.Alpha8 },
                { PlayerControlAction.HotbarSlot9, KeyCode.Alpha9 },
                { PlayerControlAction.HotbarSlot10, KeyCode.Alpha0 },
            };

        private static readonly Dictionary<PlayerControlAction, KeyCode> Current =
            new Dictionary<PlayerControlAction, KeyCode>();

        private static bool _loaded;

        public static event Action<PlayerControlAction, KeyCode> BindingChanged;

        public static KeyCode GetBinding(PlayerControlAction action)
        {
            EnsureLoaded();
            return Current.TryGetValue(action, out KeyCode value)
                ? value
                : GetDefaultBinding(action);
        }

        public static KeyCode GetDefaultBinding(PlayerControlAction action) =>
            Defaults.TryGetValue(action, out KeyCode value)
                ? value
                : KeyCode.None;

        public static bool GetKey(PlayerControlAction action)
        {
            KeyCode key = GetBinding(action);
            return key != KeyCode.None && UnityEngine.Input.GetKey(key);
        }

        public static bool GetKeyDown(PlayerControlAction action)
        {
            KeyCode key = GetBinding(action);
            return key != KeyCode.None && UnityEngine.Input.GetKeyDown(key);
        }

        public static bool GetKeyUp(PlayerControlAction action)
        {
            KeyCode key = GetBinding(action);
            return key != KeyCode.None && UnityEngine.Input.GetKeyUp(key);
        }

        public static void SetBinding(PlayerControlAction action, KeyCode key)
        {
            EnsureLoaded();
            Current[action] = key;
            PlayerPrefs.SetInt(PreferencePrefix + action, (int)key);
            PlayerPrefs.Save();
            BindingChanged?.Invoke(action, key);
        }

        public static void ResetBinding(PlayerControlAction action) =>
            SetBinding(action, GetDefaultBinding(action));

        public static void ResetAllDefaults()
        {
            EnsureLoaded();

            foreach (KeyValuePair<PlayerControlAction, KeyCode> pair in Defaults)
            {
                PlayerPrefs.DeleteKey(PreferencePrefix + pair.Key);
                Current[pair.Key] = pair.Value;
            }

            PlayerPrefs.Save();

            foreach (KeyValuePair<PlayerControlAction, KeyCode> pair in Defaults)
                BindingChanged?.Invoke(pair.Key, pair.Value);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;

            foreach (KeyValuePair<PlayerControlAction, KeyCode> pair in Defaults)
            {
                int value =
                    PlayerPrefs.GetInt(
                        PreferencePrefix + pair.Key,
                        (int)pair.Value);

                Current[pair.Key] =
                    Enum.IsDefined(typeof(KeyCode), value)
                        ? (KeyCode)value
                        : pair.Value;
            }
        }

        public static PlayerControlAction HotbarAction(int oneBasedSlot)
        {
            int clamped = Mathf.Clamp(oneBasedSlot, 1, 10);

            return (PlayerControlAction)(
                (int)PlayerControlAction.HotbarSlot1 +
                (clamped - 1));
        }

        public static string DisplayName(PlayerControlAction action)
        {
            switch (action)
            {
                case PlayerControlAction.MoveForward: return "Move Forward";
                case PlayerControlAction.MoveBackward: return "Move Backward";
                case PlayerControlAction.MoveLeft: return "Move Left";
                case PlayerControlAction.MoveRight: return "Move Right";
                case PlayerControlAction.Jump: return "Jump";
                case PlayerControlAction.Sprint: return "Sprint";
                case PlayerControlAction.Interact: return "Interact";
                case PlayerControlAction.ToggleCombatStance: return "Toggle Combat Stance";
                case PlayerControlAction.Crouch: return "Crouch";
                case PlayerControlAction.OpenInventory: return "Inventory / Character";
                case PlayerControlAction.ReleaseCursor: return "Release UI Cursor";
                case PlayerControlAction.OpenSkills: return "Skills";
                case PlayerControlAction.OpenMap: return "Map";
                case PlayerControlAction.OpenSocial: return "Social";
                case PlayerControlAction.OpenMenu: return "Menu";
                case PlayerControlAction.FocusChat: return "Focus Chat";
                case PlayerControlAction.OpenEmotes: return "Emote Panel";
                case PlayerControlAction.ReloadOrRespawn: return "Reload / Respawn";
                case PlayerControlAction.PickupNearest: return "Pickup Nearest";
                case PlayerControlAction.InteractionAction2: return "Interaction Action 2";
                case PlayerControlAction.InteractionAction3: return "Interaction Action 3";
                case PlayerControlAction.InteractionAction4: return "Interaction Action 4";
                case PlayerControlAction.DropSelected: return "Drop Selected Item";
                case PlayerControlAction.OpenAchievements: return "Achievements";

                default:
                    int value =
                        (int)action -
                        (int)PlayerControlAction.HotbarSlot1 +
                        1;

                    if (value >= 1 && value <= 10)
                        return $"Hotbar Slot {value}";

                    return action.ToString();
            }
        }

        public static string BindingLabel(PlayerControlAction action)
        {
            KeyCode key = GetBinding(action);

            if (key == KeyCode.None)
                return "Unbound";

            switch (key)
            {
                case KeyCode.Alpha0: return "0";
                case KeyCode.Alpha1: return "1";
                case KeyCode.Alpha2: return "2";
                case KeyCode.Alpha3: return "3";
                case KeyCode.Alpha4: return "4";
                case KeyCode.Alpha5: return "5";
                case KeyCode.Alpha6: return "6";
                case KeyCode.Alpha7: return "7";
                case KeyCode.Alpha8: return "8";
                case KeyCode.Alpha9: return "9";
                case KeyCode.LeftAlt: return "Alt";
                case KeyCode.RightAlt: return "Right Alt";
                case KeyCode.LeftShift: return "Shift";
                case KeyCode.RightShift: return "Right Shift";
                case KeyCode.Return: return "Enter";
                case KeyCode.KeypadEnter: return "Numpad Enter";
                case KeyCode.Escape: return "Esc";
                case KeyCode.Space: return "Space";
                default: return key.ToString();
            }
        }
    }
}
