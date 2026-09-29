#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Game.Client.UI.Root;

namespace Game.Client.Editor
{
    public sealed class CombatV1TestWindow : EditorWindow
    {
        private static readonly (string Label, string Id)[] TargetAbilities =
        {
            ("Slashing 20", "ability.test.slash20"),
            ("Fire 20", "ability.test.fire20"),
            ("Electric 20 + Projectile", "ability.test.electric20"),
            ("Poison DOT", "ability.test.poison"),
            ("Poison 20", "ability.test.poison20"),
            ("Poison Resistance 50% (8s)", "ability.test.poison_resistant"),
            ("Poison Immunity (8s)", "ability.test.poison_immune"),
            ("Damage Taken +50% (8s)", "ability.test.damage_taken_debuff"),
            ("Reset Selected Target Health", "ability.test.reset_target"),
        };

        private static readonly (string Label, string Id)[] SelfAbilities =
        {
            ("Hurt Self -40", "ability.test.hurt_self"),
            ("Heal Self +25", "ability.test.heal_self"),
            ("Regen Self", "ability.test.regen_self"),
            ("Damage Buff Self +50% (8s)", "ability.test.damage_buff_self"),
            ("Stun Self 3s", "ability.test.stun_self"),
            ("Root Self 5s", "ability.test.root_self"),
        };

        [MenuItem("MMO Tools/Diagnostics/Combat V1 Test Panel")]
        private static void Open() => GetWindow<CombatV1TestWindow>("Combat V1 Test");

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Place a CombatTestDummy on a normal WorldInteractable and bake the server world. Aim the combat camera/crosshair at it for ordinary attacks; " +
                "selection is only needed for explicitly targeted ability tests. These buttons use normal client intent paths and do not bypass GameServer authority.",
                MessageType.Info);

            ClientUIRoot ui = Object.FindObjectOfType<ClientUIRoot>();
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || ui == null))
            {
                if (GUILayout.Button("Cycle Remote Player Target")) ui.CyclePlayerTarget();
                if (GUILayout.Button("Light Combat Action")) ui.RequestCombatAction(Game.Shared.Abilities.BasicAttackInputKind.Light);
                if (GUILayout.Button("Heavy Unarmed Action")) ui.RequestCombatAction(Game.Shared.Abilities.BasicAttackInputKind.Heavy);
                if (GUILayout.Button("Reload / Respawn")) ui.RequestReloadOrRespawn();

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Targeted Tests", EditorStyles.boldLabel);
                for (int i = 0; i < TargetAbilities.Length; ++i)
                    if (GUILayout.Button(TargetAbilities[i].Label))
                        ui.RequestAbility(TargetAbilities[i].Id);

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Self Tests", EditorStyles.boldLabel);
                for (int i = 0; i < SelfAbilities.Length; ++i)
                    if (GUILayout.Button(SelfAbilities[i].Label))
                        ui.RequestAbility(SelfAbilities[i].Id);
            }

            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode to use the panel.", MessageType.None);
            else if (ui == null)
                EditorGUILayout.HelpBox("ClientUIRoot was not found in the active gameplay scene.", MessageType.Warning);
        }
    }
}
#endif
