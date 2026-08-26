using UnityEngine;
using TMPro;

/// Shared world-space popup text: floats up, faces the camera, fades out, self-destructs.
/// Used by kill popups, combo banners, and any future juice.
public static class PopupText
{
    public static void Spawn(Vector3 pos, string text, Color color, float fontSize, float life = 1.2f, float riseSpeed = 0.9f)
    {
        var go = new GameObject("Popup");
        go.transform.position = pos;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = color;
        tmp.rectTransform.sizeDelta = new Vector2(3f, 1.5f);
        var f = go.AddComponent<PopupFloat>();
        f.life = life;
        f.riseSpeed = riseSpeed;
    }

    private class PopupFloat : MonoBehaviour
    {
        public float life = 1.2f;
        public float riseSpeed = 0.9f;
        private TextMeshPro _tmp;
        private float _t;

        private void Awake() { _tmp = GetComponent<TextMeshPro>(); }

        private void LateUpdate()
        {
            _t += Time.deltaTime;
            transform.position += Vector3.up * (riseSpeed * Time.deltaTime);
            var cam = Camera.main;
            if (cam != null)
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
            if (_tmp != null)
            {
                var c = _tmp.color;
                c.a = Mathf.Clamp01(1.6f - (_t / life) * 1.6f);
                _tmp.color = c;
            }
            if (_t >= life) Destroy(gameObject);
        }
    }
}
