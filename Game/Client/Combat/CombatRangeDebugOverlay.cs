using System;
using Game.Client.Targeting;
using Game.Client.UI.Root;
using Game.WorldAuthoring;
using Game.Shared.Abilities;
using Player.Client;
using Player.Networking;
using UnityEngine;

namespace Game.Client.Combat
{
    /// <summary>
    /// Temporary client-only visualization for validating direct basic-attack reach.
    /// It uses the shared hard combat request cap for the current combat mode and
    /// mirrors the standalone GameServer's direct-contact geometry for presentation/testing.
    /// It sends no messages and owns no gameplay authority.
    ///
    /// F10 toggles the overlay. Selected Players / Combat Test Dummies receive a ground ring:
    /// green when their server-style body point is inside the direct-contact envelope,
    /// red when it is outside. LOS is intentionally not predicted here; this tool answers
    /// the range/contact-envelope question only.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(20000)]
    public sealed class CombatRangeDebugOverlay : MonoBehaviour
    {
        // Mirrors GameServerHost.CombatContacts.cs. These are presentation-only test constants;
        // No attack-range field is added to the owner-state wire contract.
        private const float DirectCombatContactRadius = 0.85f;
        private const float DirectCombatOriginHeight = 1.10f;
        private const float DirectCombatTargetHeight = 1.00f;
        private const float GroundLift = 0.045f;
        private const int MeleeArcSegments = 32;
        private const int TargetRingSegments = 32;

        private static readonly Color RangeColor = new Color(0.12f, 0.80f, 1.00f, 0.95f);
        private static readonly Color ToleranceColor = new Color(1.00f, 0.76f, 0.12f, 0.95f);
        private static readonly Color InRangeColor = new Color(0.15f, 1.00f, 0.28f, 0.98f);
        private static readonly Color OutOfRangeColor = new Color(1.00f, 0.20f, 0.16f, 0.98f);

        [SerializeField] private KeyCode toggleKey = KeyCode.F10;
        [SerializeField] private bool visible = true;
        [SerializeField] private bool showStatusPanel = true;
        [SerializeField] private float lineWidth = 0.045f;
        [SerializeField] private float selectedTargetRingRadius = 0.72f;

        private ClientUIRoot _ui;
        private PlayerEntityGameManager _manager;
        private PlayerEntityClient _owner;
        private PlayerEntityLocomotionCameraController _cameraController;
        private CombatTestDummy _resolvedDummy;
        private long _resolvedDummyStableId;
        private float _nextBindingRefreshAt;

        private LineRenderer _authoredRangeLine;
        private LineRenderer _toleranceExtensionLine;
        private LineRenderer _groundEnvelope;
        private LineRenderer _targetRing;
        private Material _rangeMaterial;
        private Material _toleranceMaterial;
        private Material _inRangeMaterial;
        private Material _outOfRangeMaterial;

        private float _lastRange;
        private float _lastEffectiveRange;
        private float _lastPitch;
        private float _lastTargetDistance;
        private float _lastTargetEdgeDistance;
        private BasicAttackMode _lastMode;
        private bool _hasSelectedTarget;
        private bool _selectedTargetInRange;
        private string _selectedTargetLabel = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
#if !UNITY_SERVER
            if (FindFirstObjectByType<CombatRangeDebugOverlay>() != null)
                return;

            var host = new GameObject("__CombatRangeDebugOverlay");
            DontDestroyOnLoad(host);
            host.AddComponent<CombatRangeDebugOverlay>();
#endif
        }

        private void Awake()
        {
#if UNITY_SERVER
            enabled = false;
#else
            CreateMaterials();
            _authoredRangeLine = CreateLine("AuthoredRange", _rangeMaterial, 2, false);
            _toleranceExtensionLine = CreateLine("BodyToleranceExtension", _toleranceMaterial, 2, false);
            _groundEnvelope = CreateLine("DirectContactEnvelope", _rangeMaterial, MeleeArcSegments + 3, false);
            _targetRing = CreateLine("SelectedTargetRangeRing", _outOfRangeMaterial, TargetRingSegments + 1, true);
            SetVisualsActive(false);
#endif
        }

        private void OnDestroy()
        {
#if !UNITY_SERVER
            DestroyMaterial(_rangeMaterial);
            DestroyMaterial(_toleranceMaterial);
            DestroyMaterial(_inRangeMaterial);
            DestroyMaterial(_outOfRangeMaterial);
#endif
        }

