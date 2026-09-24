using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace VRZ.FX
{

    /// Shared world-space popup text: floats up, faces the camera, fades out, then returns to a
    /// pool. TextMeshPro mesh generation is the expensive part of a popup; instances are built
    /// once and recycled (text and colour change, the component survives).
    public static class PopupText
    {
        private static readonly Stack<PopupFloat> Free = new();
        private static Transform _root;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Free.Clear(); _root = null; }

        public static void Spawn(Vector3 pos, string text, Color color, float fontSize, float life = 1.2f, float riseSpeed = 0.9f)
        {
            PopupFloat popup = null;
            while (Free.Count > 0 && popup == null) popup = Free.Pop();   // skip destroyed ones
            if (popup == null) popup = Create();

            popup.transform.position = pos;
            popup.Tmp.text = text;
            popup.Tmp.fontSize = fontSize;
            popup.Tmp.color = color;
            popup.Begin(life, riseSpeed);
        }

        private static PopupFloat Create()
        {
            if (_root == null)
            {
                var rootGo = new GameObject("[PopupPool]");
                Object.DontDestroyOnLoad(rootGo);
                _root = rootGo.transform;
            }
            var go = new GameObject("Popup");
            go.transform.SetParent(_root, false);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(3f, 1.5f);
            var f = go.AddComponent<PopupFloat>();
            f.Tmp = tmp;
            return f;
        }

        private static void Recycle(PopupFloat popup)
        {
            popup.gameObject.SetActive(false);
            Free.Push(popup);
        }

        private class PopupFloat : MonoBehaviour
        {
            public TextMeshPro Tmp;
            private float _life, _riseSpeed, _t;

            public void Begin(float life, float riseSpeed)
            {
                _life = life; _riseSpeed = riseSpeed; _t = 0f;
                gameObject.SetActive(true);
            }

            private void LateUpdate()
            {
                _t += Time.deltaTime;
                transform.position += Vector3.up * (_riseSpeed * Time.deltaTime);
                var cam = Camera.main;
                if (cam != null)
                    transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
                var c = Tmp.color;
                c.a = Mathf.Clamp01(1.6f - (_t / _life) * 1.6f);
                Tmp.color = c;
                if (_t >= _life) Recycle(this);
            }
        }
    }
}
