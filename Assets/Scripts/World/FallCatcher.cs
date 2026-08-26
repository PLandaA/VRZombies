using UnityEngine;
using UnityEngine.SceneManagement;
using Autohand;

/// Safety net: if the player somehow falls out of the level, teleports them back to where they started in the current scene.
public class FallCatcher : MonoBehaviour
{
    private Vector3 _startPos;
    private bool _captured;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("FallCatcher");
        DontDestroyOnLoad(go);
        var fc = go.AddComponent<FallCatcher>();
        SceneManager.sceneLoaded += (s, m) => fc._captured = false;
    }

    private void FixedUpdate()
    {
        var player = AutoHandPlayer.Instance;
        if (player == null) return;

        if (!_captured)
        {
            if (player.transform.position.y > -1f)
            {
                _startPos = player.transform.position;
                _captured = true;
            }
            return;
        }

        if (player.transform.position.y < -5f)
            player.SetPosition(_startPos + Vector3.up * 0.5f);
    }
}