        private void Update()
        {
#if !UNITY_SERVER
            if (UnityEngine.Input.GetKeyDown(toggleKey))
                visible = !visible;

            if (!visible)
            {
                SetVisualsActive(false);
                return;
            }

            RefreshBindingsIfNeeded();
            if (_ui == null || !_ui.GameplaySessionActive || _manager == null ||
                !PlayerEntityClient.TryGetOwner(out _owner) || _owner == null)
            {
                SetVisualsActive(false);
                return;
            }

            PlayerCombatOwnerStateMessage ownerState = _manager.LatestCombatOwnerState;
            if (!ownerState.success || ownerState.revision <= 0 || CombatRangePolicy.ClientRequestRangeForMode(ownerState.Mode) <= 0f)
            {
                SetVisualsActive(false);
                return;
            }

            _cameraController = _owner.GetComponent<PlayerEntityLocomotionCameraController>();
            float yaw = _cameraController != null && _cameraController.HasFacingState
                ? _cameraController.CurrentFacingYaw
                : _owner.PresentationYaw;
            BasicAttackMode mode = ownerState.Mode;
            float pitch = mode == BasicAttackMode.Firearm && _cameraController != null
                ? _cameraController.CurrentAimPitchDegrees
                : 0f;
            if (mode == BasicAttackMode.Firearm && _cameraController != null)
                _cameraController.TryGetPrecisionAim(out yaw, out pitch);

            Vector3 root = _owner.PresentationPosition;
            Vector3 origin = root + Vector3.up * DirectCombatOriginHeight;
            Vector3 direction = BuildAimDirection(yaw, pitch);
            float range = Mathf.Max(0.1f, CombatRangePolicy.ClientRequestRangeForMode(ownerState.Mode));
            float effectiveRange = range + (mode == BasicAttackMode.Firearm
                ? DirectCombatContactRadius
                : CombatRangePolicy.MeleeTargetBodyRadius);

            _lastRange = range;
            _lastEffectiveRange = effectiveRange;
            _lastPitch = pitch;
            _lastMode = mode;

            DrawRange(origin, direction, root, yaw, range, effectiveRange, mode);
            DrawSelectedTarget(origin, direction, root, yaw, range, mode);
#endif
        }

        private void RefreshBindingsIfNeeded()
        {
            if (_ui != null && _manager != null)
                return;

            float now = Time.unscaledTime;
            if (now < _nextBindingRefreshAt)
                return;
            _nextBindingRefreshAt = now + 1f;

            if (_ui == null)
                _ui = FindFirstObjectByType<ClientUIRoot>();
            if (_manager == null)
                _manager = _ui != null && _ui.Manager != null
                    ? _ui.Manager
                    : FindFirstObjectByType<PlayerEntityGameManager>();
        }

        private void DrawRange(
            Vector3 origin,
            Vector3 direction,
            Vector3 ownerRoot,
            float facingYaw,
            float authoredRange,
            float effectiveRange,
            BasicAttackMode mode)
        {
            if (mode == BasicAttackMode.Firearm)
            {
                SetLineActive(_authoredRangeLine, true);
                SetLineActive(_toleranceExtensionLine, true);
                SetLineActive(_groundEnvelope, false);

                Vector3 authoredEnd = origin + direction * authoredRange;
                Vector3 toleranceEnd = origin + direction * effectiveRange;
                _authoredRangeLine.SetPosition(0, origin);
                _authoredRangeLine.SetPosition(1, authoredEnd);
                _toleranceExtensionLine.SetPosition(0, authoredEnd);
                _toleranceExtensionLine.SetPosition(1, toleranceEnd);
                return;
            }

            SetLineActive(_authoredRangeLine, false);
            SetLineActive(_toleranceExtensionLine, false);
            DrawMeleeArc(ownerRoot, facingYaw, effectiveRange, mode);
            SetLineActive(_groundEnvelope, true);
        }

        private void DrawMeleeArc(
            Vector3 ownerRoot,
            float facingYaw,
            float effectiveRange,
            BasicAttackMode mode)
        {
            float totalArc = CombatRangePolicy.SwingArcDegreesFor(mode);
            float halfArc = totalArc * 0.5f;
            Vector3 center = ownerRoot + Vector3.up * GroundLift;

            _groundEnvelope.loop = false;
            _groundEnvelope.positionCount = MeleeArcSegments + 3;
            _groundEnvelope.SetPosition(0, center);
            for (int i = 0; i <= MeleeArcSegments; ++i)
            {
                float t = i / (float)MeleeArcSegments;
                float yaw = facingYaw + Mathf.Lerp(-halfArc, halfArc, t);
                Vector3 radial = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                _groundEnvelope.SetPosition(i + 1, center + radial * effectiveRange);
            }
            _groundEnvelope.SetPosition(MeleeArcSegments + 2, center);
        }

