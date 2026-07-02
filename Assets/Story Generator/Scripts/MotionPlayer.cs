using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RootMotion.FinalIK;
using UnityEngine.AI;
using Newtonsoft.Json;

/// <summary>
/// Feasibility-study component: drives a Humanoid character from an external
/// motion frame stream (e.g. SMPL-X pose parameters converted on the Python
/// side to per-bone local rotations).
///
/// Lifecycle:
///   1. Play(frames) receives a list of MotionFrame and starts playback.
///   2. On enable, it disables the components that would otherwise fight the
///      bone overrides: FullBodyBipedIK, NavMeshAgent, and the Animator's
///      root motion. On disable/finish it restores them.
///   3. Each LateUpdate it samples the frame at the current simulation time
///      and writes localRotation to every bone listed in the frame, plus the
///      root position/rotation. Running in LateUpdate puts it after the
///      Animator/IK update so the override sticks.
///
/// The retargeting math is done on the Python side: each bone quaternion in a
/// frame is ALREADY the final Unity local rotation (i.e. restOffset *
/// smplxLocalRot). This keeps the Unity side a dumb player.
/// </summary>
[RequireComponent(typeof(Animator))]
public class MotionPlayer : MonoBehaviour
{
    public enum PlayState { Idle, Playing, Finished }

    [Tooltip("Frames per second of the incoming motion stream.")]
    public float frameRate = 30f;

    [Tooltip("Loop the motion when it reaches the last frame.")]
    public bool loop = false;

    private Animator m_animator;
    private FullBodyBipedIK m_fbbik;
    private NavMeshAgent m_nma;
    private Rigidbody m_rb;

    // Saved state to restore after playback.
    private bool m_animatorEnabled;
    private bool m_applyRootMotion;
    private bool m_fbbikEnabled;
    private bool m_nmaEnabled;
    private bool m_nmaUpdatePosition;
    private bool m_nmaUpdateRotation;
    private bool m_rbIsKinematic;
    // True once we have saved the original enabled-state and disabled the
    // conflict sources; makes CaptureAndDisableConflictSources idempotent.
    private bool m_conflictsCaptured = false;

    private List<MotionFrame> m_frames;
    private float m_elapsed;
    private PlayState m_state = PlayState.Idle;
    // When true, PlayAndCapture owns frame advance and LateUpdate stays idle
    // so the two paths don't fight over bone writes / m_elapsed.
    private bool m_capturing = false;
    // Last root pose written by SetPose/ApplyAt. While m_holdRoot is true,
    // LateUpdate re-writes this every frame so any system that snaps the
    // character back to its spawn point (NavMesh, physics, placement scripts)
    // between HTTP requests cannot win. Set when frames are loaded; cleared by
    // Stop() so the character is fully released afterwards.
    private bool m_holdRoot = false;
    private Vector3 m_lastRootPos = Vector3.zero;
    private Quaternion m_lastRootRot = Quaternion.identity;

    // Cache bone transforms by name once.
    private Dictionary<string, Transform> m_boneCache = new Dictionary<string, Transform>();

    public PlayState State => m_state;
    public int FrameCount => m_frames != null ? m_frames.Count : 0;

    /// <summary>
    /// One frame of motion: root transform + a map of bone-name -> local
    /// rotation (quaternion xyzw).
    /// </summary>
    [System.Serializable]
    public class MotionFrame
    {
        public float[] rootPosition; // 3
        public float[] rootRotation; // 4, quaternion xyzw
        public List<BoneRotation> bones; // bone name -> quat xyzw
    }

    [System.Serializable]
    public class BoneRotation
    {
        public string bone;
        public float[] rot; // 4, xyzw
    }

    [System.Serializable]
    private class FrameList { public List<MotionFrame> frames; public float frameRate; }

    void Awake()
    {
        m_animator = GetComponent<Animator>();
        m_fbbik = GetComponent<FullBodyBipedIK>();
        m_nma = GetComponent<NavMeshAgent>();
        m_rb = GetComponent<Rigidbody>();
    }

