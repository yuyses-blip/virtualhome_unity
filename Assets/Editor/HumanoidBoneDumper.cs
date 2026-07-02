#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Feasibility-study utility: dumps a Humanoid rig's bone hierarchy so we can
/// build the SMPL-X -> Unity humanoid retargeting table.
///
/// Run from the menu: Tools > Motion Import > Dump Humanoid Bones
/// (with a character GameObject selected in the Hierarchy).
///
/// For every HumanBodyBones entry it records:
///   - bone name + HumanBodyBones enum
///   - parent bone (if any)
///   - local position / local rotation / local scale at the current pose
///   - world position / world rotation
///
/// The dump is printed to the console and written to
///   Assets/StreamingAssets/humanoid_bone_dump.json
/// so the Python side can read the rest offsets and build the mapping table.
/// </summary>
public static class HumanoidBoneDumper
{
    [MenuItem("Tools/Motion Import/Dump Humanoid Bones")]
    public static void Dump()
    {
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
        {
            Debug.LogError("[BoneDumper] No GameObject selected. Select a character in the Hierarchy.");
            return;
        }

        Animator animator = selected.GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            Debug.LogError("[BoneDumper] Selected object has no Humanoid Animator.");
            return;
        }

        // Enumerate every HumanBodyBones value (0 .. LastBone-1).
        Array boneEnums = Enum.GetValues(typeof(HumanBodyBones));
        Dictionary<HumanBodyBones, Transform> boneMap = new Dictionary<HumanBodyBones, Transform>();
        foreach (HumanBodyBones hb in boneEnums)
        {
            if (hb == HumanBodyBones.LastBone) continue;
            Transform t = animator.GetBoneTransform(hb);
            if (t != null) boneMap[hb] = t;
        }

        // Build a reverse lookup: Transform -> HumanBodyBones, to find parents.
        Dictionary<Transform, HumanBodyBones> transformToBone = new Dictionary<Transform, HumanBodyBones>();
        foreach (var kv in boneMap)
        {
            if (!transformToBone.ContainsKey(kv.Value)) transformToBone[kv.Value] = kv.Key;
        }

        List<object> entries = new List<object>();
        Debug.Log("[BoneDumper] === Humanoid bone dump for " + selected.name + " ===");
        foreach (var kv in boneMap)
        {
            HumanBodyBones hb = kv.Key;
            Transform t = kv.Value;
            HumanBodyBones parentBone = HumanBodyBones.LastBone;
            if (t.parent != null && transformToBone.TryGetValue(t.parent, out HumanBodyBones pb))
            {
                parentBone = pb;
            }

            string line = string.Format(
                "{0}\tname={1}\tparent={2}\tlpos=({3:F4},{4:F4},{5:F4})\tlrot=({6:F4},{7:F4},{8:F4},{9:F4})\twpos=({10:F4},{11:F4},{12:F4})",
                hb, t.name, parentBone,
                t.localPosition.x, t.localPosition.y, t.localPosition.z,
                t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w,
                t.position.x, t.position.y, t.position.z);
            Debug.Log("[BoneDumper] " + line);

            entries.Add(new
            {
                bone = hb.ToString(),
                transformName = t.name,
                parent = parentBone.ToString(),
                localPosition = new float[] { t.localPosition.x, t.localPosition.y, t.localPosition.z },
                localRotation = new float[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
                worldPosition = new float[] { t.position.x, t.position.y, t.position.z },
                worldRotation = new float[] { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w },
            });
        }

        // Root (Hips) world transform is needed for coordinate alignment on the Python side.
        Transform hips = boneMap.ContainsKey(HumanBodyBones.Hips) ? boneMap[HumanBodyBones.Hips] : null;
        object rootInfo = null;
        if (hips != null)
        {
            rootInfo = new
            {
                name = hips.name,
                worldPosition = new float[] { hips.position.x, hips.position.y, hips.position.z },
                worldRotation = new float[] { hips.rotation.x, hips.rotation.y, hips.rotation.z, hips.rotation.w },
                rootWorldPosition = new float[] { selected.transform.position.x, selected.transform.position.y, selected.transform.position.z },
                rootWorldRotation = new float[] { selected.transform.rotation.x, selected.transform.rotation.y, selected.transform.rotation.z, selected.transform.rotation.w },
            };
        }

        object payload = new { character = selected.name, root = rootInfo, bones = entries };

        string dir = "Assets/StreamingAssets";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "humanoid_bone_dump.json");
        File.WriteAllText(path, JsonUtility.ToJson(payload, true));
        Debug.Log("[BoneDumper] Wrote " + entries.Count + " bones to " + path);
        AssetDatabase.Refresh();
    }
}
#endif
