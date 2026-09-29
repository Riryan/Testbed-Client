using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Scene authoring for an authoritative standalone GameServer combat fixture.
    /// The scene object is presentation/selection only; combat state lives on the server.
    /// </summary>
    [AddComponentMenu("MMO/Combat Test Dummy")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldObject))]
    [RequireComponent(typeof(WorldInteractable))]
    public sealed class CombatTestDummy : MonoBehaviour
    {
        [Min(1)] public int healthMaximum = 500;
        [Min(0f)] public float armor;
        [Tooltip("Restore Health after lethal damage. Disable this when validating defeat/death behavior.")]
        public bool resetOnDefeat;
        public string fireDamageTypeDefinitionId = "damage.fire";
        public string electricDamageTypeDefinitionId = "damage.electric";
        public string poisonDamageTypeDefinitionId = "damage.poison";

        private void Reset()
        {
            WorldInteractable interactable = GetComponent<WorldInteractable>();
            if (interactable == null)
                return;

            interactable.definitionId = "combat_test_dummy";
            interactable.label = "Combat Test Dummy";
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.dynamicBlocker = false;
        }

        private void OnValidate()
        {
            healthMaximum = Mathf.Max(1, healthMaximum);
            armor = Mathf.Max(0f, armor);
            fireDamageTypeDefinitionId = Normalize(fireDamageTypeDefinitionId, "damage.fire");
            electricDamageTypeDefinitionId = Normalize(electricDamageTypeDefinitionId, "damage.electric");
            poisonDamageTypeDefinitionId = Normalize(poisonDamageTypeDefinitionId, "damage.poison");
        }

        private static string Normalize(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
