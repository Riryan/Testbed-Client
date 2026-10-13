using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Game.Shared.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Player.Client
{
    /// <summary>
    /// Read-only client query view over the client-safe Server World Bake V2 package.
    /// It is prediction data only: the standalone GameServer still independently validates
    /// every movement/interaction and remains the sole authority.
    /// </summary>
    public static class SharedWorldClient
    {
        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly int X;
            public readonly int Z;
            public CellKey(int x, int z) { X = x; Z = z; }
            public bool Equals(CellKey other) => X == other.X && Z == other.Z;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        private sealed class RuntimeWorld
        {
            private const float CellSize = 8f;
            private const float Epsilon = 0.0001f;

            private sealed class DynamicBlockerState
            {
                public ServerDynamicBlocker Definition;
                public bool Enabled;
            }

            private readonly ServerCollisionTriangle[] _support;
            private readonly ServerCollisionTriangle[] _collision;
            private readonly Dictionary<CellKey, List<int>> _supportGrid = new Dictionary<CellKey, List<int>>();
            private readonly Dictionary<CellKey, List<int>> _collisionGrid = new Dictionary<CellKey, List<int>>();
            private readonly Dictionary<long, DynamicBlockerState> _dynamicBlockers =
                new Dictionary<long, DynamicBlockerState>();
            private readonly Dictionary<CellKey, List<long>> _dynamicBlockerGrid =
                new Dictionary<CellKey, List<long>>();
            private readonly HashSet<int> _scratch = new HashSet<int>();
            private readonly HashSet<long> _dynamicBlockerScratch = new HashSet<long>();

            public SharedWorldSnapshot Snapshot { get; }

            public RuntimeWorld(SharedWorldSnapshot snapshot)
            {
                Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
                _support = snapshot.supportTriangles ?? Array.Empty<ServerCollisionTriangle>();
                _collision = snapshot.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
                BuildGrid(_support, _supportGrid);
                BuildGrid(_collision, _collisionGrid);

                ServerDynamicBlocker[] blockers =
                    snapshot.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>();
                for (int i = 0; i < blockers.Length; ++i)
                {
                    ServerDynamicBlocker blocker = blockers[i];
                    if (blocker == null || blocker.stableId <= 0)
                        continue;
                    _dynamicBlockers[blocker.stableId] = new DynamicBlockerState
                    {
                        Definition = blocker,
                        Enabled = blocker.enabledByDefault,
                    };
                    IndexDynamicBlocker(blocker);
                }
            }

            public bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
            {
                if (stableId <= 0 || !_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state))
                    return false;
                state.Enabled = enabled;
                return true;
            }

            // Same movement dimensions and walkability limit as the authoritative
            // CharacterMotorSettings. This client assembly cannot depend on Game.Server.
            private const float CapsuleHeight = 1.8f;
            private const float DefaultCapsuleRadius = 0.35f;
            private const float MaximumSlopeDegrees = 50f;

            public bool TryResolveGroundedMove(
                Vector3 start,
                Vector3 desiredDelta,
                float radius,
                float maximumStepUp,
                float maximumSnapDown,
                out Vector3 resolved,
                out bool blocked)
            {
                resolved = start;
                blocked = false;
                desiredDelta.y = 0f;
                float length = desiredDelta.magnitude;
                if (length <= 0.000001f)
                    return true;

                // Match the authoritative horizontal motor's bounded substeps and
                // its X/Z soft-slide decision rather than evaluating one full delta.
                float safeRadius = Mathf.Max(0.05f, radius);
                float stepUp = Mathf.Max(0.01f, maximumStepUp);
                float snapDown = Mathf.Max(0.01f, maximumSnapDown);
                float maxSubstep = Mathf.Max(0.05f, safeRadius * 0.5f);
                int steps = Mathf.Clamp(Mathf.CeilToInt(length / maxSubstep), 1, 16);
                float sx = desiredDelta.x / steps;
                float sz = desiredDelta.z / steps;
                Vector3 current = start;
                // Owner presentation can be grounded from the local scene physics
                // before the authoritative kinematic capsule has cleared a lower
                // stair/curb edge. Do not suppress otherwise legal movement merely
                // because that presentation-only feet Y clips the outgoing riser.
                // Recover a clear pose only from nearby authored higher support.
                if (!IsStandingCapsuleClear(current, safeRadius) &&
                    TryFindNearbyClearStepHeight(current, safeRadius, stepUp, out float correctedY))
                    current.y = correctedY;

                for (int i = 0; i < steps; ++i)
                {
                    if (TryGroundedCandidate(current, sx, sz, safeRadius, stepUp, snapDown,
                            out Vector3 candidate))
                    {
                        current = candidate;
                        continue;
                    }

                    bool canX = TryGroundedCandidate(current, sx, 0f, safeRadius, stepUp,
                        snapDown, out Vector3 xOnly);
                    bool canZ = TryGroundedCandidate(current, 0f, sz, safeRadius, stepUp,
                        snapDown, out Vector3 zOnly);
                    if (canX && (!canZ || Mathf.Abs(sx) >= Mathf.Abs(sz)))
                        current = xOnly;
                    else if (canZ)
                        current = zOnly;
                    else
                        blocked = true;
                }

                resolved = current;
                return true;
            }

            private bool TryFindNearbyClearStepHeight(
                Vector3 current, float radius, float stepHeight, out float raisedY)
            {
                raisedY = current.y;
                float probe = Mathf.Max(0.05f, stepHeight + 0.05f);
                float bestY = current.y;
                for (int i = 0; i < 4; ++i)
                {
                    float x = current.x + (i == 0 ? radius : i == 1 ? -radius : 0f);
                    float z = current.z + (i == 2 ? radius : i == 3 ? -radius : 0f);
                    if (!TryFindGroundAtPoint(x, z, current.y, probe, probe,
                            out float y) ||
                        y <= current.y + 0.01f ||
                        y > current.y + stepHeight + 0.05f ||
                        y <= bestY)
                        continue;
                    if (IsStandingCapsuleClear(new Vector3(current.x, y, current.z), radius))
                        bestY = y;
                }
                if (bestY <= current.y + 0.01f)
                    return false;
                raisedY = bestY;
                return true;
            }

            private bool TryGroundedCandidate(
                Vector3 start, float deltaX, float deltaZ,
                float radius, float stepHeight, float groundSnapDistance,
                out Vector3 resolved)
            {
                resolved = start;
                if (Mathf.Abs(deltaX) <= 0.000001f && Mathf.Abs(deltaZ) <= 0.000001f)
                    return false;

                Vector3 target = new Vector3(start.x + deltaX, start.y, start.z + deltaZ);
                float up = Mathf.Max(0.05f, stepHeight + 0.05f);
                float down = Mathf.Max(0.05f, groundSnapDistance);
                if (!TryFindGroundFootprint(target.x, target.z, start.y, radius,
                        up, down, out float groundY))
                    return false;

                if (groundY > start.y + stepHeight + 0.05f ||
                    groundY < start.y - groundSnapDistance - 0.05f)
                    return false;

                Vector3 standing = new Vector3(target.x, groundY, target.z);
                if (IsStandingCapsuleClear(standing, radius))
                {
                    if (groundY < start.y - 0.01f &&
                        IsStandingCapsuleClear(target, radius) &&
                        HasWalkableTreadAhead(target, deltaX, deltaZ, start.y,
                            radius, stepHeight, groundY))
                    {
                        resolved = target;
                        return true;
                    }

                    if (groundY > start.y + 0.01f &&
                        !IsStepElevationClear(start, groundY, radius))
                        return false;

                    resolved = standing;
                    return true;
                }

                // Retain height for a legitimate step down until the exposed riser
                // edge is no longer inside the lower capsule hemisphere.
                if (groundY < start.y - 0.01f && IsStandingCapsuleClear(target, radius))
                {
                    resolved = target;
                    return true;
                }

                // Probe a real next tread one radius ahead. This allows ascent before
                // the center touches the next step, without climbing bare walls.
                float length = Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
                if (length > 0.000001f)
                {
                    float leadX = target.x + deltaX / length * radius;
                    float leadZ = target.z + deltaZ / length * radius;
                    if (TryFindGroundAtPoint(leadX, leadZ, start.y, up, down,
                            out float leadY) &&
                        leadY > groundY + 0.01f &&
                        leadY >= start.y - 0.01f &&
                        leadY <= start.y + stepHeight + 0.05f)
                    {
                        Vector3 raisedEnd = new Vector3(target.x, leadY, target.z);
                        if (IsStepElevationClear(start, leadY, radius) &&
                            IsStandingCapsuleClear(raisedEnd, radius))
                        {
                            resolved = raisedEnd;
                            return true;
                        }
                    }
                }
                return false;
            }

            private bool IsStepElevationClear(Vector3 start, float raisedY, float radius)
            {
                float rise = raisedY - start.y;
                if (rise < 0f)
                    return false;
                float unit = Mathf.Max(0.05f, radius * 0.5f);
                int samples = Mathf.Clamp(Mathf.CeilToInt(rise / unit), 1, 12);
                for (int i = 1; i <= samples; ++i)
                {
                    float y = start.y + rise * i / samples;
                    if (!IsStandingCapsuleClear(new Vector3(start.x, y, start.z), radius))
                        return false;
                }
                return true;
            }

            private bool HasWalkableTreadAhead(
                Vector3 desired, float dx, float dz, float currentY,
                float radius, float stepHeight, float centerGroundY)
            {
                float length = Mathf.Sqrt(dx * dx + dz * dz);
                if (length <= 0.000001f)
                    return false;
                float x = desired.x + dx / length * radius;
                float z = desired.z + dz / length * radius;
                return TryFindGroundAtPoint(x, z, currentY,
                           Mathf.Max(0.05f, stepHeight + 0.05f),
                           Mathf.Max(0.05f, stepHeight + 0.05f), out float aheadY) &&
                       aheadY > centerGroundY + 0.01f &&
                       aheadY >= currentY - 0.01f &&
                       aheadY <= currentY + stepHeight + 0.05f;
            }

            private bool TryFindGroundAtPoint(
                float x, float z, float feetY,
                float probeUp, float probeDown, out float height)
            {
                Query(_supportGrid, x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f);
                return TryGroundAtPointFromCandidates(x, z, feetY, probeUp,
                    probeDown, Mathf.Cos(MaximumSlopeDegrees * Mathf.Deg2Rad), out height);
            }

            public bool TryFindSupportHeight(
                float x, float z, float referenceY,
                float probeUp, float probeDown, out float y)
            {
                // Also used by owner presentation without a movement delta.
                // Use the same five-point footprint and slope constraints.
                return TryFindGroundFootprint(x, z, referenceY,
                    DefaultCapsuleRadius, probeUp, probeDown, out y);
            }

            private bool TryFindGroundFootprint(
                float x, float z, float feetY, float radius,
                float probeUp, float probeDown, out float groundY)
            {
                groundY = feetY;
                float ring = Mathf.Max(0f, radius) * 0.6f;
                float minNormalY = Mathf.Cos(MaximumSlopeDegrees * Mathf.Deg2Rad);
                float extent = ring + 0.01f;
                Query(_supportGrid, x - extent, z - extent, x + extent, z + extent);

                bool centerSupported = false;
                float centerY = float.NegativeInfinity;
                int supportCount = 0;
                float bestY = float.NegativeInfinity;
                for (int sample = 0; sample < 5; ++sample)
                {
                    float px = x + (sample == 1 ? ring : sample == 2 ? -ring : 0f);
                    float pz = z + (sample == 3 ? ring : sample == 4 ? -ring : 0f);
                    if (!TryGroundAtPointFromCandidates(px, pz, feetY,
                            probeUp, probeDown, minNormalY, out float candidateY))
                        continue;
                    supportCount++;
                    if (sample == 0)
                    {
                        centerSupported = true;
                        centerY = candidateY;
                    }
                    if (candidateY > bestY)
                        bestY = candidateY;
                }

                if (!centerSupported && supportCount < 2)
                    return false;
                groundY = centerSupported ? centerY : bestY;
                return true;
            }

            private bool TryGroundAtPointFromCandidates(
                float x, float z, float feetY,
                float probeUp, float probeDown, float minNormalY, out float bestY)
            {
                bestY = float.NegativeInfinity;
                bool found = false;
                foreach (int index in _scratch)
                {
                    ServerCollisionTriangle t = _support[index];
                    if (t.normalY < minNormalY ||
                        !TryHeightAtXZ(t, x, z, out float candidate))
                        continue;
                    if (candidate > feetY + Mathf.Max(0f, probeUp) ||
                        candidate < feetY - Mathf.Max(0f, probeDown))
                        continue;
                    if (!found || candidate > bestY)
                    {
                        found = true;
                        bestY = candidate;
                    }
                }
                return found;
            }

            private bool IsStandingCapsuleClear(Vector3 feet, float radius)
            {
                // This matches the server's standing clearance check, including
                // skipping only the low supporting floor (not walls or ceilings).
                float bottomY = feet.y + radius;
                float topY = feet.y + Mathf.Max(radius * 2f, CapsuleHeight) - radius;
                float floorNormalY = Mathf.Cos(MaximumSlopeDegrees * Mathf.Deg2Rad);
                Query(_collisionGrid, feet.x - radius, feet.z - radius,
                    feet.x + radius, feet.z + radius);
                foreach (int index in _scratch)
                {
                    ServerCollisionTriangle t = _collision[index];
                    // Match the GameServer: long ramps are classified by the
                    // local footprint height, never the remote high vertex.
                    if (t.normalY >= floorNormalY &&
                        (Mathf.Max(t.ay, Mathf.Max(t.by, t.cy)) <= feet.y + radius * 1.5f ||
                         (TryClosestTriangleHeightAtXZ(t, feet.x, feet.z, out float localFloorY) &&
                          localFloorY <= feet.y + radius * 1.5f)))
                        continue;
                    float distanceSquared = SegmentTriangleDistanceSquared(
                        feet.x, bottomY, feet.z, feet.x, topY, feet.z, t);
                    if (distanceSquared < radius * radius - 0.000001f)
                        return false;
                }

                QueryDynamicBlockers(feet.x - radius, feet.z - radius,
                    feet.x + radius, feet.z + radius);
                foreach (long stableId in _dynamicBlockerScratch)
                {
                    if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                        state.Enabled && CapsuleOverlapsOrientedBox(feet, radius, state.Definition))
                        return false;
                }
                return true;
            }

            private static bool CapsuleOverlapsOrientedBox(
                Vector3 feet, float radius, ServerDynamicBlocker box)
            {
                float yaw = -box.pose.yaw * Mathf.Deg2Rad;
                float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
                float dx = feet.x - box.pose.x, dz = feet.z - box.pose.z;
                float localX = dx * cos - dz * sin;
                float localZ = dx * sin + dz * cos;
                float halfX = Mathf.Max(0.01f, box.sizeX * 0.5f);
                float halfY = Mathf.Max(0.01f, box.sizeY * 0.5f);
                float halfZ = Mathf.Max(0.01f, box.sizeZ * 0.5f);
                float nearestX = Mathf.Clamp(localX, -halfX, halfX);
                float nearestZ = Mathf.Clamp(localZ, -halfZ, halfZ);
                float horizontalDx = localX - nearestX;
                float horizontalDz = localZ - nearestZ;
                if (horizontalDx * horizontalDx + horizontalDz * horizontalDz >= radius * radius)
                    return false;
                float capsuleHeight = Mathf.Max(radius * 2f, CapsuleHeight);
                return feet.y + capsuleHeight > box.pose.y - halfY &&
                       feet.y < box.pose.y + halfY;
            }

            private static float SegmentTriangleDistanceSquared(
                float p0x, float p0y, float p0z,
                float p1x, float p1y, float p1z,
                ServerCollisionTriangle t)
            {
                if (SegmentIntersectsTriangle(p0x, p0y, p0z, p1x, p1y, p1z, t))
                    return 0f;

                float best = PointTriangleDistanceSquared(p0x, p0y, p0z, t);
                best = Math.Min(best, PointTriangleDistanceSquared(p1x, p1y, p1z, t));
                best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.ax, t.ay, t.az, t.bx, t.by, t.bz));
                best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.bx, t.by, t.bz, t.cx, t.cy, t.cz));
                best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.cx, t.cy, t.cz, t.ax, t.ay, t.az));
                return best;
            }

            private static bool SegmentIntersectsTriangle(float p0x, float p0y, float p0z, float p1x, float p1y, float p1z, ServerCollisionTriangle t)
            {
                float dx = p1x - p0x, dy = p1y - p0y, dz = p1z - p0z;
                float e1x = t.bx - t.ax, e1y = t.by - t.ay, e1z = t.bz - t.az;
                float e2x = t.cx - t.ax, e2y = t.cy - t.ay, e2z = t.cz - t.az;
                float hx = dy * e2z - dz * e2y;
                float hy = dz * e2x - dx * e2z;
                float hz = dx * e2y - dy * e2x;
                float a = e1x * hx + e1y * hy + e1z * hz;
                if (Math.Abs(a) < 0.0000001f) return false;
                float f = 1f / a;
                float sx = p0x - t.ax, sy = p0y - t.ay, sz = p0z - t.az;
                float u = f * (sx * hx + sy * hy + sz * hz);
                if (u < 0f || u > 1f) return false;
                float qx = sy * e1z - sz * e1y;
                float qy = sz * e1x - sx * e1z;
                float qz = sx * e1y - sy * e1x;
                float v = f * (dx * qx + dy * qy + dz * qz);
                if (v < 0f || u + v > 1f) return false;
                float along = f * (e2x * qx + e2y * qy + e2z * qz);
                return along >= 0f && along <= 1f;
            }

            // Ericson, Real-Time Collision Detection: closest point on triangle.
            private static float PointTriangleDistanceSquared(float px, float py, float pz, ServerCollisionTriangle t)
            {
                float ax=t.ax, ay=t.ay, az=t.az, bx=t.bx, by=t.by, bz=t.bz, cx=t.cx, cy=t.cy, cz=t.cz;
                float abx=bx-ax, aby=by-ay, abz=bz-az, acx=cx-ax, acy=cy-ay, acz=cz-az, apx=px-ax, apy=py-ay, apz=pz-az;
                float d1=abx*apx+aby*apy+abz*apz, d2=acx*apx+acy*apy+acz*apz;
                if (d1<=0f && d2<=0f) return DistSq(px,py,pz,ax,ay,az);
                float bpx=px-bx,bpy=py-by,bpz=pz-bz, d3=abx*bpx+aby*bpy+abz*bpz, d4=acx*bpx+acy*bpy+acz*bpz;
                if (d3>=0f && d4<=d3) return DistSq(px,py,pz,bx,by,bz);
                float vc=d1*d4-d3*d2;
                if (vc<=0f && d1>=0f && d3<=0f) { float v=d1/(d1-d3); return DistSq(px,py,pz,ax+v*abx,ay+v*aby,az+v*abz); }
                float cpx=px-cx,cpy=py-cy,cpz=pz-cz, d5=abx*cpx+aby*cpy+abz*cpz, d6=acx*cpx+acy*cpy+acz*cpz;
                if (d6>=0f && d5<=d6) return DistSq(px,py,pz,cx,cy,cz);
                float vb=d5*d2-d1*d6;
                if (vb<=0f && d2>=0f && d6<=0f) { float w=d2/(d2-d6); return DistSq(px,py,pz,ax+w*acx,ay+w*acy,az+w*acz); }
                float va=d3*d6-d5*d4;
                if (va<=0f && (d4-d3)>=0f && (d5-d6)>=0f) { float w=(d4-d3)/((d4-d3)+(d5-d6)); return DistSq(px,py,pz,bx+w*(cx-bx),by+w*(cy-by),bz+w*(cz-bz)); }
                float denom=1f/(va+vb+vc); float v2=vb*denom,w2=vc*denom;
                return DistSq(px,py,pz,ax+abx*v2+acx*w2,ay+aby*v2+acy*w2,az+abz*v2+acz*w2);
            }

            private static float SegmentSegmentDistanceSquared(
                float p1x,float p1y,float p1z,float q1x,float q1y,float q1z,
                float p2x,float p2y,float p2z,float q2x,float q2y,float q2z)
            {
                float d1x=q1x-p1x,d1y=q1y-p1y,d1z=q1z-p1z,d2x=q2x-p2x,d2y=q2y-p2y,d2z=q2z-p2z,rx=p1x-p2x,ry=p1y-p2y,rz=p1z-p2z;
                float a=d1x*d1x+d1y*d1y+d1z*d1z,e=d2x*d2x+d2y*d2y+d2z*d2z,f=d2x*rx+d2y*ry+d2z*rz;
                float s,t;
                if (a<=0.0000001f && e<=0.0000001f) return DistSq(p1x,p1y,p1z,p2x,p2y,p2z);
                if (a<=0.0000001f) { s=0f; t=Mathf.Clamp(f/e,0f,1f); }
                else
                {
                    float c=d1x*rx+d1y*ry+d1z*rz;
                    if (e<=0.0000001f) { t=0f; s=Mathf.Clamp(-c/a,0f,1f); }
                    else
                    {
                        float b=d1x*d2x+d1y*d2y+d1z*d2z,den=a*e-b*b;
                        s=den!=0f?Mathf.Clamp((b*f-c*e)/den,0f,1f):0f;
                        t=(b*s+f)/e;
                        if (t<0f) { t=0f; s=Mathf.Clamp(-c/a,0f,1f); }
                        else if (t>1f) { t=1f; s=Mathf.Clamp((b-c)/a,0f,1f); }
                    }
                }
                float c1x=p1x+d1x*s,c1y=p1y+d1y*s,c1z=p1z+d1z*s,c2x=p2x+d2x*t,c2y=p2y+d2y*t,c2z=p2z+d2z*t;
                return DistSq(c1x,c1y,c1z,c2x,c2y,c2z);
            }

            private static float DistSq(float ax,float ay,float az,float bx,float by,float bz)
            { float x=ax-bx,y=ay-by,z=az-bz; return x*x+y*y+z*z; }
            private static bool TryClosestTriangleHeightAtXZ(
                ServerCollisionTriangle t, float x, float z, out float y)
            {
                if (TryHeightAtXZ(t, x, z, out y))
                    return true;

                float bestDistance = float.PositiveInfinity;
                y = 0f;
                SampleClosestXZEdge(x, z, t.ax, t.ay, t.az,
                    t.bx, t.by, t.bz, ref bestDistance, ref y);
                SampleClosestXZEdge(x, z, t.bx, t.by, t.bz,
                    t.cx, t.cy, t.cz, ref bestDistance, ref y);
                SampleClosestXZEdge(x, z, t.cx, t.cy, t.cz,
                    t.ax, t.ay, t.az, ref bestDistance, ref y);
                return !float.IsNaN(y) && !float.IsInfinity(y) &&
                       bestDistance < float.PositiveInfinity;
            }

            private static void SampleClosestXZEdge(
                float px, float pz,
                float ax, float ay, float az,
                float bx, float by, float bz,
                ref float bestDistance, ref float nearestHeight)
            {
                float dx = bx - ax;
                float dz = bz - az;
                float lengthSquared = dx * dx + dz * dz;
                float t = lengthSquared > 0.00000001f
                    ? Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / lengthSquared)
                    : 0f;
                float nx = ax + dx * t - px;
                float nz = az + dz * t - pz;
                float squared = nx * nx + nz * nz;
                if (squared < bestDistance)
                {
                    bestDistance = squared;
                    nearestHeight = ay + (by - ay) * t;
                }
            }

            private static bool TryHeightAtXZ(ServerCollisionTriangle t, float x, float z, out float y)
            {
                float v0x = t.bx - t.ax, v0z = t.bz - t.az;
                float v1x = t.cx - t.ax, v1z = t.cz - t.az;
                float v2x = x - t.ax, v2z = z - t.az;
                float den = v0x * v1z - v1x * v0z;
                if (Mathf.Abs(den) <= 0.0000001f)
                {
                    y = 0f;
                    return false;
                }
                float inv = 1f / den;
                float u = (v2x * v1z - v1x * v2z) * inv;
                float v = (v0x * v2z - v2x * v0z) * inv;
                const float epsilon = 0.0005f;
                if (u < -epsilon || v < -epsilon || u + v > 1f + epsilon)
                {
                    y = 0f;
                    return false;
                }
                y = t.ay + u * (t.by - t.ay) + v * (t.cy - t.ay);
                return !float.IsNaN(y) && !float.IsInfinity(y);
            }

            private void IndexDynamicBlocker(ServerDynamicBlocker blocker)
            {
                float halfX = Mathf.Max(0.01f, blocker.sizeX * 0.5f);
                float halfZ = Mathf.Max(0.01f, blocker.sizeZ * 0.5f);
                float yaw = blocker.pose.yaw * Mathf.Deg2Rad;
                float cos = Mathf.Abs(Mathf.Cos(yaw));
                float sin = Mathf.Abs(Mathf.Sin(yaw));
                float extentX = cos * halfX + sin * halfZ;
                float extentZ = sin * halfX + cos * halfZ;

                int minCellX = Mathf.FloorToInt((blocker.pose.x - extentX) / CellSize);
                int maxCellX = Mathf.FloorToInt((blocker.pose.x + extentX) / CellSize);
                int minCellZ = Mathf.FloorToInt((blocker.pose.z - extentZ) / CellSize);
                int maxCellZ = Mathf.FloorToInt((blocker.pose.z + extentZ) / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        CellKey key = new CellKey(x, z);
                        if (!_dynamicBlockerGrid.TryGetValue(key, out List<long> list))
                        {
                            list = new List<long>(4);
                            _dynamicBlockerGrid.Add(key, list);
                        }
                        list.Add(blocker.stableId);
                    }
                }
            }

            private void QueryDynamicBlockers(float minX, float minZ, float maxX, float maxZ)
            {
                _dynamicBlockerScratch.Clear();
                int minCellX = Mathf.FloorToInt(minX / CellSize);
                int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        if (!_dynamicBlockerGrid.TryGetValue(new CellKey(x, z), out List<long> list))
                            continue;
                        for (int i = 0; i < list.Count; ++i)
                            _dynamicBlockerScratch.Add(list[i]);
                    }
                }
            }

            private static void BuildGrid(
                ServerCollisionTriangle[] triangles,
                Dictionary<CellKey, List<int>> grid)
            {
                for (int i = 0; i < triangles.Length; ++i)
                {
                    ServerCollisionTriangle t = triangles[i];
                    float minX = Mathf.Min(t.ax, Mathf.Min(t.bx, t.cx));
                    float maxX = Mathf.Max(t.ax, Mathf.Max(t.bx, t.cx));
                    float minZ = Mathf.Min(t.az, Mathf.Min(t.bz, t.cz));
                    float maxZ = Mathf.Max(t.az, Mathf.Max(t.bz, t.cz));
                    int minCellX = Mathf.FloorToInt(minX / CellSize);
                    int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                    int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                    int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);

                    for (int x = minCellX; x <= maxCellX; ++x)
                    {
                        for (int z = minCellZ; z <= maxCellZ; ++z)
                        {
                            CellKey key = new CellKey(x, z);
                            if (!grid.TryGetValue(key, out List<int> list))
                            {
                                list = new List<int>(8);
                                grid.Add(key, list);
                            }
                            list.Add(i);
                        }
                    }
                }
            }

            private void Query(
                Dictionary<CellKey, List<int>> grid,
                float minX,
                float minZ,
                float maxX,
                float maxZ)
            {
                _scratch.Clear();
                int minCellX = Mathf.FloorToInt(minX / CellSize);
                int maxCellX = Mathf.FloorToInt(maxX / CellSize);
                int minCellZ = Mathf.FloorToInt(minZ / CellSize);
                int maxCellZ = Mathf.FloorToInt(maxZ / CellSize);
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    for (int z = minCellZ; z <= maxCellZ; ++z)
                    {
                        if (!grid.TryGetValue(new CellKey(x, z), out List<int> list))
                            continue;
                        for (int i = 0; i < list.Count; ++i)
                            _scratch.Add(list[i]);
                    }
                }
            }
        }

        private static RuntimeWorld _world;
        private static string _loadAttemptScene = string.Empty;
        private static int _loadGeneration;
        private static bool _loadInProgress;
        private static readonly Dictionary<long, bool> PendingBlockerStates =
            new Dictionary<long, bool>();

        public static bool IsLoaded => _world != null;
        public static bool IsLoading => _loadInProgress;
        public static string LoadedMapId => _world?.Snapshot?.mapId ?? string.Empty;
        public static string LoadedHash => _world?.Snapshot?.contentHash ?? string.Empty;

        public static bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
        {
            if (stableId <= 0)
                return false;
            if (_world != null)
                return _world.TrySetDynamicBlockerEnabled(stableId, enabled);

            // World blocker state can arrive before the client-safe map is ready.
            // Preserve the latest value rather than losing a closed/open door update.
            PendingBlockerStates[stableId] = enabled;
            EnsureLoaded();
            return false; // Not yet applied. The pending state is replayed on publication.
        }

        public static bool TryResolveGroundedMove(
            Vector3 start,
            Vector3 desiredDelta,
            float radius,
            float maximumStepUp,
            float maximumSnapDown,
            out Vector3 resolved,
            out bool blocked)
        {
            resolved = start + desiredDelta;
            blocked = false;
            if (!EnsureLoaded())
                return false;

            return _world.TryResolveGroundedMove(
                start,
                desiredDelta,
                radius,
                maximumStepUp,
                maximumSnapDown,
                out resolved,
                out blocked);
        }

        /// <summary>
        /// Resolves the baked support height without moving. If the world is still being
        /// prepared, callers retain their existing authoritative-height fallback.
        /// </summary>
        public static bool TryFindSupportHeight(
            float x,
            float z,
            float referenceY,
            float probeUp,
            float probeDown,
            out float y)
        {
            y = referenceY;
            if (!EnsureLoaded())
                return false;

            return _world.TryFindSupportHeight(
                x,
                z,
                referenceY,
                probeUp,
                probeDown,
                out y);
        }

        // The earlier implementation did File.ReadAllText/GZip, JsonUtility.FromJson and
        // BuildGrid synchronously inside the FIRST movement/presentation query. Map content
        // can be large, so start once and never stall movement input on map preparation.
        private static bool EnsureLoaded()
        {
            if (_world != null)
                return true;

            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                BeginLoad(active.name, eagerOnly: false);
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeSceneWarmup()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Scene active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
                BeginLoad(active.name, eagerOnly: true);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Additive scenes may contain UI/presentation objects; the active gameplay
            // scene, not every additive scene, owns this client-safe collision world.
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || active.handle != scene.handle)
                return;
            ClearForSceneChange();
            BeginLoad(scene.name, eagerOnly: true);
        }

        private static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            ClearForSceneChange();
            if (newScene.IsValid() && newScene.isLoaded)
                BeginLoad(newScene.name, eagerOnly: true);
        }

        private static void ClearForSceneChange()
        {
            ++_loadGeneration; // Invalidate pending completion from the previous scene.
            _world = null;
            _loadAttemptScene = string.Empty;
            _loadInProgress = false;
            PendingBlockerStates.Clear();
        }

        private static void BeginLoad(string sceneName, bool eagerOnly)
        {
            if (string.IsNullOrWhiteSpace(sceneName) || _world != null ||
                string.Equals(_loadAttemptScene, sceneName, StringComparison.Ordinal))
                return;

            string folder = Path.Combine(Application.streamingAssetsPath, "MMOWorld");
            if (!Directory.Exists(folder))
            {
                _loadAttemptScene = sceneName;
                return;
            }

            // Do not eagerly load the only shared-world file into Character Select or
            // another unrelated scene merely because the legacy fallback accepts it.
            // First gameplay query retains the legacy fallback, but loads asynchronously.
            string mapId = ServerMapId.Normalize(sceneName);
            string preferred = Path.Combine(folder, mapId + ".sharedworld.json.gz");
            string legacy = Path.Combine(folder, mapId + ".sharedworld.json");
            bool exactMatch = File.Exists(preferred) || File.Exists(legacy);
            if (eagerOnly && !exactMatch)
                return;

            _loadAttemptScene = sceneName;
            StartLoad(sceneName, folder, ++_loadGeneration);
        }

        private static void StartLoad(string sceneName, string folder, int generation)
        {
            _loadInProgress = true;
            LoadAsync(sceneName, folder, generation);
        }

        private static async void LoadAsync(string sceneName, string folder, int generation)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeWorld loaded = null;
            Exception failure = null;
            try
            {
                // Pure DTO + grid preparation; no GameObject, Transform or scene access.
                // JsonUtility.FromJson supports deserialization from background threads.
                loaded = await Task.Run(() => ReadAndBuildWorld(folder, sceneName));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            // Unity's synchronization context resumes this await on its main thread.
            Scene active = SceneManager.GetActiveScene();
            if (generation != _loadGeneration || !active.IsValid() ||
                !string.Equals(sceneName, active.name, StringComparison.Ordinal))
                return;

            _loadInProgress = false;
            if (failure != null)
            {
                Debug.LogWarning($"[Shared World] Background preparation failed: {failure.Message}");
                return;
            }
            if (loaded == null)
                return; // Same missing-file behavior as before, without blocking movement.

            // Replay all state changes observed while the world data was being prepared.
            foreach (KeyValuePair<long, bool> pair in PendingBlockerStates)
                loaded.TrySetDynamicBlockerEnabled(pair.Key, pair.Value);
            PendingBlockerStates.Clear();
            _world = loaded;
            Debug.Log(
                $"[Shared World] Ready '{loaded.Snapshot.mapId}' revision={loaded.Snapshot.bakeRevision}, " +
                $"support={loaded.Snapshot.supportTriangles?.Length ?? 0:n0}, " +
                $"edges={loaded.Snapshot.surfaceEdges?.Length ?? 0:n0}, " +
                $"collision={loaded.Snapshot.collisionTriangles?.Length ?? 0:n0}, " +
                $"interactables={loaded.Snapshot.interactables?.Length ?? 0:n0}, " +
                $"prepareMs={timer.Elapsed.TotalMilliseconds:0.##} (background). ");
        }

        private static RuntimeWorld ReadAndBuildWorld(string folder, string sceneName)
        {
            if (!Directory.Exists(folder))
                return null;

            string mapId = ServerMapId.Normalize(sceneName);
            string preferred = Path.Combine(folder, mapId + ".sharedworld.json.gz");
            string legacy = Path.Combine(folder, mapId + ".sharedworld.json");
            string path = File.Exists(preferred) ? preferred
                : File.Exists(legacy) ? legacy : FindFallback(folder, mapId);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string json;
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream stream = File.OpenRead(path))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                    json = reader.ReadToEnd();
            }
            else
            {
                json = File.ReadAllText(path);
            }
            SharedWorldSnapshot snapshot = JsonUtility.FromJson<SharedWorldSnapshot>(json);
            if (snapshot == null || snapshot.formatVersion != SharedWorldFormat.Version ||
                string.IsNullOrWhiteSpace(snapshot.mapId))
                throw new InvalidDataException($"Invalid client world package: {path}");

            return new RuntimeWorld(snapshot);
        }

        private static string FindFallback(string folder, string mapId)
        {
            string[] compressed = Directory.GetFiles(folder, "*.sharedworld.json.gz", SearchOption.TopDirectoryOnly);
            string[] legacy = Directory.GetFiles(folder, "*.sharedworld.json", SearchOption.TopDirectoryOnly);
            string[] files = new string[compressed.Length + legacy.Length];
            Array.Copy(compressed, files, compressed.Length);
            Array.Copy(legacy, 0, files, compressed.Length, legacy.Length);
            if (files.Length == 1)
                return files[0];

            for (int i = 0; i < files.Length; ++i)
            {
                string name = Path.GetFileName(files[i]);
                const string gzSuffix = ".sharedworld.json.gz";
                const string jsonSuffix = ".sharedworld.json";
                if (name.EndsWith(gzSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - gzSuffix.Length);
                else if (name.EndsWith(jsonSuffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - jsonSuffix.Length);
                if (name.StartsWith(mapId, StringComparison.OrdinalIgnoreCase))
                    return files[i];
            }
            return string.Empty;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            ClearForSceneChange();
        }
    }
}
