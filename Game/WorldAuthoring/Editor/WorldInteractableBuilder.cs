using Game.Shared.Interactions;
using Game.Shared.World;
using UnityEditor;
using UnityEngine;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// Production reusable authoring entry point for canonical baked world interactables.
    /// New presets belong here instead of accumulating one-off scene/test generators.
    /// </summary>
    public sealed class WorldInteractableBuilder : EditorWindow
    {
        private enum Preset
        {
            Door = 0,
            SearchableLoot = 1,
            CraftingStation = 2,
            FactionRecruiter = 3,
            SurveillanceCamera = 4,
            CombatTestDummy = 5,
            HarvestTestLootbox = 6,
            RecoveryValidationSet = 7,
        }

        private Preset _preset;
        private Transform _parent;
        private string _label = "Door";
        private string _lootTableId = "loot.world.demo_scrap";
        private string _craftingStationId = "station.recovery_workbench";
        private string _factionDefinitionId = "faction.vampire";
        private string _harvestProfileId = "recovery_salvage";

        [MenuItem("MMO Tools/World/Add World Interactable")]
        private static void Open()
        {
            WorldInteractableBuilder window = GetWindow<WorldInteractableBuilder>();
            window.titleContent = new GUIContent("World Interactable");
            window.minSize = new Vector2(420f, 300f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Add World Interactable", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Creates canonical WorldObject + WorldInteractable authoring. " +
                "Run Server World Bake V2 after adding or changing baked objects.",
                MessageType.Info);

            Preset previousPreset = _preset;
            _preset = (Preset)EditorGUILayout.EnumPopup("Preset", _preset);
            if (_preset != previousPreset &&
                (string.IsNullOrWhiteSpace(_label) ||
                 string.Equals(_label, DefaultLabel(previousPreset), System.StringComparison.Ordinal)))
            {
                _label = DefaultLabel(_preset);
            }

            _parent = (Transform)EditorGUILayout.ObjectField(
                "Optional Parent", _parent, typeof(Transform), true);
            _label = EditorGUILayout.TextField(
                "Label",
                string.IsNullOrWhiteSpace(_label) ? DefaultLabel(_preset) : _label);

            if (_preset == Preset.SearchableLoot)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Loot", EditorStyles.boldLabel);
                _lootTableId = EditorGUILayout.TextField("Loot Table ID", _lootTableId);
                EditorGUILayout.HelpBox("The ID must exist in the authoritative Loot catalog.", MessageType.None);
            }
            else if (_preset == Preset.CraftingStation)
            {
                EditorGUILayout.Space();
                _craftingStationId = EditorGUILayout.TextField("Crafting Station ID", _craftingStationId);
            }
            else if (_preset == Preset.FactionRecruiter)
            {
                EditorGUILayout.Space();
                _factionDefinitionId = EditorGUILayout.TextField("Faction ID", _factionDefinitionId);
            }
            else if (_preset == Preset.HarvestTestLootbox)
            {
                EditorGUILayout.Space();
                _harvestProfileId = EditorGUILayout.TextField("Harvest Profile ID", _harvestProfileId);
                EditorGUILayout.HelpBox("Creates a HarvestNode using the lootbox/cube test presentation. It does not use Searchable loot state.", MessageType.None);
            }
            else if (_preset == Preset.RecoveryValidationSet)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Adds recovery loot, a Harvest test lootbox, workbench, Vampire/Hunter recruiters, a surveillance camera, and a combat dummy. All objects use normal production authoring and must be baked with Server World Bake V2.",
                    MessageType.Info);
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add", GUILayout.Height(38f)))
                CreateSelected();

        }

        private void CreateSelected()
        {
            GameObject created;
            switch (_preset)
            {
                case Preset.Door: created = CreateDoor(); break;
                case Preset.SearchableLoot: created = CreateSearchable(); break;
                case Preset.CraftingStation: created = CreateCraftingStation(); break;
                case Preset.FactionRecruiter: created = CreateFactionRecruiter(); break;
                case Preset.SurveillanceCamera: created = CreateSurveillanceCamera(); break;
                case Preset.CombatTestDummy: created = CreateCombatDummy(); break;
                case Preset.HarvestTestLootbox: created = CreateHarvestTestLootbox(); break;
                case Preset.RecoveryValidationSet: created = CreateRecoveryValidationSet(); break;
                default: created = null; break;
            }

            if (created == null)
                return;

            if (_parent != null)
                Undo.SetTransformParent(created.transform, _parent, "Parent World Interactable");

            PositionAtScenePivot(created.transform);
            Selection.activeGameObject = created;
            EditorGUIUtility.PingObject(created);

            Debug.Log(
                $"[World Interactable] Added {_preset}: '{created.name}'. " +
                "Save the scene and run Server World Bake V2 before testing.");
        }

        private GameObject CreateCraftingStation() =>
            CreateCraftingStationObject(
                string.IsNullOrWhiteSpace(_label) ? "Recovery Workbench" : _label.Trim(),
                string.IsNullOrWhiteSpace(_craftingStationId) ? "station.recovery_workbench" : _craftingStationId.Trim());

        private static GameObject CreateCraftingStationObject(string label, string stationId)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(root, "Add Crafting Station");
            root.name = label;
            root.transform.localScale = new Vector3(1.6f, 0.9f, 0.8f);
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "crafting_station";
            interactable.kind = ServerWorldInteractableKind.Generic;
            interactable.gameplayProfileId = "crafting_station";
            interactable.label = label;
            interactable.craftingStationId = stationId;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "craft", actionId = InteractionActionId.Craft, displayLabel = "Craft", categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f, maximumFacingAngle = 180f, cancelOnDamage = true, cancelOnMovement = true,
                    cancelOnTargetUnavailable = true, contentLevel = InteractionContentLevel.General, feature = InteractionFeature.WorldObjects,
                }
            };
            return root;
        }

        private GameObject CreateFactionRecruiter() =>
            CreateFactionRecruiterObject(
                string.IsNullOrWhiteSpace(_label) ? "Faction Recruiter" : _label.Trim(),
                string.IsNullOrWhiteSpace(_factionDefinitionId) ? "faction.vampire" : _factionDefinitionId.Trim());

        private static GameObject CreateFactionRecruiterObject(string label, string factionId)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(root, "Add Faction Recruiter");
            root.name = label;
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "faction_recruiter";
            interactable.kind = ServerWorldInteractableKind.Generic;
            interactable.gameplayProfileId = "faction_recruiter";
            interactable.label = label;
            interactable.factionDefinitionId = factionId;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "recruit", actionId = InteractionActionId.Recruit, displayLabel = "Join Faction", categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f, maximumFacingAngle = 180f, cancelOnDamage = true, cancelOnMovement = true,
                    cancelOnTargetUnavailable = true, contentLevel = InteractionContentLevel.General, feature = InteractionFeature.WorldObjects,
                }
            };
            return root;
        }

        private GameObject CreateSurveillanceCamera() => CreateSurveillanceCameraObject(
            string.IsNullOrWhiteSpace(_label) ? "Surveillance Camera" : _label.Trim());

        private static GameObject CreateSurveillanceCameraObject(string label)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(root, "Add Surveillance Camera");
            root.name = label;
            root.transform.localScale = new Vector3(0.35f, 0.25f, 0.55f);
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "surveillance_camera";
            interactable.kind = ServerWorldInteractableKind.Generic;
            interactable.gameplayProfileId = "surveillance_camera";
            interactable.label = label;
            interactable.interactions = System.Array.Empty<WorldInteractable.Interaction>();
            interactable.surveillanceCamera = true;
            interactable.surveillanceRadius = 12f;
            interactable.surveillanceEvidence = 2;
            return root;
        }

        private GameObject CreateCombatDummy() => CreateCombatDummyObject(
            string.IsNullOrWhiteSpace(_label) ? "Recovery Combat Dummy" : _label.Trim());

        private static GameObject CreateCombatDummyObject(string label)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(root, "Add Combat Test Dummy");
            root.name = label;
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "combat_test_dummy";
            interactable.kind = ServerWorldInteractableKind.Generic;
            interactable.label = label;
            interactable.interactions = System.Array.Empty<WorldInteractable.Interaction>();
            CombatTestDummy dummy = Undo.AddComponent<CombatTestDummy>(root);
            dummy.healthMaximum = 500;
            dummy.resetOnDefeat = false;
            return root;
        }

        private GameObject CreateHarvestTestLootbox() =>
            CreateHarvestTestLootboxObject(
                string.IsNullOrWhiteSpace(_label) ? "Harvest Test Lootbox" : _label.Trim(),
                string.IsNullOrWhiteSpace(_harvestProfileId) ? "recovery_salvage" : _harvestProfileId.Trim());

        private static GameObject CreateHarvestTestLootboxObject(string label, string profileId)
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(root, "Add Harvest Test Lootbox");
            root.name = label;
            root.transform.localScale = new Vector3(1.35f, 0.7f, 1f);
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "harvest_test_lootbox";
            interactable.kind = ServerWorldInteractableKind.HarvestNode;
            interactable.gameplayProfileId = profileId;
            interactable.label = label;
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "harvest", actionId = InteractionActionId.Harvest, displayLabel = "Harvest", categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 4f, maximumFacingAngle = 180f, exclusiveOccupancy = true, fixedDurationSeconds = 2f,
                    cancelOnDamage = true, cancelOnMovement = true, cancelOnTargetUnavailable = true,
                    contentLevel = InteractionContentLevel.General, feature = InteractionFeature.Harvesting,
                }
            };
            return root;
        }

        private static GameObject CreateRecoveryLootObject()
        {
            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(root, "Add Recovery Loot");
            root.name = "Recovery Scrap Crate";
            root.transform.localScale = new Vector3(1.35f, 0.7f, 1f);
            Collider collider = root.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "searchable_loot";
            interactable.kind = ServerWorldInteractableKind.Searchable;
            interactable.gameplayProfileId = "searchable_basic";
            interactable.label = root.name;
            interactable.lootTableId = "loot.world.recovery_scrap";
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "search", actionId = InteractionActionId.Search, displayLabel = "Search", categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f, maximumFacingAngle = 180f, cancelOnDamage = true, cancelOnMovement = true,
                    cancelOnTargetUnavailable = true, contentLevel = InteractionContentLevel.General, feature = InteractionFeature.WorldObjects,
                }
            };
            return root;
        }

        private static GameObject CreateRecoveryValidationSet()
        {
            GameObject root = new GameObject("Recovery Validation Set");
            Undo.RegisterCreatedObjectUndo(root, "Add Recovery Validation Set");
            GameObject loot = CreateRecoveryLootObject();
            GameObject harvest = CreateHarvestTestLootboxObject("Harvest Test Lootbox", "recovery_salvage");
            GameObject bench = CreateCraftingStationObject("Recovery Workbench", "station.recovery_workbench");
            GameObject vampire = CreateFactionRecruiterObject("Vampire Recruiter", "faction.vampire");
            GameObject hunter = CreateFactionRecruiterObject("Hunter Recruiter", "faction.hunter");
            GameObject camera = CreateSurveillanceCameraObject("Recovery Surveillance Camera");
            GameObject dummy = CreateCombatDummyObject("Recovery Combat Dummy");

            GameObject[] children = { loot, harvest, bench, vampire, hunter, camera, dummy };
            Vector3[] positions =
            {
                new Vector3(-4.5f, 0.5f, 0f), new Vector3(-2f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(3f, 1f, 0f),
                new Vector3(5f, 1f, 0f), new Vector3(1f, 3f, 2f), new Vector3(1f, 1f, 4f),
            };
            for (int i = 0; i < children.Length; ++i)
            {
                Undo.SetTransformParent(children[i].transform, root.transform, "Parent Recovery Fixture");
                children[i].transform.localPosition = positions[i];
            }
            return root;
        }

        private GameObject CreateDoor()
        {
            GameObject root = new GameObject(
                string.IsNullOrWhiteSpace(_label) ? "Door" : _label.Trim());
            Undo.RegisterCreatedObjectUndo(root, "Add Door");

            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "door";
            interactable.kind = ServerWorldInteractableKind.Door;
            interactable.gameplayProfileId = "door_standard";
            interactable.label = root.name;
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.dynamicBlocker = true;
            interactable.blockerKind = ServerDynamicBlockerKind.Door;
            interactable.blockerEnabledByDefault = true;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "open",
                    actionId = InteractionActionId.Open,
                    displayLabel = "Open / Close",
                    categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f,
                    maximumFacingAngle = 180f,
                    cancelOnDamage = true,
                    cancelOnMovement = true,
                    cancelOnTargetUnavailable = true,
                    consentMode = InteractionConsentMode.None,
                    contentLevel = InteractionContentLevel.General,
                    feature = InteractionFeature.WorldObjects,
                }
            };

            CreateDoorFrame(root.transform, out Transform pivot);

            GameObject blockerAnchor = new GameObject("Server Blocker Anchor");
            Undo.RegisterCreatedObjectUndo(blockerAnchor, "Add Door Blocker Anchor");
            blockerAnchor.transform.SetParent(root.transform, false);
            blockerAnchor.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            interactable.blockerAnchor = blockerAnchor.transform;
            interactable.blockerSize = new Vector3(1.35f, 2.35f, 0.26f);

            WorldDoorPresentation presentation = Undo.AddComponent<WorldDoorPresentation>(root);
            SerializedObject serialized = new SerializedObject(presentation);
            serialized.FindProperty("doorPivot").objectReferenceValue = pivot;
            serialized.FindProperty("openYawDegrees").floatValue = 90f;
            serialized.FindProperty("openSeconds").floatValue = 0.35f;
            serialized.FindProperty("visuallyOpenByDefault").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        private GameObject CreateSearchable()
        {
            if (string.IsNullOrWhiteSpace(_lootTableId))
            {
                EditorUtility.DisplayDialog(
                    "World Interactable",
                    "Item Definition ID is required.",
                    "OK");
                return null;
            }

            string objectName = string.IsNullOrWhiteSpace(_label)
                ? "Searchable Loot"
                : _label.Trim();

            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(root, "Add Searchable Loot");
            root.name = objectName;
            root.transform.localScale = new Vector3(1.35f, 0.7f, 1f);
            // Searchable test props are interaction targets, not movement authority. Keep
            // the primitive collider as a trigger so pointer raycasts can select it without
            // creating a client-only solid obstacle that the standalone server did not bake.
            Collider targetCollider = root.GetComponent<Collider>();
            if (targetCollider != null)
                targetCollider.isTrigger = true;

            Undo.AddComponent<WorldObject>(root);
            WorldInteractable interactable = Undo.AddComponent<WorldInteractable>(root);
            interactable.definitionId = "searchable_loot";
            interactable.kind = ServerWorldInteractableKind.Searchable;
            interactable.gameplayProfileId = "searchable_basic";
            interactable.label = objectName;
            interactable.persistentState = false;
            interactable.enabledByDefault = true;
            interactable.dynamicBlocker = false;
            interactable.interactions = new[]
            {
                new WorldInteractable.Interaction
                {
                    definitionId = "search",
                    actionId = InteractionActionId.Search,
                    displayLabel = "Search",
                    categoryId = InteractionCategoryId.Use,
                    maximumUseDistance = 3.25f,
                    maximumFacingAngle = 180f,
                    cancelOnDamage = true,
                    cancelOnMovement = true,
                    cancelOnTargetUnavailable = true,
                    consentMode = InteractionConsentMode.None,
                    contentLevel = InteractionContentLevel.General,
                    feature = InteractionFeature.WorldObjects,
                }
            };
            interactable.lootTableId = _lootTableId.Trim();

            return root;
        }

        private static void CreateDoorFrame(Transform root, out Transform pivot)
        {
            CreateVisualCube(root, "Frame Left", new Vector3(-0.85f, 1.2f, 0.18f), new Vector3(0.18f, 2.6f, 0.2f), false);
            CreateVisualCube(root, "Frame Right", new Vector3(0.85f, 1.2f, 0.18f), new Vector3(0.18f, 2.6f, 0.2f), false);
            CreateVisualCube(root, "Frame Header", new Vector3(0f, 2.48f, 0.18f), new Vector3(1.88f, 0.18f, 0.2f), false);

            GameObject pivotObject = new GameObject("Door Pivot");
            Undo.RegisterCreatedObjectUndo(pivotObject, "Add Door Pivot");
            pivotObject.transform.SetParent(root, false);
            pivotObject.transform.localPosition = new Vector3(-0.65f, 1.15f, 0f);

            GameObject leaf = CreateVisualCube(
                pivotObject.transform,
                "Door Leaf",
                new Vector3(0.65f, 0f, 0f),
                new Vector3(1.3f, 2.3f, 0.14f),
                true);
            leaf.GetComponent<Collider>().isTrigger = false;
            pivot = pivotObject.transform;
        }

        private static GameObject CreateVisualCube(
            Transform parent,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            bool keepCollider)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(go, $"Add {name}");
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            if (!keepCollider)
                UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static void PositionAtScenePivot(Transform transform)
        {
            if (transform == null || SceneView.lastActiveSceneView == null)
                return;
            Vector3 pivot = SceneView.lastActiveSceneView.pivot;
            transform.position = new Vector3(pivot.x, Mathf.Max(0f, pivot.y), pivot.z);
        }

        private static string DefaultLabel(Preset preset)
        {
            switch (preset)
            {
                case Preset.Door: return "Door";
                case Preset.SearchableLoot: return "Searchable Loot";
                case Preset.CraftingStation: return "Recovery Workbench";
                case Preset.FactionRecruiter: return "Faction Recruiter";
                case Preset.SurveillanceCamera: return "Surveillance Camera";
                case Preset.CombatTestDummy: return "Recovery Combat Dummy";
                case Preset.HarvestTestLootbox: return "Harvest Test Lootbox";
                case Preset.RecoveryValidationSet: return "Recovery Validation Set";
                default: return "World Interactable";
            }
        }
    }
}
