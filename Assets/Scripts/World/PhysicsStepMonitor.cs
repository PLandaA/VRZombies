#if UNITY_EDITOR
using UnityEngine;

namespace VRZ.World
{
    /// EDITOR-ONLY diagnostic: how many physics steps does each rendered frame get?
    ///
    /// AutoHand disables Rigidbody interpolation on hands, held objects and the player body, and
    /// relies on DynamicTimestepSetter keeping fixedDeltaTime == frame time (exactly 1 step/frame).
    /// When frame times fluctuate (editor + Link), frames get 0 or 2 steps and held objects visibly
    /// hitch against the camera while walking. This logs the evidence: a histogram of steps per
    /// frame, the current fixedDeltaTime, average fps and body speed, every ReportInterval seconds.
    /// Self-creates on play; strip-safe (whole file is #if UNITY_EDITOR).
    public class PhysicsStepMonitor : MonoBehaviour
    {
        private const float ReportInterval = 3f;
        private const float WalkingSpeed = 0.3f;   // m/s: below this the report is tagged "still"

        private int _stepsThisFrame;           // FixedUpdate calls since the last Update
        private readonly int[] _histogram = new int[4];   // frames that got 0, 1, 2, 3+ steps
        private int _frames;
        private float _frameTimeSum;
        private float _speedSum;
        private float _nextReport;

        // ── Editor-only mitigation ──
        // AutoHand clamps fixedDeltaTime to [1/144, 1/50]. The arena runs at ~30 fps in the editor
        // with a headset, so the clamp pins physics at 20 ms while frames take 30 ms = 1.5 steps
        // per frame, alternating 1 and 2 -> held objects hitch while walking. Relaxing the floor to
        // 1/30 kept the 1:1 pairing down to 30 fps...
        //
        // ...but (2026-09-21) that traded a walking hitch for a WORSE artifact: AutoHand's joint and
        // WeightlessFollower are tuned for ~11 ms steps; at 28-33 ms they are under-damped and a held
        // rifle/magazine oscillates violently in the hand ("the master's rifle shakes": the master
        // was the 36 fps editor instance). Builds never see this (the monitor is editor-only and the
        // Quest runs 72 Hz / 13.9 ms). The floor is back at AutoHand's 1/50 so editor feel matches the
        // build; the walking hitch at 36 fps is the honest symptom of the editor being slow.
        // Set to 1f/30f to get the old behaviour back for walking tests.
        private const float RelaxedSlowestTimestep = 1f / 50f;
        private bool _clampRelaxed;
        private float _nextClampCheck;

        private void TryRelaxTimestepClamp()
        {
            if (_clampRelaxed || Time.unscaledTime < _nextClampCheck) return;
            _nextClampCheck = Time.unscaledTime + 0.5f;

            var setter = FindFirstObjectByType<DynamicTimestepSetter>();
            if (setter == null) return;

            float before = setter.slowestTimestep;
            setter.slowestTimestep = RelaxedSlowestTimestep;
            _clampRelaxed = true;
            Debug.Log($"[PhysStep] DynamicTimestepSetter.slowestTimestep {before * 1000f:F1}ms -> {RelaxedSlowestTimestep * 1000f:F1}ms (editor-only mitigation)");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            var go = new GameObject("[PhysicsStepMonitor]");
            DontDestroyOnLoad(go);
            go.AddComponent<PhysicsStepMonitor>();
            // The hitch autopsy needs the editor profiler recording (in memory, no window required).
            UnityEditorInternal.ProfilerDriver.enabled = true;
            UnityEditorInternal.ProfilerDriver.profileEditor = false;
            // ForceQualityLevel("Quest") was tried here: the arena stayed at 30-36 fps with the Quest
            // RP asset too, so the PC render path is not the bottleneck. Left available for re-tests.
        }

        // EXPERIMENT: render the editor session with the Quest quality level (Quest_RPAsset) to see
        // whether the arena's ~30 fps is the PC render path or CPU-side work. Editor-only, in-memory
        // (QualitySettings changes in play mode do not persist).
        private static void ForceQualityLevel(string name)
        {
            int index = System.Array.IndexOf(QualitySettings.names, name);
            if (index < 0) { Debug.LogWarning($"[PhysStep] Quality level '{name}' not found"); return; }
            string before = QualitySettings.names[QualitySettings.GetQualityLevel()];
            QualitySettings.SetQualityLevel(index, applyExpensiveChanges: true);
            Debug.Log($"[PhysStep] Quality level {before} -> {QualitySettings.names[QualitySettings.GetQualityLevel()]} (editor-only experiment)");
        }

        private void Start() => _nextReport = Time.unscaledTime + ReportInterval;

        private void FixedUpdate() => _stepsThisFrame++;

        // ── Spike attribution (which one-off cost caused the worst frame of the interval?) ──
        private static readonly string[] SpikeMarkers =
        {
            "Shader.CreateGPUProgram",     // first-time shader variant compile (editor: huge; Quest: Vulkan PSO)
            "Instantiate",                 // prefab instantiation
            "Animator.Initialize",
            "Loading.LoadFileHeaders",
            "AssetBundle.LoadAsset",
            "Physics.Processing",
            "Gfx.WaitForPresentOnGfxThread",
        };
        private UnityEngine.Profiling.Recorder[] _recorders;
        private float _worstFrameMs;
        private readonly float[] _markerAtWorst = new float[7];

        private void EnsureRecorders()
        {
            if (_recorders != null) return;
            _recorders = new UnityEngine.Profiling.Recorder[SpikeMarkers.Length];
            for (int i = 0; i < SpikeMarkers.Length; i++)
            {
                _recorders[i] = UnityEngine.Profiling.Recorder.Get(SpikeMarkers[i]);
                _recorders[i].enabled = true;
            }
        }

