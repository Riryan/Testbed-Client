#if UNITY_EDITOR
using System;
using Game.Client.UI.Root;
using Game.Client.UI.Social;
using ClientSocialEconomyRowView = Game.Client.UI.SocialEconomy.ClientSocialEconomyRowView;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Client.Editor
{
    /// <summary>
    /// One-time prefab authoring for Social/Economy test bindings. Runtime code only binds
    /// these authored controls; it does not build the UI hierarchy.
    /// </summary>
    public static class ClientSocialEconomyUIAuthoring
    {
        private const string TargetPath = "Assets/Game/Client/UI/Root/Prefabs/ClientUIRoot.prefab";

        [MenuItem("MMO Tools/UI/Social Economy/Install or Repair + Validate")]
        public static void InstallOrRepairAndValidate()
        {
            ClientUIFullScaffoldBuilder.BuildOrRepairFullMasterUi();
            GameObject root = PrefabUtility.LoadPrefabContents(TargetPath);
            try
            {
                InstallOrRepair(root);
                PrefabUtility.SaveAsPrefabAsset(root, TargetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Validate();
        }

        [MenuItem("MMO Tools/UI/Social Economy/Validate")]
        public static void Validate()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath);
            if (prefab == null) throw new InvalidOperationException($"ClientUIRoot prefab not found: {TargetPath}");
            ClientSocialEconomyUI ui = prefab.GetComponent<ClientSocialEconomyUI>();
            if (ui == null || !ui.HasAuthoredBindings)
                throw new InvalidOperationException("ClientSocialEconomyUI is missing or has incomplete authored bindings.");
            Debug.Log("[ClientUI] Social Economy UI bindings validated.", prefab);
            Selection.activeObject = prefab;
        }

        public static void InstallOrRepairForRoot(GameObject root)
        {
            InstallOrRepair(root);
        }

        private static void InstallOrRepair(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            RemoveLegacyDuplicateBinder(root);
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ??
                        Resources.GetBuiltinResource<Font>("Arial.ttf");

            GameObject friendsWindow = FindPanel(root, ClientUIPanelId.FriendsWindow);
            GameObject socialWindow = FindPanel(root, ClientUIPanelId.SocialWindow);
            GameObject invitePopup = FindPanel(root, ClientUIPanelId.PopupFriendInvite);
            GameObject partyWindow = FindPanel(root, ClientUIPanelId.PartyWindow);
            GameObject partyHud = FindPanel(root, ClientUIPanelId.HudPartyFrames);
            GameObject partyInvitePopup = FindPanel(root, ClientUIPanelId.PopupPartyInvite);
            GameObject mainMenu = FindPanel(root, ClientUIPanelId.MainMenuWindow);
            if (friendsWindow == null || socialWindow == null || invitePopup == null ||
                partyWindow == null || partyHud == null || partyInvitePopup == null || mainMenu == null)
                throw new InvalidOperationException("Friends, Party, Social, invite popup, HUD, or MainMenu UI is missing from ClientUIRoot.");

            Transform mainMenuBody = mainMenu.transform.Find("Body") ?? mainMenu.transform;
            Button openFriends = EnsureMainMenuFriendsButton(mainMenu, mainMenuBody, font);

            Transform friendsBody = friendsWindow.transform.Find("Body") ?? friendsWindow.transform;
            Transform planned = friendsBody.Find("PlannedSurfaceOutline");
            if (planned != null) planned.gameObject.SetActive(false);

            RectTransform view = EnsureSingleRect(friendsBody, "SocialEconomyFriendsView");
            Stretch(view);

            Text summary = EnsureSingleText(view, "Summary", font, 14, TextAnchor.MiddleLeft);
            summary.horizontalOverflow = HorizontalWrapMode.Wrap;
            summary.verticalOverflow = VerticalWrapMode.Truncate;
            RectTransform summaryRect = summary.rectTransform;
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0.5f, 1f);
            summaryRect.sizeDelta = new Vector2(0f, 32f);
            summaryRect.anchoredPosition = new Vector2(0f, -4f);

            RectTransform list = EnsureSingleRect(view, "FriendsList");
            list.anchorMin = new Vector2(0f, 0f);
            list.anchorMax = new Vector2(1f, 1f);
            list.offsetMin = Vector2.zero;
            list.offsetMax = new Vector2(0f, -42f);
            VerticalLayoutGroup layout = list.GetComponent<VerticalLayoutGroup>() ?? list.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            ClientSocialEconomyFriendRow row = EnsureFriendRow(list, font);

            Transform socialTab = socialWindow.transform.Find("FriendsTab");
            if (socialTab == null)
                throw new InvalidOperationException("SocialWindow is missing FriendsTab.");
            Transform socialBinding = socialTab.Find("SocialEconomyBinding");
            if (socialBinding == null)
                throw new InvalidOperationException("SocialWindow/FriendsTab is missing its authored SocialEconomyBinding.");
            Text socialStatus = socialBinding.Find("StatusText")?.GetComponent<Text>();
            RectTransform socialList = socialBinding.Find("List/Viewport/Content") as RectTransform;
            ClientSocialEconomyRowView socialRow = socialBinding.Find("List/Viewport/Content/RowTemplate")?.GetComponent<ClientSocialEconomyRowView>();
            if (socialStatus == null || socialList == null || socialRow == null)
                throw new InvalidOperationException("SocialWindow/FriendsTab authored Friends bindings are incomplete.");
            socialRow.CaptureAuthoredReferencesForEditor();
            socialRow.gameObject.SetActive(false);

            Transform inviteBody = invitePopup.transform.Find("Body") ?? invitePopup.transform;
            Transform invitePlanned = inviteBody.Find("PlannedSurfaceOutline");
            if (invitePlanned != null) invitePlanned.gameObject.SetActive(false);
            Text inviteText = EnsureText(inviteBody, "InviteMessage", font, 18, TextAnchor.MiddleCenter);
            RectTransform inviteTextRect = inviteText.rectTransform;
            inviteTextRect.anchorMin = new Vector2(0f, 0.35f);
            inviteTextRect.anchorMax = new Vector2(1f, 0.9f);
            inviteTextRect.offsetMin = new Vector2(20f, 0f);
            inviteTextRect.offsetMax = new Vector2(-20f, 0f);

            Button accept = EnsureButton(inviteBody, "AcceptButton", "ACCEPT", font);
            Button decline = EnsureButton(inviteBody, "DeclineButton", "DECLINE", font);
            PlaceBottomButton(accept.GetComponent<RectTransform>(), -90f);
            PlaceBottomButton(decline.GetComponent<RectTransform>(), 90f);

            // Party reuses the authored PartyWindow/HUD/popup scaffolding instead of
            // constructing a parallel runtime hierarchy.
            Transform partyBody = partyWindow.transform.Find("Body") ?? partyWindow.transform;
            Transform partyPlanned = partyBody.Find("PlannedSurfaceOutline");
            if (partyPlanned != null) partyPlanned.gameObject.SetActive(false);

            RectTransform partyView = EnsureSingleRect(partyBody, "PartyStateView");
            Stretch(partyView);

            Text partySummary = EnsureSingleText(partyView, "Summary", font, 14, TextAnchor.MiddleLeft);
            partySummary.horizontalOverflow = HorizontalWrapMode.Wrap;
            partySummary.verticalOverflow = VerticalWrapMode.Truncate;
            RectTransform partySummaryRect = partySummary.rectTransform;
            partySummaryRect.anchorMin = new Vector2(0f, 1f);
            partySummaryRect.anchorMax = new Vector2(1f, 1f);
            partySummaryRect.pivot = new Vector2(0.5f, 1f);
            partySummaryRect.sizeDelta = new Vector2(0f, 32f);
            partySummaryRect.anchoredPosition = new Vector2(0f, -4f);

            RectTransform partyList = EnsureSingleRect(partyView, "PartyList");
            partyList.anchorMin = new Vector2(0f, 0f);
            partyList.anchorMax = new Vector2(1f, 1f);
            partyList.offsetMin = new Vector2(0f, 66f);
            partyList.offsetMax = new Vector2(0f, -42f);
            VerticalLayoutGroup partyLayout = partyList.GetComponent<VerticalLayoutGroup>() ?? partyList.gameObject.AddComponent<VerticalLayoutGroup>();
            partyLayout.spacing = 4f;
            partyLayout.childControlHeight = false;
            partyLayout.childControlWidth = true;
            partyLayout.childForceExpandHeight = false;
            partyLayout.childForceExpandWidth = true;
            ClientSocialEconomyRowView partyRow = EnsurePartyRow(partyList, font);

            Button partyLeave = EnsureButton(partyView, "LeaveButton", "LEAVE PARTY", font);
            Button partyDisband = EnsureButton(partyView, "DisbandButton", "DISBAND", font);
            PlaceBottomButton(partyLeave.GetComponent<RectTransform>(), -90f);
            PlaceBottomButton(partyDisband.GetComponent<RectTransform>(), 90f);

            RectTransform partyHudRect = partyHud.GetComponent<RectTransform>();
            if (partyHudRect != null)
            {
                partyHudRect.anchorMin = new Vector2(0f, 1f);
                partyHudRect.anchorMax = new Vector2(0f, 1f);
                partyHudRect.pivot = new Vector2(0f, 1f);
                partyHudRect.sizeDelta = new Vector2(280f, 178f);
                partyHudRect.anchoredPosition = new Vector2(18f, -118f);
            }

            Transform oldPartyHudLabel = partyHud.transform.Find("Label");
            if (oldPartyHudLabel != null) oldPartyHudLabel.gameObject.SetActive(false);
            Text partyHudText = EnsureSingleText(partyHud.transform, "PartyStateText", font, 13, TextAnchor.MiddleLeft);
            RectTransform partyHudTextRect = partyHudText.rectTransform;
            partyHudTextRect.anchorMin = new Vector2(0f, 0f);
            partyHudTextRect.anchorMax = new Vector2(0.76f, 1f);
            partyHudTextRect.offsetMin = new Vector2(8f, 4f);
            partyHudTextRect.offsetMax = new Vector2(-4f, -4f);
            partyHudText.horizontalOverflow = HorizontalWrapMode.Wrap;
            partyHudText.verticalOverflow = VerticalWrapMode.Overflow;
            Button openParty = EnsureButton(partyHud.transform, "OpenPartyButton", "OPEN", font);
            RectTransform openPartyRect = openParty.GetComponent<RectTransform>();
            openPartyRect.anchorMin = new Vector2(0.78f, 0.18f);
            openPartyRect.anchorMax = new Vector2(0.98f, 0.82f);
            openPartyRect.offsetMin = Vector2.zero;
            openPartyRect.offsetMax = Vector2.zero;
            partyHud.SetActive(false);

            Transform partyInviteBody = partyInvitePopup.transform.Find("Body") ?? partyInvitePopup.transform;
            Transform partyInvitePlanned = partyInviteBody.Find("PlannedSurfaceOutline");
            if (partyInvitePlanned != null) partyInvitePlanned.gameObject.SetActive(false);
            Text partyInviteText = EnsureText(partyInviteBody, "InviteMessage", font, 18, TextAnchor.MiddleCenter);
            RectTransform partyInviteTextRect = partyInviteText.rectTransform;
            partyInviteTextRect.anchorMin = new Vector2(0f, 0.35f);
            partyInviteTextRect.anchorMax = new Vector2(1f, 0.9f);
            partyInviteTextRect.offsetMin = new Vector2(20f, 0f);
            partyInviteTextRect.offsetMax = new Vector2(-20f, 0f);
            Button partyAccept = EnsureButton(partyInviteBody, "AcceptButton", "ACCEPT", font);
            Button partyDecline = EnsureButton(partyInviteBody, "DeclineButton", "DECLINE", font);
            PlaceBottomButton(partyAccept.GetComponent<RectTransform>(), -90f);
            PlaceBottomButton(partyDecline.GetComponent<RectTransform>(), 90f);

            ClientSocialEconomyUI ui = root.GetComponent<ClientSocialEconomyUI>() ?? root.AddComponent<ClientSocialEconomyUI>();
            ui.ConfigureForEditor(
                openFriends, summary, list, row, socialTab.gameObject, socialStatus, socialList, socialRow, inviteText, accept, decline,
                partyHud, partyHudText, openParty, partySummary, partyList, partyRow, partyLeave, partyDisband,
                partyInviteText, partyAccept, partyDecline);
            EditorUtility.SetDirty(ui);
        }

        private static void RemoveLegacyDuplicateBinder(GameObject root)
        {
            // The old Game.Client.UI.SocialEconomy binder duplicated Friends ownership and
            // fought the canonical Social binder. Remove it during every authoring repair.
            // This is client-only prefab cleanup; it does not alter any social wire contract.
            MonoBehaviour[] behaviours = root.GetComponents<MonoBehaviour>();
            for (int i = behaviours.Length - 1; i >= 0; --i)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || behaviour.GetType().FullName != "Game.Client.UI.SocialEconomy.ClientSocialEconomyUI")
                    continue;
                UnityEngine.Object.DestroyImmediate(behaviour);
            }
        }

        private static Button EnsureMainMenuFriendsButton(GameObject mainMenu, Transform body, Font font)
        {
            if (mainMenu == null) throw new ArgumentNullException(nameof(mainMenu));
            if (body == null) throw new ArgumentNullException(nameof(body));

            Transform planned = body.Find("PlannedSurfaceOutline");
            if (planned != null) planned.gameObject.SetActive(false);

            RectTransform menuRect = mainMenu.GetComponent<RectTransform>();
            if (menuRect != null && menuRect.sizeDelta.y < 298f)
                menuRect.sizeDelta = new Vector2(menuRect.sizeDelta.x, 298f);

            Button resume = FindDirectButton(body, "Resume Button");
            Button settings = FindDirectButton(body, "Settings Button");
            Button logout = FindDirectButton(body, "Logout Button");
            Button quit = FindDirectButton(body, "Quit Game Button");

            Button openFriends = FindDirectButton(body, "OpenFriendsButton");
            RemoveDuplicateDirectChildren(body, "OpenFriendsButton", openFriends != null ? openFriends.transform : null);

            Button template = settings ?? resume ?? logout ?? quit;
            if (openFriends == null && template != null)
            {
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, body, false);
                clone.name = "OpenFriendsButton";
                openFriends = clone.GetComponent<Button>();
                if (openFriends == null)
                    openFriends = clone.AddComponent<Button>();
                openFriends.onClick = new Button.ButtonClickedEvent();
            }

            if (openFriends == null)
                openFriends = EnsureButton(body, "OpenFriendsButton", "Friends", font);

            CopyButtonVisualStyle(template, openFriends, font);
            Text label = openFriends.GetComponentInChildren<Text>(true);
            if (label == null)
                label = EnsureText(openFriends.transform, "Label", font, 14, TextAnchor.MiddleLeft);
            label.text = "Friends";
            label.raycastTarget = false;

            PlaceMenuButton(resume, -28f);
            PlaceMenuButton(settings, -76f);
            PlaceMenuButton(openFriends, -124f);
            PlaceMenuButton(logout, -172f);
            PlaceMenuButton(quit, -220f);

            if (settings != null)
                openFriends.transform.SetSiblingIndex(Math.Min(settings.transform.GetSiblingIndex() + 1, body.childCount - 1));

            return openFriends;
        }

        private static Button FindDirectButton(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrWhiteSpace(name))
                return null;
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<Button>() : null;
        }

        private static void RemoveDuplicateDirectChildren(Transform parent, string name, Transform keep)
        {
            if (parent == null || string.IsNullOrWhiteSpace(name))
                return;

            for (int i = parent.childCount - 1; i >= 0; --i)
            {
                Transform child = parent.GetChild(i);
                if (child == null || child == keep || !string.Equals(child.name, name, StringComparison.Ordinal))
                    continue;
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void PlaceMenuButton(Button button, float y)
        {
            if (button == null) return;
            RectTransform rect = button.GetComponent<RectTransform>();
            if (rect == null) return;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(210f, 40f);
            rect.anchoredPosition = new Vector2(0f, y);
        }

        private static void CopyButtonVisualStyle(Button source, Button target, Font fallbackFont)
        {
            if (target == null) return;

            if (source != null)
            {
                Image sourceImage = source.GetComponent<Image>();
                Image targetImage = target.GetComponent<Image>() ?? target.gameObject.AddComponent<Image>();
                if (sourceImage != null)
                {
                    targetImage.color = sourceImage.color;
                    targetImage.sprite = sourceImage.sprite;
                    targetImage.type = sourceImage.type;
                    targetImage.preserveAspect = sourceImage.preserveAspect;
                }

                target.transition = source.transition;
                target.colors = source.colors;
                target.spriteState = source.spriteState;
                target.navigation = source.navigation;
                target.targetGraphic = targetImage;

                Text sourceLabel = source.GetComponentInChildren<Text>(true);
                Text targetLabel = target.GetComponentInChildren<Text>(true);
                if (sourceLabel != null && targetLabel != null)
                {
                    targetLabel.font = sourceLabel.font != null ? sourceLabel.font : fallbackFont;
                    targetLabel.fontSize = sourceLabel.fontSize;
                    targetLabel.fontStyle = sourceLabel.fontStyle;
                    targetLabel.color = sourceLabel.color;
                    targetLabel.alignment = sourceLabel.alignment;
                    targetLabel.horizontalOverflow = sourceLabel.horizontalOverflow;
                    targetLabel.verticalOverflow = sourceLabel.verticalOverflow;
                    targetLabel.supportRichText = sourceLabel.supportRichText;
                }
            }
            else
            {
                Image image = target.GetComponent<Image>() ?? target.gameObject.AddComponent<Image>();
                image.color = new Color(0.055f, 0.075f, 0.115f, 0.94f);
                target.targetGraphic = image;
            }
        }

        private static RectTransform EnsureSingleRect(Transform parent, string name)
        {
            RectTransform keep = null;
            for (int i = parent.childCount - 1; i >= 0; --i)
            {
                Transform child = parent.GetChild(i);
                if (!string.Equals(child.name, name, StringComparison.Ordinal))
                    continue;

                RectTransform rect = child as RectTransform ?? child.GetComponent<RectTransform>();
                if (keep == null)
                    keep = rect;
                else
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            if (keep != null)
                return keep;

            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static Text EnsureSingleText(Transform parent, string name, Font font, int size, TextAnchor anchor)
        {
            Text keep = null;
            for (int i = parent.childCount - 1; i >= 0; --i)
            {
                Transform child = parent.GetChild(i);
                if (!string.Equals(child.name, name, StringComparison.Ordinal))
                    continue;

                Text text = child.GetComponent<Text>();
                if (keep == null && text != null)
                {
                    keep = text;
                    continue;
                }

                if (keep == null)
                {
                    text = child.gameObject.AddComponent<Text>();
                    keep = text;
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            if (keep == null)
                keep = EnsureText(parent, name, font, size, anchor);

            keep.font = font;
            keep.fontSize = size;
            keep.alignment = anchor;
            keep.color = Color.white;
            return keep;
        }

        private static ClientSocialEconomyFriendRow EnsureFriendRow(RectTransform parent, Font font)
        {
            Transform existing = parent.Find("FriendRowTemplate");
            RemoveDuplicateDirectChildren(parent, "FriendRowTemplate", existing);
            GameObject go = existing != null ? existing.gameObject : new GameObject("FriendRowTemplate", typeof(RectTransform), typeof(Image));
            if (existing == null) go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0f, 42f);
            Image bg = go.GetComponent<Image>();
            bg.color = new Color(0.09f, 0.10f, 0.12f, 0.94f);

            Text name = EnsureText(go.transform, "Name", font, 16, TextAnchor.MiddleLeft);
            SetRowRect(name.rectTransform, 0f, 0.58f, 10f, -4f);
            Text presence = EnsureText(go.transform, "Presence", font, 14, TextAnchor.MiddleCenter);
            SetRowRect(presence.rectTransform, 0.58f, 0.80f, 0f, 0f);
            Button remove = EnsureButton(go.transform, "RemoveButton", "REMOVE", font);
            SetRowRect(remove.GetComponent<RectTransform>(), 0.80f, 1f, 4f, -4f);

            ClientSocialEconomyFriendRow row = go.GetComponent<ClientSocialEconomyFriendRow>() ?? go.AddComponent<ClientSocialEconomyFriendRow>();
            row.ConfigureForEditor(name, presence, remove);
            go.SetActive(false);
            EditorUtility.SetDirty(row);
            return row;
        }

        private static ClientSocialEconomyRowView EnsurePartyRow(RectTransform parent, Font font)
        {
            Transform existing = parent.Find("PartyRowTemplate");
            RemoveDuplicateDirectChildren(parent, "PartyRowTemplate", existing);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject("PartyRowTemplate", typeof(RectTransform), typeof(Image));
            if (existing == null) go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0f, 42f);
            Image bg = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            bg.color = new Color(0.09f, 0.10f, 0.12f, 0.94f);

            Text primary = EnsureText(go.transform, "PrimaryText", font, 16, TextAnchor.MiddleLeft);
            SetRowRect(primary.rectTransform, 0f, 0.56f, 10f, -4f);
            Text secondary = EnsureText(go.transform, "SecondaryText", font, 14, TextAnchor.MiddleCenter);
            SetRowRect(secondary.rectTransform, 0.56f, 0.78f, 0f, 0f);
            Button primaryButton = EnsureButton(go.transform, "PrimaryButton", "KICK", font);
            SetRowRect(primaryButton.GetComponent<RectTransform>(), 0.78f, 1f, 4f, -4f);
            Button secondaryButton = EnsureButton(go.transform, "SecondaryButton", string.Empty, font);
            secondaryButton.gameObject.SetActive(false);

            ClientSocialEconomyRowView row = go.GetComponent<ClientSocialEconomyRowView>() ?? go.AddComponent<ClientSocialEconomyRowView>();
            row.CaptureAuthoredReferencesForEditor();
            go.SetActive(false);
            EditorUtility.SetDirty(row);
            return row;
        }

        private static void SetRowRect(RectTransform rect, float minX, float maxX, float left, float right)
        {
            rect.anchorMin = new Vector2(minX, 0f);
            rect.anchorMax = new Vector2(maxX, 1f);
            rect.offsetMin = new Vector2(left, 2f);
            rect.offsetMax = new Vector2(right, -2f);
        }

        private static void PlaceBottomButton(RectTransform rect, float x)
        {
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(160f, 44f);
            rect.anchoredPosition = new Vector2(x, 18f);
        }

        private static GameObject FindPanel(GameObject root, ClientUIPanelId id)
        {
            ClientUIPanelMarker[] markers = root.GetComponentsInChildren<ClientUIPanelMarker>(true);
            for (int i = 0; i < markers.Length; ++i)
                if (markers[i] != null && markers[i].PanelId == id) return markers[i].gameObject;
            return null;
        }

        private static RectTransform EnsureRect(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.GetComponent<RectTransform>();
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static Text EnsureText(Transform parent, string name, Font font, int size, TextAnchor anchor)
        {
            Transform existing = parent.Find(name);
            Text text = existing != null ? existing.GetComponent<Text>() : null;
            if (text == null)
            {
                GameObject go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
                if (existing == null) go.transform.SetParent(parent, false);
                text = go.AddComponent<Text>();
            }
            text.font = font;
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            return text;
        }

        private static Button EnsureButton(Transform parent, string name, string label, Font font)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
            if (existing == null) go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
            image.color = new Color(0.14f, 0.15f, 0.18f, 1f);
            Button button = go.GetComponent<Button>() ?? go.AddComponent<Button>();
            Text text = EnsureText(go.transform, "Label", font, 14, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform);
            text.text = label;
            text.raycastTarget = false;
            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
#endif
