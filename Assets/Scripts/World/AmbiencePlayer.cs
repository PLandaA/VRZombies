using UnityEngine;
using Fusion;

/// Picks one random horror ambience track per run, synced to every player via a networked index.
public class AmbiencePlayer : NetworkBehaviour
{
    [Tooltip("Ambience clips (one is picked at random per run)")]
    [SerializeField] private AudioClip[] clips;

    [Tooltip("Looping 2D AudioSource used for the ambience")]
    [SerializeField] private AudioSource source;

    [Networked, OnChangedRender(nameof(OnClipChanged))]
    private int ClipIndex { get; set; }

    private int _playingIndex;

    public override void Spawned()
    {
        if (Object.HasStateAuthority && ClipIndex == 0 && clips != null && clips.Length > 0)
            ClipIndex = Random.Range(0, clips.Length) + 1;

        TryPlay();
    }

    private void OnClipChanged()
    {
        TryPlay();
    }

    private void TryPlay()
    {
        if (source == null || clips == null) return;
        int idx = ClipIndex;
        if (idx <= 0 || idx > clips.Length) return;
        if (_playingIndex == idx && source.isPlaying) return;

        source.clip = clips[idx - 1];
        source.loop = true;
        source.spatialBlend = 0f;
        source.Play();
        _playingIndex = idx;
        Debug.Log("[Ambience] Run track: " + source.clip.name);
    }
}