        private void TrackSpike(float frameMs)
        {
            if (frameMs > HitchDumpThresholdMs) { _pendingHitchMs = frameMs; _pendingRetries = 60; }
            if (_pendingRetries > 0)
            {
                _pendingRetries--;
                if (DumpHitchHierarchy(_pendingHitchMs)) _pendingRetries = 0;
            }
            if (frameMs <= _worstFrameMs) return;
            _worstFrameMs = frameMs;
            for (int i = 0; i < _recorders.Length; i++)
                _markerAtWorst[i] = _recorders[i].isValid ? _recorders[i].elapsedNanoseconds / 1e6f : -1f;
        }

        // The profiler finalizes a frame a little after we measure it, so a dump is retried for a few frames.
        private float _pendingHitchMs;
        private int _pendingRetries;

        // ── Hitch autopsy: when a frame blows past the threshold, dump the profiler hierarchy of the
        // frame that just completed (ProfilerDriver.lastFrameIndex) so the culprit is named without
        // anyone having to catch it in the Profiler window. Editor-only API, max 3 dumps per play.
        private const float HitchDumpThresholdMs = 100f;
        private int _dumpsLeft = 6;

        private bool DumpHitchHierarchy(float frameMs)
        {
            if (_dumpsLeft <= 0 || !UnityEditorInternal.ProfilerDriver.enabled) return true;
            int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
            if (last < 0) return false;

            // lastFrameIndex lags the frame we just measured; pick the slowest of the last few instead.
            // (Scene-load hitches are NOT filtered out: the arena switch turned out to be the real one.)
            int frame = -1; float frameTime = 0f;
            for (int f = Mathf.Max(0, last - 40); f <= last; f++)
            {
                using (var probe = UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView(
                           f, 0, UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.Default,
                           UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (probe.valid && probe.frameTimeMs > frameTime) { frameTime = probe.frameTimeMs; frame = f; }
                }
            }
            if (frame < 0 || frameTime < HitchDumpThresholdMs * 0.5f) return false;   // not in the buffer yet, retry
            _dumpsLeft--;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[PhysStep] HITCH {frameMs:F0}ms (profiler frame #{frame} = {frameTime:F0}ms) — top of hierarchy:");
            using (var v = UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView(
                       frame, 0, UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.Default,
                       UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime, false))
            {
                if (!v.valid) { Debug.Log(sb + "  (frame data not valid)"); return true; }
                int col = UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime;
                System.Action<int, int> dump = null;
                dump = (id, depth) =>
                {
                    float ms = v.GetItemColumnDataAsFloat(id, col);
                    if (ms < 4f) return;
                    sb.AppendLine($"{new string(' ', depth * 2)}{v.GetItemName(id)}  {ms:F1}ms");
                    if (depth >= 8) return;
                    var children = new System.Collections.Generic.List<int>();
                    v.GetItemChildren(id, children);
                    children.Sort((a, b) => v.GetItemColumnDataAsFloat(b, col).CompareTo(v.GetItemColumnDataAsFloat(a, col)));
                    int n = 0;
                    foreach (var c in children) { if (n++ >= 4) break; dump(c, depth + 1); }
                };
                dump(v.GetRootItemID(), 0);
            }
            Debug.Log(sb.ToString());
            return true;
        }

        private string SpikeReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"worst frame {_worstFrameMs:F1}ms");
            for (int i = 0; i < _recorders.Length; i++)
                if (_markerAtWorst[i] > 0.05f) sb.Append($" | {SpikeMarkers[i]}={_markerAtWorst[i]:F1}ms");
            _worstFrameMs = 0f;
            return sb.ToString();
        }

        private void Update()
        {
            TryRelaxTimestepClamp();
            EnsureRecorders();
            TrackSpike(Time.unscaledDeltaTime * 1000f);

            // Bucket this frame by how many physics steps ran before it rendered.
            int bucket = Mathf.Min(_stepsThisFrame, 3);
            _histogram[bucket]++;
            _stepsThisFrame = 0;

            _frames++;
            _frameTimeSum += Time.unscaledDeltaTime;

            var player = Autohand.AutoHandPlayer.Instance;
            if (player != null && player.body != null)
            {
                var v = player.body.linearVelocity; v.y = 0f;
                _speedSum += v.magnitude;
            }

            if (Time.unscaledTime >= _nextReport) Report();
        }

        private void Report()
        {
            if (_frames == 0) return;
            float avgFrameMs = _frameTimeSum / _frames * 1000f;
            float avgFps = 1000f / avgFrameMs;
            float avgSpeed = _speedSum / _frames;
            float fixedMs = Time.fixedDeltaTime * 1000f;

            float P(int i) => 100f * _histogram[i] / _frames;
            string tag = avgSpeed >= WalkingSpeed ? "WALKING" : "still";
            string quality = QualitySettings.names[QualitySettings.GetQualityLevel()];

            Debug.Log($"[PhysStep] {tag} [{quality}] speed={avgSpeed:F2}m/s | fps={avgFps:F1} (frame {avgFrameMs:F1}ms) fixed={fixedMs:F1}ms | " +
                      $"steps/frame: 0={P(0):F0}%  1={P(1):F0}%  2={P(2):F0}%  3+={P(3):F0}%  (n={_frames}) | {SpikeReport()}");

            System.Array.Clear(_histogram, 0, _histogram.Length);
            _frames = 0; _frameTimeSum = 0f; _speedSum = 0f;
            _nextReport = Time.unscaledTime + ReportInterval;
        }
    }
}
#endif
