using System;
using System.Collections.Generic;
using Player.Networking;
using UnityEngine;

namespace Game.Client.UI.PlayerItems
{
    [CreateAssetMenu(menuName = "MMO/Client Item Presentation Catalog", fileName = "ClientItemPresentationCatalog")]
    public sealed class ClientItemPresentationCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public ushort presentationId;
            public Sprite icon;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();

        public bool TryGetIcon(ushort presentationId, out Sprite icon)
        {
            Entry[] values = entries ?? Array.Empty<Entry>();
            for (int i = 0; i < values.Length; ++i)
            {
                Entry entry = values[i];
                if (entry != null && entry.presentationId == presentationId && entry.icon != null)
                {
                    icon = entry.icon;
                    return true;
                }
            }
            icon = null;
            return false;
        }
    }

    /// <summary>
    /// Resolves item presentation entirely from client-known content. The wire already carries
    /// itemDataId and the gameplay settings cache already maps that to presentationId, so icons
    /// never require a separate item-icon message or lookup request.
    /// </summary>
    public static class ClientItemIconResolver
    {
        private const string ResourcePath = "MMO/Items/ClientItemPresentationCatalog";
        private static ClientItemPresentationCatalog _catalog;
        private static readonly Dictionary<ItemIconKind, Sprite> Fallbacks = new Dictionary<ItemIconKind, Sprite>();

        private enum ItemIconKind : byte { Generic, Material, Tool, Weapon, Consumable, Armor, Ammo }

        public static Sprite Resolve(PlayerItemWire item)
        {
            GameplayItemReferenceWire definition = default;
            bool hasDefinition = item.itemDataId != 0 && PlayerGameplaySettingsRuntime.TryGetItem(item.itemDataId, out definition);
            ushort presentationId = hasDefinition ? definition.presentationId : (ushort)0;
            if (presentationId != 0)
            {
                if (_catalog == null)
                    _catalog = Resources.Load<ClientItemPresentationCatalog>(ResourcePath);
                if (_catalog != null && _catalog.TryGetIcon(presentationId, out Sprite authored))
                    return authored;
            }

            string semantic = hasDefinition && !string.IsNullOrWhiteSpace(definition.definitionId)
                ? definition.definitionId
                : item.definitionId;
            return ResolveFallback(semantic, item.canUse, item.allowedEquipmentSlots != null && item.allowedEquipmentSlots.Length > 0);
        }

        private static Sprite ResolveFallback(string semantic, bool canUse, bool equippable)
        {
            string value = (semantic ?? string.Empty).ToLowerInvariant();
            ItemIconKind kind = value.Contains("ammo") || value.Contains("bullet") ? ItemIconKind.Ammo
                : value.Contains("weapon") || value.Contains("gun") || value.Contains("rifle") || value.Contains("pistol") || value.Contains("blade") ? ItemIconKind.Weapon
                : value.Contains("armor") || value.Contains("shirt") || value.Contains("pants") || value.Contains("helmet") || value.Contains("clothing") ? ItemIconKind.Armor
                : value.Contains("tool") || value.Contains("kit") ? ItemIconKind.Tool
                : value.Contains("material") || value.Contains("scrap") || value.Contains("part") || value.Contains("ore") ? ItemIconKind.Material
                : canUse ? ItemIconKind.Consumable
                : equippable ? ItemIconKind.Armor
                : ItemIconKind.Generic;

            if (!Fallbacks.TryGetValue(kind, out Sprite sprite) || sprite == null)
            {
                sprite = BuildFallback(kind);
                Fallbacks[kind] = sprite;
            }
            return sprite;
        }

        private static Sprite BuildFallback(ItemIconKind kind)
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ClientItemIcon_" + kind,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 ink = new Color32(235, 235, 235, 235);
            Color32[] pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = clear;

            void Set(int x, int y)
            {
                if ((uint)x < size && (uint)y < size) pixels[y * size + x] = ink;
            }
            void Rect(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y <= y1; ++y) for (int x = x0; x <= x1; ++x) Set(x, y);
            }
            void Line(int x0, int y0, int x1, int y1, int thickness = 2)
            {
                int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = dx + dy;
                while (true)
                {
                    for (int oy = -thickness / 2; oy <= thickness / 2; ++oy)
                        for (int ox = -thickness / 2; ox <= thickness / 2; ++ox) Set(x0 + ox, y0 + oy);
                    if (x0 == x1 && y0 == y1) break;
                    int e2 = 2 * err;
                    if (e2 >= dy) { err += dy; x0 += sx; }
                    if (e2 <= dx) { err += dx; y0 += sy; }
                }
            }

            switch (kind)
            {
                case ItemIconKind.Material:
                    Rect(7, 7, 24, 11); Rect(5, 13, 22, 17); Rect(9, 19, 26, 23); break;
                case ItemIconKind.Tool:
                    Line(8, 7, 24, 23, 3); Rect(5, 5, 11, 10); Rect(21, 20, 27, 26); break;
                case ItemIconKind.Weapon:
                    Line(7, 7, 24, 24, 3); Line(7, 23, 13, 17, 2); Line(18, 26, 26, 18, 2); break;
                case ItemIconKind.Consumable:
                    Rect(11, 7, 20, 10); Rect(9, 11, 22, 24); Rect(12, 4, 19, 7); break;
                case ItemIconKind.Armor:
                    for (int y = 6; y <= 25; ++y)
                    {
                        int inset = Math.Abs(16 - y) / 3;
                        for (int x = 7 + inset; x <= 25 - inset; ++x) Set(x, y);
                    }
                    break;
                case ItemIconKind.Ammo:
                    Rect(8, 8, 12, 24); Rect(15, 6, 19, 24); Rect(22, 10, 26, 24); break;
                default:
                    for (int y = 6; y <= 25; ++y)
                    {
                        int span = y <= 16 ? y - 5 : 26 - y;
                        for (int x = 16 - span; x <= 16 + span; ++x) Set(x, y);
                    }
                    break;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public static void ResetForReload()
        {
            _catalog = null;
            Fallbacks.Clear();
        }
    }
}
