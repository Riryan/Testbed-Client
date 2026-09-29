using System.Text;
using Game.Client.UI.PlayerItems;
using Player.Networking;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.UI.Standalone
{
    /// <summary>
    /// One fixed HUD-owned tooltip surface. Hover sources only change its content; they never
    /// create/move free-floating tooltip GameObjects at runtime.
    /// </summary>
    public sealed class StandaloneHudTooltipPanel : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Image icon;
        [SerializeField] private Text titleText;
        [SerializeField] private Text bodyText;

        public bool IsVisible => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
#if UNITY_SERVER
            gameObject.SetActive(false);
#else
            Hide();
#endif
        }

        public void ShowItem(PlayerItemWire item, string context = null)
        {
#if !UNITY_SERVER
            if (panelRoot == null)
                return;

            if (icon != null)
            {
                icon.sprite = ClientItemIconResolver.Resolve(item);
                icon.enabled = icon.sprite != null;
                icon.preserveAspect = true;
            }

            if (titleText != null)
                titleText.text = string.IsNullOrWhiteSpace(item.displayName) ? "Item" : item.displayName;

            if (bodyText != null)
            {
                var text = new StringBuilder(192);
                if (!string.IsNullOrWhiteSpace(context))
                    text.AppendLine(context);
                if (item.quantity > 1)
                    text.Append("Quantity: ").Append(item.quantity).AppendLine();
                if (item.maxDurability > 0)
                    text.Append("Durability: ").Append(item.durability).Append('/').Append(item.maxDurability).AppendLine();
                if (item.unitWeight > 0f)
                    text.Append("Weight: ").Append(item.unitWeight.ToString("0.##")).AppendLine();
                if (item.canUse)
                    text.AppendLine("Usable");
                if (item.allowedEquipmentSlots != null && item.allowedEquipmentSlots.Length > 0)
                    text.Append("Equip: ").Append(string.Join(", ", item.allowedEquipmentSlots)).AppendLine();
                bodyText.text = text.ToString().TrimEnd();
            }

            panelRoot.SetActive(true);
#endif
        }

        public void ShowText(string title, string body)
        {
#if !UNITY_SERVER
            if (panelRoot == null)
                return;
            if (icon != null)
                icon.enabled = false;
            if (titleText != null)
                titleText.text = title ?? string.Empty;
            if (bodyText != null)
                bodyText.text = body ?? string.Empty;
            panelRoot.SetActive(true);
#endif
        }

        public void Hide()
        {
#if !UNITY_SERVER
            if (panelRoot != null)
                panelRoot.SetActive(false);
#endif
        }

#if UNITY_EDITOR
        public void ConfigureForEditor(GameObject root, Image itemIcon, Text title, Text body)
        {
            panelRoot = root;
            icon = itemIcon;
            titleText = title;
            bodyText = body;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