        private void DrawSelectedTarget(
            Vector3 origin,
            Vector3 direction,
            Vector3 ownerRoot,
            float facingYaw,
            float range,
            BasicAttackMode mode)
        {
            _hasSelectedTarget = TryResolveSelectedTarget(out Vector3 targetRoot, out _selectedTargetLabel);
            if (!_hasSelectedTarget)
            {
                _lastTargetDistance = 0f;
                _lastTargetEdgeDistance = 0f;
                SetLineActive(_targetRing, false);
                return;
            }

            if (mode == BasicAttackMode.Firearm)
            {
                Vector3 bodyPoint = targetRoot + Vector3.up * DirectCombatTargetHeight;
                _lastTargetDistance = Vector3.Distance(origin, bodyPoint);
                _lastTargetEdgeDistance = Mathf.Max(0f, _lastTargetDistance - DirectCombatContactRadius);
                _selectedTargetInRange = IsInsideDirectContactEnvelope(origin, direction, bodyPoint, range);
            }
            else
            {
                _selectedTargetInRange = IsInsideMeleeSwingEnvelope(
                    ownerRoot,
                    facingYaw,
                    targetRoot,
                    range,
                    mode,
                    out _lastTargetDistance,
                    out _lastTargetEdgeDistance);
            }

            _targetRing.sharedMaterial = _selectedTargetInRange ? _inRangeMaterial : _outOfRangeMaterial;
            float ringRadius = Mathf.Max(0.25f, selectedTargetRingRadius);
            float y = targetRoot.y + GroundLift * 1.5f;
            _targetRing.positionCount = TargetRingSegments + 1;
            for (int i = 0; i <= TargetRingSegments; ++i)
            {
                float angle = (i / (float)TargetRingSegments) * Mathf.PI * 2f;
                _targetRing.SetPosition(
                    i,
                    new Vector3(
                        targetRoot.x + Mathf.Cos(angle) * ringRadius,
                        y,
                        targetRoot.z + Mathf.Sin(angle) * ringRadius));
            }
            SetLineActive(_targetRing, true);
        }

        private bool TryResolveSelectedTarget(out Vector3 targetRoot, out string label)
        {
            targetRoot = default;
            label = string.Empty;
            if (_ui == null)
                return false;

            if (_ui.SelectedCombatTarget.IsPlayer)
            {
                PlayerEntityClient selected = PlayerEntityTargetSelection.SelectedClient;
                if (selected == null || !selected.IsTargetableRemotePlayer || selected.PresentationTransform == null)
                    return false;
                targetRoot = selected.PresentationTransform.position;
                label = string.IsNullOrWhiteSpace(selected.DisplayName)
                    ? $"Player {selected.NetworkBridge.ObjectId}"
                    : selected.DisplayName;
                return true;
            }

            if (!_ui.SelectedCombatTarget.IsCombatTestTarget)
                return false;

            long stableId = _ui.SelectedCombatTarget.primaryId;
            if (_resolvedDummy == null || _resolvedDummyStableId != stableId)
            {
                _resolvedDummy = null;
                _resolvedDummyStableId = stableId;
                CombatTestDummy[] dummies = FindObjectsByType<CombatTestDummy>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int i = 0; i < dummies.Length; ++i)
                {
                    CombatTestDummy candidate = dummies[i];
                    WorldObject worldObject = candidate != null ? candidate.GetComponent<WorldObject>() : null;
                    if (worldObject == null || worldObject.StableId != stableId)
                        continue;
                    _resolvedDummy = candidate;
                    break;
                }
            }

            if (_resolvedDummy == null)
                return false;