    /// <summary>Load frames from a JSON string and begin playback.
    /// Uses Newtonsoft.Json because Unity's JsonUtility does not deserialize
    /// nested List&lt;T&gt; (frames -> bones).</summary>
    public void PlayFromJson(string json)
    {
        // LoadFrames already caches bones, disables conflict sources, applies
        // frame 0, and leaves m_state Idle. We only need to flip to Playing so
        // LateUpdate advances frames. Do NOT call Play() here -- it would call
        // CaptureAndDisableConflictSources a second time, overwriting the saved
        // (true) enabled-state with false, so Stop() could never restore.
        if (LoadFrames(json) > 0)
        {
            m_state = PlayState.Playing;
            Debug.Log("[MotionPlayer] Playing " + m_frames.Count + " frames @ " + frameRate + " fps");
        }
    }

    /// <summary>Parse a frame-table JSON and cache the bones + conflict-source
    /// state, but do NOT start playback. Returns the frame count (0 on parse
    /// error). Used by the set_body_pose action so the Python side can drive
    /// frame-by-frame while capture is handled by the native camera_image
    /// action. After this, call SetPose(i) for each frame index.</summary>
    public int LoadFrames(string json)
    {
        FrameList data = JsonConvert.DeserializeObject<FrameList>(json);
        if (data == null || data.frames == null || data.frames.Count == 0)
        {
            Debug.LogError("[MotionPlayer] No frames in payload.");
            m_frames = null;
            return 0;
        }
        if (data.frameRate > 0f) frameRate = data.frameRate;
        m_frames = data.frames;
        m_elapsed = 0f;
        CacheBones();
        CaptureAndDisableConflictSources();
        m_state = PlayState.Idle; // stay Idle: LateUpdate won't auto-advance.
        m_holdRoot = true;        // LateUpdate re-asserts root pose each frame.
        ApplyAt(0);
        return m_frames.Count;
    }

    /// <summary>Apply the pose at frame index i and hold it. Does not capture
    /// and does not auto-advance; the caller drives frame indices. Leaves
    /// m_state Idle so LateUpdate stays inert.</summary>
    public bool SetPose(int frameIndex)
    {
        if (m_frames == null || frameIndex < 0 || frameIndex >= m_frames.Count)
        {
            return false;
        }
        ApplyAt(frameIndex);
        return true;
    }

    public void Play(List<MotionFrame> frames)
    {
        m_frames = frames;
        m_elapsed = 0f;
        CacheBones();
        CaptureAndDisableConflictSources();
        m_state = PlayState.Playing;
        ApplyAt(0);
        Debug.Log("[MotionPlayer] Playing " + frames.Count + " frames @ " + frameRate + " fps");
    }

    public void Stop()
    {
        m_state = PlayState.Idle;
        m_holdRoot = false; // release the character; other systems may move it again.
        m_frames = null;
        RestoreConflictSources();
    }

