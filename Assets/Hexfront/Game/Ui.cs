using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hexfront
{
    /// <summary>
    /// Small helpers for building uGUI screens from code. Layout is done with Unity's layout
    /// groups (Column / Row), so screens adapt to any phone resolution without hand-placed pixels.
    /// </summary>
    public static class Ui
    {
        public static Font Font;
        static Sprite _rounded;
        static readonly List<RectTransform> SafeRoots = new List<RectTransform>();
        static Rect _lastSafe;
        static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        public static readonly Color PanelColor = new Color(0.07f, 0.09f, 0.13f, 0.93f);
        public static readonly Color ButtonColor = new Color(0.20f, 0.26f, 0.36f, 1f);
        public static readonly Color Accent = new Color(0.98f, 0.70f, 0.16f, 1f);
        public static readonly Color Good = new Color(0.30f, 0.78f, 0.40f, 1f);
        public static readonly Color Bad = new Color(0.88f, 0.30f, 0.26f, 1f);
        public static readonly Color TextColor = new Color(0.94f, 0.95f, 0.97f, 1f);
        public static readonly Color Dim = new Color(0.66f, 0.70f, 0.76f, 1f);

        public static void Init()
        {
            Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (Font == null) Font = Font.CreateDynamicFontFromOSFont("Arial", 24);
            EnsureEventSystem();
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            var module = go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            module.AssignDefaultActions();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        /// <summary>True when a screen position is on top of any UI element (so the board should ignore it).</summary>
        public static bool IsOverUi(Vector2 screenPosition)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            var data = new PointerEventData(es) { position = screenPosition };
            Hits.Clear();
            es.RaycastAll(data, Hits);
            return Hits.Count > 0;
        }

        // ------------------------------------------------------------------ canvas

        /// <summary>Creates a screen-space canvas and returns a child rect that stays inside the phone's safe area (notch).</summary>
        public static RectTransform CreateCanvas(string name, int sortOrder, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // scale with screen height (landscape game)
            go.AddComponent<GraphicRaycaster>();

            var safe = Rect("SafeArea", go.transform);
            SafeRoots.Add(safe);
            ApplySafeArea(safe);
            return safe;
        }

        static void ApplySafeArea(RectTransform rt)
        {
            var sa = Screen.safeArea;
            float w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            rt.anchorMin = new Vector2(sa.xMin / w, sa.yMin / h);
            rt.anchorMax = new Vector2(sa.xMax / w, sa.yMax / h);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>Call every frame; re-applies the safe area if the device rotated.</summary>
        public static void RefreshSafeAreas()
        {
            if (Screen.safeArea == _lastSafe) return;
            _lastSafe = Screen.safeArea;
            for (int i = SafeRoots.Count - 1; i >= 0; i--)
            {
                if (SafeRoots[i] == null) SafeRoots.RemoveAt(i);
                else ApplySafeArea(SafeRoots[i]);
            }
        }

        // ------------------------------------------------------------------ primitives

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static Sprite Rounded()
        {
            if (_rounded != null) return _rounded;
            const int size = 48; const float radius = 13f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius)));
                    float dy = Mathf.Max(0, Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)));
                    float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply();
            _rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(16, 16, 16, 16));
            return _rounded;
        }

        public static Image Box(string name, Transform parent, Color color, bool rounded = true)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            if (rounded) { img.sprite = Rounded(); img.type = Image.Type.Sliced; }
            return img;
        }

        public static Text Label(Transform parent, string text, int size = 22, TextAnchor align = TextAnchor.MiddleLeft, Color? color = null)
        {
            var rt = Rect("Label", parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font; t.fontSize = size; t.alignment = align; t.color = color ?? TextColor;
            t.text = text; t.raycastTarget = false; t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static Button Button(Transform parent, string text, Action onClick, int fontSize = 24, Color? color = null)
        {
            var img = Box("Button", parent, color ?? ButtonColor);
            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null) btn.onClick.AddListener(() => { Sfx.Play(Sfx.Id.Click); onClick(); });
            var label = Label(img.transform, text, fontSize, TextAnchor.MiddleCenter);
            label.rectTransform.Stretch(8, 2, 8, 2);
            return btn;
        }

        public static Text LabelOf(Button b) => b.GetComponentInChildren<Text>();

        // ------------------------------------------------------------------ layout helpers

        /// <summary>Fill the parent, with optional margins.</summary>
        public static RectTransform Stretch(this RectTransform rt, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom); rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Pin to a point of the parent (anchor = pivot, e.g. (1,0) is bottom-right) with an offset and size.</summary>
        public static RectTransform Pin(this RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = offset; rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Preferred size inside a Column/Row. Pass -1 to leave a dimension to the layout; flex shares leftover space.</summary>
        public static T Size<T>(this T c, float width, float height, float flexWidth = -1) where T : Component
        {
            var le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0) { le.preferredWidth = width; le.minWidth = Mathf.Min(width, 40); }
            if (height >= 0) { le.preferredHeight = height; le.minHeight = height; }
            if (flexWidth >= 0) le.flexibleWidth = flexWidth;
            return c;
        }

        public static RectTransform Column(Transform parent, float spacing = 8, int padding = 0, bool fitHeight = false)
        {
            var rt = Rect("Column", parent);
            var g = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing; g.padding = new RectOffset(padding, padding, padding, padding);
            g.childControlWidth = true; g.childControlHeight = true;
            g.childForceExpandWidth = true; g.childForceExpandHeight = false;
            g.childAlignment = TextAnchor.UpperCenter;
            if (fitHeight)
            {
                var f = rt.gameObject.AddComponent<ContentSizeFitter>();
                f.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            return rt;
        }

        public static RectTransform Row(Transform parent, float spacing = 8, float height = -1)
        {
            var rt = Rect("Row", parent);
            var g = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.childControlWidth = true; g.childControlHeight = true;
            g.childForceExpandWidth = false; g.childForceExpandHeight = true;
            g.childAlignment = TextAnchor.MiddleCenter;
            if (height >= 0) rt.Size(-1, height);
            return rt;
        }

        /// <summary>A vertically scrolling list. Add rows to <paramref name="content"/>.</summary>
        public static RectTransform Scroll(Transform parent, out RectTransform content, float spacing = 6)
        {
            var root = Box("Scroll", parent, new Color(0, 0, 0, 0.01f), false);
            var sr = root.gameObject.AddComponent<ScrollRect>();
            var viewport = Rect("Viewport", root.transform).Stretch();
            viewport.gameObject.AddComponent<RectMask2D>();
            content = Column(viewport, spacing, 4, true);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = new Vector2(0, 0); content.offsetMax = new Vector2(0, 0);
            sr.viewport = viewport; sr.content = content;
            sr.horizontal = false; sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            return root.rectTransform;
        }

        public static InputField Input(Transform parent, string placeholder, string value, Action<string> onChanged)
        {
            var img = Box("Input", parent, new Color(0.02f, 0.03f, 0.05f, 1f));
            var field = img.gameObject.AddComponent<InputField>();
            var text = Label(img.transform, "", 22);
            text.supportRichText = false; text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.rectTransform.Stretch(12, 4, 12, 4);
            var ph = Label(img.transform, placeholder, 22, TextAnchor.MiddleLeft, Dim);
            ph.fontStyle = FontStyle.Italic;
            ph.rectTransform.Stretch(12, 4, 12, 4);
            field.textComponent = text; field.placeholder = ph; field.targetGraphic = img;
            field.text = value ?? "";
            if (onChanged != null) field.onValueChanged.AddListener(v => onChanged(v));
            return field;
        }

        /// <summary>"Label   [-]  value  [+]" row. <paramref name="change"/> receives -1 or +1.</summary>
        public static RectTransform Stepper(Transform parent, string label, Func<string> value, Action<int> change, List<Action> refreshers)
        {
            var row = Row(parent, 8, 46);
            Label(row, label, 21).Size(-1, -1, 1);
            Button(row, "<", () => change(-1), 24).Size(52, -1);
            var val = Label(row, value(), 21, TextAnchor.MiddleCenter, Accent).Size(190, -1);
            Button(row, ">", () => change(+1), 24).Size(52, -1);
            refreshers?.Add(() => { if (val != null) val.text = value(); });
            return row;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}