            targetRoot = _resolvedDummy.transform.position;
            label = string.IsNullOrWhiteSpace(_resolvedDummy.gameObject.name)
                ? "Combat Test Dummy"
                : _resolvedDummy.gameObject.name;
            return true;
        }

        private static bool IsInsideDirectContactEnvelope(
            Vector3 origin,
            Vector3 direction,
            Vector3 target,
            float range)
        {
            Vector3 delta = target - origin;
            float along = Vector3.Dot(delta, direction);
            if (along < 0f || along > range + DirectCombatContactRadius)
                return false;

            float perpendicularSquared = Mathf.Max(0f, delta.sqrMagnitude - (along * along));
            return perpendicularSquared <= DirectCombatContactRadius * DirectCombatContactRadius;
        }

        private static bool IsInsideMeleeSwingEnvelope(
            Vector3 sourceRoot,
            float facingYaw,
            Vector3 targetRoot,
            float range,
            BasicAttackMode mode,
            out float centerDistance,
            out float edgeDistance)
        {
            centerDistance = 0f;
            edgeDistance = 0f;
            if (mode != BasicAttackMode.Unarmed && mode != BasicAttackMode.MeleeWeapon)
                return false;

            float dy = targetRoot.y - sourceRoot.y;
            if (Mathf.Abs(dy) > CombatRangePolicy.MeleeVerticalTolerance)
                return false;

            Vector2 delta = new Vector2(targetRoot.x - sourceRoot.x, targetRoot.z - sourceRoot.z);
            centerDistance = delta.magnitude;
            float bodyRadius = CombatRangePolicy.MeleeTargetBodyRadius;
            edgeDistance = Mathf.Max(0f, centerDistance - bodyRadius);
            if (edgeDistance > range)
                return false;
            if (centerDistance <= 0.001f)
                return true;

            float totalArc = CombatRangePolicy.SwingArcDegreesFor(mode);
            if (totalArc <= 0f)
                return false;

            float allowedHalfArc = totalArc * 0.5f;
            Vector2 facing = new Vector2(Mathf.Sin(facingYaw * Mathf.Deg2Rad), Mathf.Cos(facingYaw * Mathf.Deg2Rad));
            float dot = Vector2.Dot(delta / centerDistance, facing);
            return dot >= Mathf.Cos(allowedHalfArc * Mathf.Deg2Rad);
        }

        private static Vector3 BuildAimDirection(float yawDegrees, float pitchDegrees)
        {
            Quaternion rotation = Quaternion.Euler(-pitchDegrees, yawDegrees, 0f);
            // Unity positive X rotation looks down, while the combat wire defines positive pitch as up.
            return (rotation * Vector3.forward).normalized;
        }

        private LineRenderer CreateLine(string childName, Material material, int positions, bool loop)
        {
            var child = new GameObject(childName);
            child.transform.SetParent(transform, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.positionCount = positions;
            line.startWidth = Mathf.Max(0.01f, lineWidth);
            line.endWidth = Mathf.Max(0.01f, lineWidth);
            line.numCapVertices = 3;
            line.numCornerVertices = 2;
            line.sharedMaterial = material;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private void CreateMaterials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            _rangeMaterial = CreateMaterial(shader, RangeColor, "CombatRangeDebug_Range");
            _toleranceMaterial = CreateMaterial(shader, ToleranceColor, "CombatRangeDebug_Tolerance");
            _inRangeMaterial = CreateMaterial(shader, InRangeColor, "CombatRangeDebug_InRange");
            _outOfRangeMaterial = CreateMaterial(shader, OutOfRangeColor, "CombatRangeDebug_OutOfRange");
        }

        private static Material CreateMaterial(Shader shader, Color color, string name)
        {
            if (shader == null)
                return null;
            var material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            return material;
        }

        private static void DestroyMaterial(Material material)
        {
            if (material != null)
                Destroy(material);
        }

        private void SetVisualsActive(bool active)
        {
            SetLineActive(_authoredRangeLine, active);
            SetLineActive(_toleranceExtensionLine, active);
            SetLineActive(_groundEnvelope, active && _lastMode != BasicAttackMode.Firearm);
            SetLineActive(_targetRing, active && _hasSelectedTarget);
        }

        private static void SetLineActive(LineRenderer line, bool active)
        {
            if (line != null && line.gameObject.activeSelf != active)
                line.gameObject.SetActive(active);
        }

        private void OnGUI()
        {
#if !UNITY_SERVER
            if (!visible || !showStatusPanel || _ui == null || !_ui.GameplaySessionActive ||
                _lastRange <= 0f)
                return;

            string contact = !_hasSelectedTarget
                ? "N/A"
                : (_selectedTargetInRange ? "YES" : "NO");
            string target = !_hasSelectedTarget ? "None selected" : _selectedTargetLabel;
            string modeDetail = _lastMode == BasicAttackMode.Firearm
                ? $"Precision aim pitch: {_lastPitch:0.0}°"
                : $"Swing arc: {CombatRangePolicy.SwingArcDegreesFor(_lastMode):0}° (server facing)";
            string text =
                $"COMBAT RANGE DEBUG [{toggleKey}]\n" +
                $"Mode: {_lastMode}\n" +
                $"Range cap: {_lastRange:0.00} m\n" +
                $"Body/contact tolerance: +{(_lastMode == BasicAttackMode.Firearm ? DirectCombatContactRadius : CombatRangePolicy.MeleeTargetBodyRadius):0.00} m\n" +
                $"Effective center limit: {_lastEffectiveRange:0.00} m\n" +
                $"{modeDetail}\n" +
                $"Target: {target}\n" +
                $"Target distance: {(_hasSelectedTarget ? _lastTargetDistance.ToString("0.00") + " m" : "--")}\n" +
                $"Body-edge distance: {(_hasSelectedTarget ? _lastTargetEdgeDistance.ToString("0.00") + " m" : "--")}\n" +
                $"CONTACT: {contact}\n" +
                "Local geometry mirror only; server LOS remains authoritative.";

            GUI.Box(new Rect(12f, 12f, 370f, 205f), text);
#endif
        }
    }
}
