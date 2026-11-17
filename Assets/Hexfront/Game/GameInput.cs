using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
#endif

namespace Hexfront
{
    public struct PointerState
    {
        public int Id;
        public Vector2 Position;
        /// <summary>True on the frame the finger/button went down.</summary>
        public bool Down;
        /// <summary>True on the frame the finger/button was lifted.</summary>
        public bool Up;
    }

    /// <summary>
    /// One place that reads touch and mouse input, so the rest of the game does not care whether
    /// the project uses Unity's new Input System package or the legacy Input Manager.
    /// (ENABLE_INPUT_SYSTEM is defined by Unity when the new system is active in Player Settings.)
    /// </summary>
    public static class GameInput
    {
        public static readonly List<PointerState> Pointers = new List<PointerState>();
        public static float Scroll;        // + = zoom in
        public static bool RotateDrag;     // right mouse button held
        public static Vector2 MouseDelta;
        public static float KeyRotate;     // -1, 0, +1 from Q / E
        public static bool Back;           // Escape / Android back button

        public static void Update()
        {
            Pointers.Clear();
            Scroll = 0; RotateDrag = false; MouseDelta = Vector2.zero; KeyRotate = 0; Back = false;
#if ENABLE_INPUT_SYSTEM
            if (!EnhancedTouchSupport.enabled) EnhancedTouchSupport.Enable();
            foreach (var t in ETouch.activeTouches)
            {
                var phase = t.phase;
                Pointers.Add(new PointerState
                {
                    Id = t.touchId, Position = t.screenPosition,
                    Down = phase == UnityEngine.InputSystem.TouchPhase.Began,
                    Up = phase == UnityEngine.InputSystem.TouchPhase.Ended || phase == UnityEngine.InputSystem.TouchPhase.Canceled,
                });
            }
            var mouse = Mouse.current;
            if (mouse != null && Pointers.Count == 0)
            {
                if (mouse.leftButton.isPressed || mouse.leftButton.wasReleasedThisFrame)
                    Pointers.Add(new PointerState
                    {
                        Id = -1, Position = mouse.position.ReadValue(),
                        Down = mouse.leftButton.wasPressedThisFrame, Up = mouse.leftButton.wasReleasedThisFrame,
                    });
                Scroll = Mathf.Clamp(mouse.scroll.ReadValue().y * 0.01f, -1f, 1f);
                RotateDrag = mouse.rightButton.isPressed;
                MouseDelta = mouse.delta.ReadValue();
            }
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.qKey.isPressed) KeyRotate -= 1;
                if (kb.eKey.isPressed) KeyRotate += 1;
                Back = kb.escapeKey.wasPressedThisFrame;
            }
#else
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                Pointers.Add(new PointerState
                {
                    Id = t.fingerId, Position = t.position,
                    Down = t.phase == TouchPhase.Began,
                    Up = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled,
                });
            }
            if (Pointers.Count == 0 && Input.mousePresent)
            {
                if (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0))
                    Pointers.Add(new PointerState
                    {
                        Id = -1, Position = Input.mousePosition,
                        Down = Input.GetMouseButtonDown(0), Up = Input.GetMouseButtonUp(0),
                    });
                Scroll = Mathf.Clamp(Input.mouseScrollDelta.y, -1f, 1f);
                RotateDrag = Input.GetMouseButton(1);
                MouseDelta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            }
            if (Input.GetKey(KeyCode.Q)) KeyRotate -= 1;
            if (Input.GetKey(KeyCode.E)) KeyRotate += 1;
            Back = Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }
}
