#if !UNITY_SERVER
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Client.Presentation.Characters
{
    /// <summary>
    /// Client-only Population presentation compaction.
    ///
    /// Population is authored and assembled through the same modular Player appearance
    /// pipeline, then collapsed to one visible SkinnedMeshRenderer after the recipe has
    /// been fully applied. Source renderers stay disabled (not destroyed) so a rare later
    /// appearance/equipment change can reuse the established presenter and rebuild again.
    ///
    /// No server state, message, cache or gameplay authority lives here.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class PopulationSkinnedMeshCompactor : MonoBehaviour
    {
        private const string CombinedObjectName = "__PopulationCombinedSkinnedMesh";

        private SkinnedMeshRenderer _combinedRenderer;
        private Mesh _combinedMesh;

        public bool Rebuild()
        {
            EnsureCombinedRenderer();
            if (_combinedRenderer == null)
                return false;

            // A presenter update intentionally re-enables the currently selected modular
            // sources. Hide the previous combined result while the replacement is built.
            _combinedRenderer.enabled = false;

            SkinnedMeshRenderer[] all = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var sources = new List<SkinnedMeshRenderer>(all.Length);
            for (int i = 0; i < all.Length; ++i)
            {
                SkinnedMeshRenderer renderer = all[i];
                if (renderer == null || renderer == _combinedRenderer ||
                    !renderer.enabled || renderer.sharedMesh == null)
                    continue;
                sources.Add(renderer);
            }

            if (sources.Count == 0)
            {
                // If no modular source was re-enabled, retain a previously valid compacted
                // result rather than making the actor disappear.
                if (_combinedMesh != null)
                {
                    _combinedRenderer.enabled = true;
                    return true;
                }
                return false;
            }

            if (sources.Count == 1)
            {
                // Already satisfies the hard one-visible-SMR Population rule. Avoid creating
                // a redundant mesh copy when the selected appearance naturally resolves to one.
                DestroyCombinedMesh();
                return true;
            }

            // Runtime mesh access is required to preserve skinning and the already-resolved
            // Player blend result. Fail safe to the modular sources when an imported asset is
            // not readable; never replace a valid actor with a partial combined mesh.
            for (int i = 0; i < sources.Count; ++i)
            {
                Mesh mesh = sources[i].sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    DestroyCombinedMesh();
                    return false;
                }
            }

            if (!TryBuildCombinedMesh(sources, out Mesh meshResult, out BuildResult build))
            {
                if (meshResult != null)
                    Destroy(meshResult);
                DestroyCombinedMesh();
                return false;
            }

            Mesh previousMesh = _combinedMesh;
            _combinedMesh = meshResult;

            _combinedRenderer.sharedMesh = _combinedMesh;
            _combinedRenderer.sharedMaterials = build.materials.ToArray();
            _combinedRenderer.bones = build.bones.ToArray();
            _combinedRenderer.rootBone = build.rootBone;
            _combinedRenderer.localBounds = build.bounds;
            _combinedRenderer.updateWhenOffscreen = false;
            _combinedRenderer.shadowCastingMode = build.shadowCastingMode;
            _combinedRenderer.receiveShadows = build.receiveShadows;
            _combinedRenderer.quality = build.quality;

            for (int i = 0; i < build.propertyBlocks.Count; ++i)
                _combinedRenderer.SetPropertyBlock(build.propertyBlocks[i], i);

            _combinedRenderer.enabled = true;

            // There must be exactly one visible skinned renderer for Population after a
            // successful build. Keep the modular components only as dormant rebuild inputs.
            for (int i = 0; i < sources.Count; ++i)
                sources[i].enabled = false;

            if (previousMesh != null && previousMesh != _combinedMesh)
                Destroy(previousMesh);

            return true;
        }

        private void EnsureCombinedRenderer()
        {
            if (_combinedRenderer != null)
                return;

            Transform child = transform.Find(CombinedObjectName);
            GameObject go;
            if (child != null)
            {
                go = child.gameObject;
            }
            else
            {
                go = new GameObject(CombinedObjectName);
                go.transform.SetParent(transform, false);
            }

            _combinedRenderer = go.GetComponent<SkinnedMeshRenderer>();
            if (_combinedRenderer == null)
                _combinedRenderer = go.AddComponent<SkinnedMeshRenderer>();
            _combinedRenderer.enabled = false;
            _combinedRenderer.updateWhenOffscreen = false;
        }

        private bool TryBuildCombinedMesh(
            List<SkinnedMeshRenderer> sources,
            out Mesh result,
            out BuildResult build)
        {
            result = null;
            build = new BuildResult();

            Transform target = _combinedRenderer.transform;
            var boneIndexByTransform = new Dictionary<Transform, int>();
            for (int i = 0; i < sources.Count; ++i)
            {
                SkinnedMeshRenderer source = sources[i];
                Transform[] sourceBones = source.bones ?? Array.Empty<Transform>();
                for (int b = 0; b < sourceBones.Length; ++b)
                {
                    Transform bone = sourceBones[b];
                    if (bone == null || boneIndexByTransform.ContainsKey(bone))
                        continue;
                    boneIndexByTransform.Add(bone, build.bones.Count);
                    build.bones.Add(bone);
                }

                if (build.rootBone == null && source.rootBone != null)
                    build.rootBone = source.rootBone;
                build.receiveShadows |= source.receiveShadows;
                if (i == 0)
                {
                    build.shadowCastingMode = source.shadowCastingMode;
                    build.quality = source.quality;
                }
            }

            if (build.bones.Count == 0)
                return false;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Color32>();
            var boneWeights = new List<BoneWeight>();
            var uvChannels = new List<Vector4>[8];
            var anyUv = new bool[8];
            for (int channel = 0; channel < uvChannels.Length; ++channel)
                uvChannels[channel] = new List<Vector4>();

            bool anyColors = false;
            bool missingNormals = false;
            bool missingTangents = false;
            bool hasBounds = false;
            Bounds combinedBounds = default(Bounds);

            for (int sourceIndex = 0; sourceIndex < sources.Count; ++sourceIndex)
            {
                SkinnedMeshRenderer source = sources[sourceIndex];
                Mesh mesh = source.sharedMesh;
                int vertexCount = mesh.vertexCount;
                if (vertexCount <= 0)
                    continue;

                int vertexOffset = vertices.Count;
                Vector3[] sourceVertices = mesh.vertices;
                Vector3[] sourceNormals = mesh.normals;
                Vector4[] sourceTangents = mesh.tangents;
                BoneWeight[] sourceBoneWeights = mesh.boneWeights;
                if (sourceVertices == null || sourceVertices.Length != vertexCount ||
                    sourceBoneWeights == null || sourceBoneWeights.Length != vertexCount)
                    return false;

                bool hasNormals = sourceNormals != null && sourceNormals.Length == vertexCount;
                bool hasTangents = sourceTangents != null && sourceTangents.Length == vertexCount;
                missingNormals |= !hasNormals;
                missingTangents |= !hasTangents;

                // Population blend channels were already synchronized by the Player presenter.
                // Bake those fixed per-actor weights into the base vertex data before combine;
                // the single runtime mesh no longer needs duplicate blendshape channels.
                ApplyCurrentBlendShapes(
                    source,
                    mesh,
                    sourceVertices,
                    hasNormals ? sourceNormals : null,
                    hasTangents ? sourceTangents : null);

                Matrix4x4 sourceToTarget = target.worldToLocalMatrix * source.transform.localToWorldMatrix;
                Matrix4x4 normalMatrix = sourceToTarget.inverse.transpose;

                for (int v = 0; v < vertexCount; ++v)
                {
                    vertices.Add(sourceToTarget.MultiplyPoint3x4(sourceVertices[v]));

                    if (hasNormals)
                        normals.Add(normalMatrix.MultiplyVector(sourceNormals[v]).normalized);
                    else
                        normals.Add(Vector3.up);

                    if (hasTangents)
                    {
                        Vector4 tangent = sourceTangents[v];
                        Vector3 direction = sourceToTarget.MultiplyVector(
                            new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
                        tangents.Add(new Vector4(direction.x, direction.y, direction.z, tangent.w));
                    }
                    else
                    {
                        tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    }

                    if (!TryRemapBoneWeight(
                            sourceBoneWeights[v], source.bones, boneIndexByTransform, out BoneWeight remapped))
                        return false;
                    boneWeights.Add(remapped);
                }

                CopyUvChannels(mesh, vertexCount, uvChannels, anyUv);

                Color32[] sourceColors = mesh.colors32;
                bool hasColors = sourceColors != null && sourceColors.Length == vertexCount;
                anyColors |= hasColors;
                for (int v = 0; v < vertexCount; ++v)
                    colors.Add(hasColors ? sourceColors[v] : new Color32(255, 255, 255, 255));

                int subMeshCount = mesh.subMeshCount;
                if (subMeshCount <= 0)
                    return false;
                Material[] sourceMaterials = source.sharedMaterials ?? Array.Empty<Material>();
                for (int subMesh = 0; subMesh < subMeshCount; ++subMesh)
                {
                    int[] triangles = mesh.GetTriangles(subMesh);
                    for (int t = 0; t < triangles.Length; ++t)
                        triangles[t] += vertexOffset;
                    build.triangles.Add(triangles);

                    int materialIndex = sourceMaterials.Length > 0
                        ? Math.Min(subMesh, sourceMaterials.Length - 1)
                        : -1;
                    build.materials.Add(materialIndex >= 0 ? sourceMaterials[materialIndex] : null);

                    var block = new MaterialPropertyBlock();
                    if (materialIndex >= 0)
                        source.GetPropertyBlock(block, materialIndex);
                    build.propertyBlocks.Add(block);
                }

                EncapsulateRendererBounds(source, target, ref combinedBounds, ref hasBounds);
            }

            if (vertices.Count == 0 || vertices.Count != boneWeights.Count || build.triangles.Count == 0)
                return false;

            var combined = new Mesh
            {
                name = "PopulationCombinedRuntime",
                indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };

            combined.SetVertices(vertices);
            combined.SetNormals(normals);
            combined.SetTangents(tangents);
            if (anyColors)
                combined.SetColors(colors);
            for (int channel = 0; channel < uvChannels.Length; ++channel)
            {
                if (anyUv[channel])
                    combined.SetUVs(channel, uvChannels[channel]);
            }

            combined.boneWeights = boneWeights.ToArray();
            Matrix4x4[] bindPoses = new Matrix4x4[build.bones.Count];
            Matrix4x4 targetLocalToWorld = target.localToWorldMatrix;
            for (int i = 0; i < build.bones.Count; ++i)
                bindPoses[i] = build.bones[i].worldToLocalMatrix * targetLocalToWorld;
            combined.bindposes = bindPoses;

            combined.subMeshCount = build.triangles.Count;
            for (int subMesh = 0; subMesh < build.triangles.Count; ++subMesh)
                combined.SetTriangles(build.triangles[subMesh], subMesh, false);

            if (missingNormals)
                combined.RecalculateNormals();
            if (missingTangents && anyUv[0])
                combined.RecalculateTangents();
            combined.RecalculateBounds();

            build.bounds = hasBounds ? combinedBounds : combined.bounds;
            result = combined;
            return true;
        }

        private static void CopyUvChannels(
            Mesh mesh,
            int vertexCount,
            List<Vector4>[] destination,
            bool[] anyUv)
        {
            for (int channel = 0; channel < destination.Length; ++channel)
            {
                var source = new List<Vector4>(vertexCount);
                mesh.GetUVs(channel, source);
                bool hasChannel = source.Count == vertexCount;
                anyUv[channel] |= hasChannel;
                if (hasChannel)
                {
                    destination[channel].AddRange(source);
                }
                else
                {
                    for (int i = 0; i < vertexCount; ++i)
                        destination[channel].Add(Vector4.zero);
                }
            }
        }

        private static bool TryRemapBoneWeight(
            BoneWeight source,
            Transform[] sourceBones,
            Dictionary<Transform, int> boneIndexByTransform,
            out BoneWeight remapped)
        {
            remapped = source;
            if (!TryRemapBoneIndex(source.boneIndex0, sourceBones, boneIndexByTransform, out int boneIndex0) ||
                !TryRemapBoneIndex(source.boneIndex1, sourceBones, boneIndexByTransform, out int boneIndex1) ||
                !TryRemapBoneIndex(source.boneIndex2, sourceBones, boneIndexByTransform, out int boneIndex2) ||
                !TryRemapBoneIndex(source.boneIndex3, sourceBones, boneIndexByTransform, out int boneIndex3))
                return false;

            remapped.boneIndex0 = boneIndex0;
            remapped.boneIndex1 = boneIndex1;
            remapped.boneIndex2 = boneIndex2;
            remapped.boneIndex3 = boneIndex3;
            return true;
        }

        private static bool TryRemapBoneIndex(
            int sourceIndex,
            Transform[] sourceBones,
            Dictionary<Transform, int> boneIndexByTransform,
            out int remapped)
        {
            remapped = 0;
            if (sourceBones == null || sourceBones.Length == 0)
                return false;
            if (sourceIndex < 0 || sourceIndex >= sourceBones.Length)
                return false;
            Transform bone = sourceBones[sourceIndex];
            return bone != null && boneIndexByTransform.TryGetValue(bone, out remapped);
        }

        private static void ApplyCurrentBlendShapes(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents)
        {
            int shapeCount = mesh.blendShapeCount;
            if (shapeCount <= 0)
                return;

            int vertexCount = mesh.vertexCount;
            // Unity's blend-frame API expects vertex-sized arrays. Allocate all three even
            // when the source mesh has no authored normal/tangent stream, then simply ignore
            // those deltas below when the corresponding destination is absent.
            var deltaVerticesA = new Vector3[vertexCount];
            var deltaNormalsA = new Vector3[vertexCount];
            var deltaTangentsA = new Vector3[vertexCount];
            var deltaVerticesB = new Vector3[vertexCount];
            var deltaNormalsB = new Vector3[vertexCount];
            var deltaTangentsB = new Vector3[vertexCount];

            for (int shape = 0; shape < shapeCount; ++shape)
            {
                float weight = renderer.GetBlendShapeWeight(shape);
                if (Mathf.Abs(weight) <= 0.0001f)
                    continue;

                int frameCount = mesh.GetBlendShapeFrameCount(shape);
                if (frameCount <= 0)
                    continue;

                int lowerFrame = -1;
                int upperFrame = -1;
                float lowerWeight = 0f;
                float upperWeight = 0f;
                for (int frame = 0; frame < frameCount; ++frame)
                {
                    float frameWeight = mesh.GetBlendShapeFrameWeight(shape, frame);
                    if (frameWeight <= weight)
                    {
                        lowerFrame = frame;
                        lowerWeight = frameWeight;
                    }
                    if (frameWeight >= weight)
                    {
                        upperFrame = frame;
                        upperWeight = frameWeight;
                        break;
                    }
                }

                if (lowerFrame < 0)
                {
                    // Interpolate/extrapolate from the implicit zero-weight base to the first frame.
                    upperFrame = 0;
                    upperWeight = mesh.GetBlendShapeFrameWeight(shape, 0);
                    float factor = Mathf.Abs(upperWeight) > 0.0001f ? weight / upperWeight : 0f;
                    ReadBlendFrame(mesh, shape, upperFrame, deltaVerticesA, deltaNormalsA, deltaTangentsA);
                    AccumulateBlendFrame(vertices, normals, tangents,
                        deltaVerticesA, deltaNormalsA, deltaTangentsA, factor);
                    continue;
                }

                if (upperFrame < 0)
                {
                    // Extrapolate from the last authored frame.
                    float factor = Mathf.Abs(lowerWeight) > 0.0001f ? weight / lowerWeight : 1f;
                    ReadBlendFrame(mesh, shape, lowerFrame, deltaVerticesA, deltaNormalsA, deltaTangentsA);
                    AccumulateBlendFrame(vertices, normals, tangents,
                        deltaVerticesA, deltaNormalsA, deltaTangentsA, factor);
                    continue;
                }

                if (lowerFrame == upperFrame || Mathf.Abs(upperWeight - lowerWeight) <= 0.0001f)
                {
                    ReadBlendFrame(mesh, shape, lowerFrame, deltaVerticesA, deltaNormalsA, deltaTangentsA);
                    AccumulateBlendFrame(vertices, normals, tangents,
                        deltaVerticesA, deltaNormalsA, deltaTangentsA, 1f);
                    continue;
                }

                ReadBlendFrame(mesh, shape, lowerFrame, deltaVerticesA, deltaNormalsA, deltaTangentsA);
                ReadBlendFrame(mesh, shape, upperFrame, deltaVerticesB, deltaNormalsB, deltaTangentsB);
                float t = Mathf.InverseLerp(lowerWeight, upperWeight, weight);
                AccumulateInterpolatedBlendFrames(vertices, normals, tangents,
                    deltaVerticesA, deltaNormalsA, deltaTangentsA,
                    deltaVerticesB, deltaNormalsB, deltaTangentsB,
                    t);
            }
        }

        private static void ReadBlendFrame(
            Mesh mesh,
            int shape,
            int frame,
            Vector3[] deltaVertices,
            Vector3[] deltaNormals,
            Vector3[] deltaTangents)
        {
            Array.Clear(deltaVertices, 0, deltaVertices.Length);
            Array.Clear(deltaNormals, 0, deltaNormals.Length);
            Array.Clear(deltaTangents, 0, deltaTangents.Length);
            mesh.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
        }

        private static void AccumulateBlendFrame(
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Vector3[] deltaVertices,
            Vector3[] deltaNormals,
            Vector3[] deltaTangents,
            float factor)
        {
            for (int i = 0; i < vertices.Length; ++i)
            {
                vertices[i] += deltaVertices[i] * factor;
                if (normals != null && deltaNormals != null)
                    normals[i] += deltaNormals[i] * factor;
                if (tangents != null && deltaTangents != null)
                {
                    Vector3 tangent = new Vector3(tangents[i].x, tangents[i].y, tangents[i].z) +
                                      deltaTangents[i] * factor;
                    tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[i].w);
                }
            }
        }

        private static void AccumulateInterpolatedBlendFrames(
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Vector3[] deltaVerticesA,
            Vector3[] deltaNormalsA,
            Vector3[] deltaTangentsA,
            Vector3[] deltaVerticesB,
            Vector3[] deltaNormalsB,
            Vector3[] deltaTangentsB,
            float t)
        {
            for (int i = 0; i < vertices.Length; ++i)
            {
                vertices[i] += Vector3.LerpUnclamped(deltaVerticesA[i], deltaVerticesB[i], t);
                if (normals != null && deltaNormalsA != null && deltaNormalsB != null)
                    normals[i] += Vector3.LerpUnclamped(deltaNormalsA[i], deltaNormalsB[i], t);
                if (tangents != null && deltaTangentsA != null && deltaTangentsB != null)
                {
                    Vector3 tangent = new Vector3(tangents[i].x, tangents[i].y, tangents[i].z) +
                                      Vector3.LerpUnclamped(deltaTangentsA[i], deltaTangentsB[i], t);
                    tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[i].w);
                }
            }
        }

        private static void EncapsulateRendererBounds(
            SkinnedMeshRenderer source,
            Transform target,
            ref Bounds combined,
            ref bool hasBounds)
        {
            Bounds bounds = source.localBounds;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            Matrix4x4 sourceToTarget = target.worldToLocalMatrix * source.transform.localToWorldMatrix;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 local = center + Vector3.Scale(extents, new Vector3(x, y, z));
                        Vector3 point = sourceToTarget.MultiplyPoint3x4(local);
                        if (!hasBounds)
                        {
                            combined = new Bounds(point, Vector3.zero);
                            hasBounds = true;
                        }
                        else
                        {
                            combined.Encapsulate(point);
                        }
                    }
                }
            }
        }

        private void DestroyCombinedMesh()
        {
            if (_combinedRenderer != null)
            {
                _combinedRenderer.enabled = false;
                _combinedRenderer.sharedMesh = null;
                _combinedRenderer.sharedMaterials = Array.Empty<Material>();
            }

            if (_combinedMesh != null)
            {
                Destroy(_combinedMesh);
                _combinedMesh = null;
            }
        }

        private void OnDestroy()
        {
            if (_combinedMesh != null)
            {
                Destroy(_combinedMesh);
                _combinedMesh = null;
            }
        }

        private sealed class BuildResult
        {
            public readonly List<Transform> bones = new List<Transform>();
            public readonly List<Material> materials = new List<Material>();
            public readonly List<MaterialPropertyBlock> propertyBlocks = new List<MaterialPropertyBlock>();
            public readonly List<int[]> triangles = new List<int[]>();
            public Transform rootBone;
            public Bounds bounds;
            public ShadowCastingMode shadowCastingMode = ShadowCastingMode.On;
            public bool receiveShadows;
            public SkinQuality quality = SkinQuality.Auto;
        }
    }
}
#endif