    /// <summary>
    /// Play the loaded frames one frame per Unity frame, capturing each frame
    /// from the given cameras to PNG files. Intended to be run as a coroutine
    /// from TestDriver so the HTTP request blocks until capture finishes.
    ///
    /// outputFolder: absolute or relative path; created if missing.
    /// cameraIndexes: which cameras in `cameras` to capture; null/empty = none.
    /// Returns the number of frames captured.
    /// </summary>
    public IEnumerator PlayAndCapture(string outputFolder, List<Camera> cameras, List<int> cameraIndexes, int imageWidth, int imageHeight)
    {
        if (m_frames == null || m_frames.Count == 0) yield break;
        if (string.IsNullOrEmpty(outputFolder))
        {
            Debug.LogError("[MotionPlayer] output_folder is empty; cannot capture.");
            yield break;
        }
        Directory.CreateDirectory(outputFolder);

        // Take over frame advance from LateUpdate so the captured frame index
        // matches the pose we write.
        m_capturing = true;

        List<Camera> activeCams = new List<Camera>();
        List<RenderTexture> rts = new List<RenderTexture>();
        List<int> activeIndexes = new List<int>();
        List<bool> camWasEnabled = new List<bool>();
        Texture2D tex = null;
        int captured = 0;

        try
        {
            if (cameras != null && cameraIndexes != null)
            {
                foreach (int ci in cameraIndexes)
                {
                    if (ci < 0 || ci >= cameras.Count) continue;
                    Camera cam = cameras[ci];
                    RenderTexture rt = RenderTexture.GetTemporary(imageWidth, imageHeight, 24);
                    cam.targetTexture = rt;
                    camWasEnabled.Add(cam.enabled);
                    cam.enabled = true;
                    activeCams.Add(cam);
                    rts.Add(rt);
                    activeIndexes.Add(ci);
                }
            }
            if (activeCams.Count == 0)
            {
                Debug.LogWarning("[MotionPlayer] No valid cameras to capture; no PNGs will be written.");
            }

            tex = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
            for (int i = 0; i < m_frames.Count; i++)
            {
                m_elapsed = i / frameRate;
                ApplyAt(i);

                // Wait a frame so the camera renders the new pose.
                yield return new WaitForEndOfFrame();

                for (int c = 0; c < activeCams.Count; c++)
                {
                    RenderTexture.active = rts[c];
                    tex.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
                    tex.Apply();
                    byte[] png = tex.EncodeToPNG();
                    string path = Path.Combine(outputFolder, string.Format("frame_{0:D5}_cam{1}.png", i, activeIndexes[c]));
                    File.WriteAllBytes(path, png);
                    captured++;
                }
            }
        }
        finally
        {
            // Restore cameras and release render textures even on exception.
            for (int c = 0; c < activeCams.Count; c++)
            {
                activeCams[c].targetTexture = null;
                activeCams[c].enabled = camWasEnabled.Count > c ? camWasEnabled[c] : false;
            }
            foreach (RenderTexture rt in rts) RenderTexture.ReleaseTemporary(rt);
            RenderTexture.active = null;
            if (tex != null) Object.Destroy(tex);
            m_capturedCount = captured;
            m_capturing = false;
        }
    }

    private int m_capturedCount = 0;
    public int LastCapturedCount => m_capturedCount;

    void LateUpdate()
    {
        // When driving frame-by-frame from the Python side (set_body_pose), the
        // caller owns frame advance and m_state stays Idle. Re-assert the last
        // root pose here every frame so nothing (NavMesh/physics/placement)
        // can move the character between HTTP requests. Bones are not re-touched:
        // the Animator/IK are disabled, so the poses written by SetPose persist.
        if (m_holdRoot && !m_capturing)
        {
            transform.position = m_lastRootPos;
            transform.rotation = m_lastRootRot;
            if (m_rb != null)
            {
                m_rb.position = m_lastRootPos;
                m_rb.rotation = m_lastRootRot;
            }
        }

        if (m_state != PlayState.Playing || m_frames == null || m_capturing) return;

        m_elapsed += Time.deltaTime;
        int idx = Mathf.FloorToInt(m_elapsed * frameRate);
        if (idx >= m_frames.Count)
        {
            if (loop)
            {
                m_elapsed = 0f;
                idx = 0;
            }
            else
            {
                m_state = PlayState.Finished;
                // Hold last frame; call Stop() externally to restore components.
                return;
            }
        }
        ApplyAt(idx);
    }

