#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        if (animator == null)
        {
            Debug.LogError("[BoneDumper] Selected '" + selected.name + "' has no Animator at all.");
            return;
        }
        if (!animator.isHuman)
        {
            // List all Animators found so the user can pick the right one.
            Animator[] all = selected.GetComponentsInChildren<Animator>(true);
            string summary = "";
            foreach (var a in all)
                summary += "\n  - " + a.gameObject.name + " (isHuman=" + a.isHuman
                           + ", avatar=" + (a.avatar != null ? a.avatar.name : "null") + ")";
            Debug.LogError("[BoneDumper] Animator on '" + animator.gameObject.name
                           + "' is NOT Humanoid. All Animators under '" + selected.name + "':" + summary
                           + "\nSelect the GameObject whose Animator isHuman=true and re-run.");
            return;
        }
        Debug.Log("[BoneDumper] Using Humanoid Animator on '" + animator.gameObject.name
                  + "' (avatar=" + (animator.avatar != null ? animator.avatar.name : "null")
                  + "), selected='" + selected.name + "'");

        // Enumerate every HumanBodyBones value (0 .. LastBone-1).
        Array boneEnums = Enum.GetValues(typeof(HumanBodyBones));
        Dictionary<HumanBodyBones, Transform> boneMap = new Dictionary<HumanBodyBones, Transform>();
        foreach (HumanBodyBones hb in boneEnums)
        {
            if (hb == HumanBodyBones.LastBone) continue;
            Transform t = animator.GetBoneTransform(hb);
            if (t != null) boneMap[hb] = t;
        }
        if (boneMap.Count == 0)
        {
            Debug.LogError("[BoneDumper] Animator isHuman=true but GetBoneTransform returned null for EVERY bone. "
                           + "The avatar's bone mapping is broken or this is a Generic avatar mislabeled. "
                           + "Check the Animator's Avatar field and re-import the fbx as Humanoid.");
            return;
        }
        Debug.Log("[BoneDumper] Mapped " + boneMap.Count + " bones.");

        // Build a reverse lookup: Transform -> HumanBodyBones, to find parents.
        Dictionary<Transform, HumanBodyBones> transformToBone = new Dictionary<Transform, HumanBodyBones>();
        foreach (var kv in boneMap)
        {
            if (!transformToBone.ContainsKey(kv.Value)) transformToBone[kv.Value] = kv.Key;
        }

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

        string dir = "Assets/StreamingAssets";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "humanoid_bone_dump.json");
        // Hand-roll JSON: JsonUtility cannot serialize anonymous objects, and we
        // avoid depending on Newtonsoft in the Editor assembly. The structure is flat.
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"character\": \"").Append(selected.name).Append("\",\n");
        sb.Append("  \"root\": ");
        if (rootInfo != null && hips != null)
        {
            sb.Append("{\n    \"name\": \"").Append(hips.name).Append("\",\n");
            sb.Append("    \"worldPosition\": [").Append(hips.position.x).Append(",").Append(hips.position.y).Append(",").Append(hips.position.z).Append("],\n");
            sb.Append("    \"worldRotation\": [").Append(hips.rotation.x).Append(",").Append(hips.rotation.y).Append(",").Append(hips.rotation.z).Append(",").Append(hips.rotation.w).Append("],\n");
            sb.Append("    \"rootWorldPosition\": [").Append(selected.transform.position.x).Append(",").Append(selected.transform.position.y).Append(",").Append(selected.transform.position.z).Append("],\n");
            sb.Append("    \"rootWorldRotation\": [").Append(selected.transform.rotation.x).Append(",").Append(selected.transform.rotation.y).Append(",").Append(selected.transform.rotation.z).Append(",").Append(selected.transform.rotation.w).Append("]\n  },\n");
        }
        else sb.Append("null,\n");
        sb.Append("  \"bones\": [\n");
        // Build bones JSON directly from boneMap.
        int idx = 0;
        foreach (var kv in boneMap)
        {
            HumanBodyBones hb = kv.Key;
            Transform t = kv.Value;
            HumanBodyBones parentBone = HumanBodyBones.LastBone;
            if (t.parent != null && transformToBone.TryGetValue(t.parent, out HumanBodyBones pb)) parentBone = pb;
            sb.Append("    {\n");
            sb.Append("      \"bone\": \"").Append(hb.ToString()).Append("\",\n");
            sb.Append("      \"transformName\": \"").Append(t.name).Append("\",\n");
            sb.Append("      \"parent\": \"").Append(parentBone.ToString()).Append("\",\n");
            sb.Append("      \"localPosition\": [").Append(t.localPosition.x).Append(",").Append(t.localPosition.y).Append(",").Append(t.localPosition.z).Append("],\n");
            sb.Append("      \"localRotation\": [").Append(t.localRotation.x).Append(",").Append(t.localRotation.y).Append(",").Append(t.localRotation.z).Append(",").Append(t.localRotation.w).Append("],\n");
            sb.Append("      \"worldPosition\": [").Append(t.position.x).Append(",").Append(t.position.y).Append(",").Append(t.position.z).Append("],\n");
            sb.Append("      \"worldRotation\": [").Append(t.rotation.x).Append(",").Append(t.rotation.y).Append(",").Append(t.rotation.z).Append(",").Append(t.rotation.w).Append("]\n");
            sb.Append("    }");
            if (idx < boneMap.Count - 1) sb.Append(",");
            sb.Append("\n");
            idx++;
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(path, sb.ToString());
        Debug.Log("[BoneDumper] Wrote " + boneMap.Count + " bones to " + path);
        AssetDatabase.Refresh();
    }
}
#endif
