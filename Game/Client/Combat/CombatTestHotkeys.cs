using Game.Client.Input;
using Game.Client.UI.Root;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Combat
{
    /// <summary>Optional scene-only test helper. Every key still sends the normal authoritative ability intent.</summary>
    [DisallowMultipleComponent]
    public sealed class CombatTestHotkeys : MonoBehaviour
    {
        [SerializeField] private ClientUIRoot ui;
        [SerializeField] private KeyCode slashingKey = KeyCode.F6;
        [SerializeField] private KeyCode fireKey = KeyCode.F7;
        [SerializeField] private KeyCode electricKey = KeyCode.F8;
        [SerializeField] private KeyCode poisonKey = KeyCode.F9;

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            if (ui == null) ui = FindFirstObjectByType<ClientUIRoot>();
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (ui == null || !ui.GameplaySessionActive || LocalClientInputGate.IsGameplayInputBlocked)
                return;
            if (UnityEngine.Input.GetKeyDown(slashingKey)) ui.RequestAbility("ability.test.slash20");
            if (UnityEngine.Input.GetKeyDown(fireKey)) ui.RequestAbility("ability.test.fire20");
            if (UnityEngine.Input.GetKeyDown(electricKey)) ui.RequestAbility("ability.test.electric20");
            if (UnityEngine.Input.GetKeyDown(poisonKey)) ui.RequestAbility("ability.test.poison");
#endif
        }
    }
}