    private void ApplyAt(int idx)
    {
        MotionFrame f = m_frames[idx];

        // Root.
        if (f.rootPosition != null && f.rootPosition.Length == 3)
        {
            Vector3 p = new Vector3(f.rootPosition[0], f.rootPosition[1], f.rootPosition[2]);
            transform.position = p;
            m_lastRootPos = p;
            // Keep the Rigidbody in sync so the physics engine does not snap the
            // transform back next FixedUpdate (m_rb is kinematic while driving).
            if (m_rb != null) m_rb.position = p;
        }
        if (f.rootRotation != null && f.rootRotation.Length == 4)
        {
            Quaternion q = new Quaternion(f.rootRotation[0], f.rootRotation[1], f.rootRotation[2], f.rootRotation[3]);
            transform.rotation = q;
            m_lastRootRot = q;
            if (m_rb != null) m_rb.rotation = q;
        }

        // Bones.
        if (f.bones == null) return;
        for (int i = 0; i < f.bones.Count; i++)
        {
            BoneRotation br = f.bones[i];
            if (br.rot == null || br.rot.Length != 4) continue;
            if (m_boneCache.TryGetValue(br.bone, out Transform t))
            {
                t.localRotation = new Quaternion(br.rot[0], br.rot[1], br.rot[2], br.rot[3]);
            }
        }
    }

    private void CacheBones()
    {
        m_boneCache.Clear();
        if (m_animator == null || !m_animator.isHuman) return;
        // Cache by HumanBodyBones enum name (e.g. "Hips", "LeftUpperArm").
        System.Array boneEnums = System.Enum.GetValues(typeof(HumanBodyBones));
        foreach (HumanBodyBones hb in boneEnums)
        {
            if (hb == HumanBodyBones.LastBone) continue;
            Transform t = m_animator.GetBoneTransform(hb);
            if (t != null) m_boneCache[hb.ToString()] = t;
        }
    }

    private void CaptureAndDisableConflictSources()
    {
        // Idempotent: if we already captured the original enabled-state and
        // disabled the conflict sources, a second call (e.g. LoadFrames invoked
        // again with a new frame table) must NOT overwrite the saved state with
        // the now-false values, or RestoreConflictSources could never re-enable.
        if (m_conflictsCaptured) return;
        if (m_animator != null)
        {
            m_animatorEnabled = m_animator.enabled;
            m_applyRootMotion = m_animator.applyRootMotion;
            m_animator.applyRootMotion = false;
            // Keep enabled=false so neither state machine nor root motion fights us.
            m_animator.enabled = false;
        }
        if (m_fbbik != null)
        {
            m_fbbikEnabled = m_fbbik.enabled;
            m_fbbik.enabled = false;
        }
        if (m_nma != null)
        {
            m_nmaEnabled = m_nma.enabled;
            // Besides disabling, stop the agent from writing transform back to
            // its NavMesh position/rotation each frame (updatePosition/updateRotation
            // run even while pathing is idle) and drop any in-flight path.
            m_nmaUpdatePosition = m_nma.updatePosition;
            m_nmaUpdateRotation = m_nma.updateRotation;
            m_nma.updatePosition = false;
            m_nma.updateRotation = false;
            m_nma.ResetPath();
            m_nma.enabled = false;
        }
        if (m_rb != null)
        {
            // The character has a dynamic Rigidbody (only rotation is frozen).
            // A non-kinematic Rigidbody is reintegrated by the physics engine
            // every FixedUpdate and would snap the transform back, undoing our
            // transform.position writes. Make it kinematic so we own the pose.
            m_rbIsKinematic = m_rb.isKinematic;
            m_rb.isKinematic = true;
            m_rb.velocity = Vector3.zero;
            m_rb.angularVelocity = Vector3.zero;
        }
        m_conflictsCaptured = true;
    }

    private void RestoreConflictSources()
    {
        if (m_animator != null)
        {
            m_animator.enabled = m_animatorEnabled;
            m_animator.applyRootMotion = m_applyRootMotion;
        }
        if (m_fbbik != null) m_fbbik.enabled = m_fbbikEnabled;
        if (m_nma != null)
        {
            m_nma.enabled = m_nmaEnabled;
            m_nma.updatePosition = m_nmaUpdatePosition;
            m_nma.updateRotation = m_nmaUpdateRotation;
        }
        if (m_rb != null) m_rb.isKinematic = m_rbIsKinematic;
        m_conflictsCaptured = false;
    }

    void OnDisable()
    {
        if (m_state == PlayState.Playing) RestoreConflictSources();
    }
}
