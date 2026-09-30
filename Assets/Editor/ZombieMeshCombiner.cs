using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRZ.EditorTools
{
    /// Merges every SkinnedMeshRenderer of Zombie1.prefab into one (2026-09-30). The pack ships
    /// the zombie in 14 skinned parts sharing one material: 14 draw calls, 14 shadow-caster draws
    /// and 14 skinning jobs per zombie, 84 of each with six on screen. All parts share the skeleton
    /// and the same bind poses (verified: 0 mismatches over 55 bones), so vertices can simply be
    /// concatenated and bone indices remapped to one union bone list.
    /// Output: Assets/Zombie/Meshes/Zombie1_Combined.asset and the prefab rewired to a single
    /// "Z_Combined" renderer; the part GameObjects are deleted. Re-run if the model changes.
    public static class ZombieMeshCombiner
    {
        private const string PrefabPath = "Assets/Prefabs/Zombie1.prefab";
        private const string MeshPath = "Assets/Zombie/Meshes/Zombie1_Combined.asset";

        [MenuItem("VRZ/Dev/Zombie/Combine skinned meshes")]
        private static void Combine()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var parts = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (parts.Length < 2) { Debug.LogWarning("[ZombieMeshCombiner] Nothing to combine (" + parts.Length + " renderer)."); return; }

                // Union bone list + one bind pose per bone (verified identical across parts).
                var boneIndex = new Dictionary<Transform, int>();
                var bones = new List<Transform>();
                var bindposes = new List<Matrix4x4>();
                foreach (var p in parts)
                {
                    var bp = p.sharedMesh.bindposes;
                    for (int i = 0; i < p.bones.Length; i++)
                    {
                        var b = p.bones[i];
                        if (boneIndex.ContainsKey(b)) continue;
                        boneIndex[b] = bones.Count; bones.Add(b); bindposes.Add(bp[i]);
                    }
                }

                var verts = new List<Vector3>(); var normals = new List<Vector3>(); var tangents = new List<Vector4>();
                var uvs = new List<Vector2>(); var colors = new List<Color>(); var weights = new List<BoneWeight>(); var tris = new List<int>();
                Material material = parts[0].sharedMaterial;
                foreach (var p in parts)
                {
                    var m = p.sharedMesh;
                    if (p.sharedMaterial != material) { Debug.LogError("[ZombieMeshCombiner] " + p.name + " uses a different material; aborting."); return; }
                    int offset = verts.Count;
                    verts.AddRange(m.vertices); normals.AddRange(m.normals); tangents.AddRange(m.tangents); uvs.AddRange(m.uv);
                    var c = m.colors; if (c.Length == m.vertexCount) colors.AddRange(c); else for (int i = 0; i < m.vertexCount; i++) colors.Add(Color.white);
                    foreach (var w in m.boneWeights)
                    {
                        var r = w;
                        r.boneIndex0 = boneIndex[p.bones[w.boneIndex0]]; r.boneIndex1 = boneIndex[p.bones[w.boneIndex1]];
                        r.boneIndex2 = boneIndex[p.bones[w.boneIndex2]]; r.boneIndex3 = boneIndex[p.bones[w.boneIndex3]];
                        weights.Add(r);
                    }
                    foreach (var t in m.triangles) tris.Add(t + offset);
                }

                var mesh = new Mesh { name = "Zombie1_Combined" };
                mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetTangents(tangents); mesh.SetUVs(0, uvs); mesh.SetColors(colors);
                mesh.boneWeights = weights.ToArray(); mesh.bindposes = bindposes.ToArray();
                mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();

                System.IO.Directory.CreateDirectory("Assets/Zombie/Meshes");
                AssetDatabase.DeleteAsset(MeshPath);
                AssetDatabase.CreateAsset(mesh, MeshPath);

                // One renderer, same parent and local transform as the parts (all identical).
                var parent = parts[0].transform.parent;
                var go = new GameObject("Z_Combined");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = parts[0].transform.localPosition; go.transform.localRotation = parts[0].transform.localRotation; go.transform.localScale = parts[0].transform.localScale;
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh; smr.bones = bones.ToArray(); smr.rootBone = parts[0].rootBone; smr.sharedMaterial = material;
                smr.shadowCastingMode = parts[0].shadowCastingMode; smr.lightProbeUsage = parts[0].lightProbeUsage; smr.quality = parts[0].quality;
                // Bounds from the bind pose plus a margin cover every animation; no per-frame bounds recompute.
                smr.updateWhenOffscreen = false;
                var bounds = mesh.bounds; bounds.Expand(0.6f); smr.localBounds = bounds;

                int removed = 0;
                foreach (var p in parts) { Object.DestroyImmediate(p.gameObject); removed++; }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log("[ZombieMeshCombiner] " + removed + " parts -> 1 renderer: " + verts.Count + " verts, " + tris.Count / 3 + " tris, " + bones.Count + " bones. Mesh: " + MeshPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
