using System;
using UnityEngine;

namespace Hexfront
{
    /// <summary>
    /// Rotatable isometric-style camera that orbits a point on the ground.
    ///   one finger drag  = pan          pinch       = zoom
    ///   two finger twist = rotate       mouse wheel = zoom, right-drag / Q,E = rotate
    /// A short touch that does not move is reported as a tap (used to select hexes).
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public Camera Cam;
        public Action<Vector2> Tapped;
        /// <summary>When false the camera ignores input (menus are open).</summary>
        public bool InputEnabled = true;

        const float MinDistance = 5f, MaxDistance = 46f;
        Vector3 _target;
        float _yaw, _yawGoal;
        float _distance = 18f, _distanceGoal = 18f;
        Vector3? _focus;
        Rect _bounds = new Rect(-5, -5, 10, 10);

        // gesture tracking
        int _activeId = int.MinValue;
        Vector2 _downPos, _lastPos;
        float _downTime;
        bool _moved, _multi, _startedOverUi;
        float _lastPinchDistance, _lastPinchAngle;
        Vector2 _lastPinchMid;

        public float Yaw => _yaw;

        public void SetBounds(Rect worldXZ) => _bounds = worldXZ;

        public void Snap(Vector3 target, float yaw, float distance)
        {
            _target = target; _yaw = _yawGoal = yaw; _distance = _distanceGoal = distance; _focus = null;
            Apply();
        }

        /// <summary>Glide the camera to look at a world position.</summary>
        public void Focus(Vector3 world)
        {
            world.y = 0;
            _focus = world;
        }

        public void RotateBy(float degrees) => _yawGoal += degrees;
        public void ZoomBy(float factor) => _distanceGoal = Mathf.Clamp(_distanceGoal * factor, MinDistance, MaxDistance);

        /// <summary>Where a screen point hits the ground plane (y = planeY).</summary>
        public bool GroundPoint(Vector2 screen, float planeY, out Vector3 hit)
        {
            var ray = Cam.ScreenPointToRay(screen);
            hit = default;
            if (Mathf.Abs(ray.direction.y) < 0.0001f) return false;
            float t = (planeY - ray.origin.y) / ray.direction.y;
            if (t < 0) return false;
            hit = ray.origin + ray.direction * t;
            return true;
        }

        void Update()
        {
            if (Cam == null) return;
            if (InputEnabled) HandleInput();
            else { _activeId = int.MinValue; _multi = false; }

            if (_focus.HasValue)
            {
                _target = Vector3.Lerp(_target, _focus.Value, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
                if ((_target - _focus.Value).sqrMagnitude < 0.01f) _focus = null;
            }
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            _yaw = Mathf.Lerp(_yaw, _yawGoal, k);
            _distance = Mathf.Lerp(_distance, _distanceGoal, k);
            _target.x = Mathf.Clamp(_target.x, _bounds.xMin, _bounds.xMax);
            _target.z = Mathf.Clamp(_target.z, _bounds.yMin, _bounds.yMax);
            Apply();
        }

        void Apply()
        {
            // Look down more steeply when zoomed out, flatter when close in.
            float pitch = Mathf.Lerp(40f, 64f, Mathf.InverseLerp(MinDistance, MaxDistance, _distance));
            var rot = Quaternion.Euler(pitch, _yaw, 0f);
            Cam.transform.SetPositionAndRotation(_target - rot * Vector3.forward * _distance, rot);
        }

        void HandleInput()
        {
            var pointers = GameInput.Pointers;
            float dpiScale = Mathf.Max(1f, Screen.dpi / 160f);

            if (Mathf.Abs(GameInput.Scroll) > 0.001f) ZoomBy(1f - GameInput.Scroll * 0.12f);
            if (GameInput.RotateDrag) _yawGoal += GameInput.MouseDelta.x * 0.25f;
            _yawGoal += GameInput.KeyRotate * 90f * Time.unscaledDeltaTime;

            if (pointers.Count >= 2)
            {
                var a = pointers[0].Position; var b = pointers[1].Position;
                float dist = Vector2.Distance(a, b);
                float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                Vector2 mid = (a + b) * 0.5f;
                if (!_multi)
                {
                    _multi = true;
                    _startedOverUi = Ui.IsOverUi(a) || Ui.IsOverUi(b);
                }
                else if (!_startedOverUi)
                {
                    if (_lastPinchDistance > 1f && dist > 1f) ZoomBy(_lastPinchDistance / dist);
                    _yawGoal += Mathf.DeltaAngle(_lastPinchAngle, angle); // the map turns with the fingers
                    PanScreen(_lastPinchMid, mid);
                }
                _lastPinchDistance = dist; _lastPinchAngle = angle; _lastPinchMid = mid;
                _moved = true;          // a two-finger gesture is never a tap
                _activeId = int.MinValue;
                _focus = null;
                return;
            }

            if (pointers.Count == 0) { _multi = false; _activeId = int.MinValue; return; }

            var p = pointers[0];
            if (_multi)
            {
                // One finger of a pinch is still down: wait for it to lift before starting a new drag.
                if (p.Up) _multi = false;
                return;
            }
            if (p.Down || _activeId != p.Id)
            {
                _activeId = p.Id;
                _downPos = _lastPos = p.Position;
                _downTime = Time.unscaledTime;
                _moved = false;
                _startedOverUi = Ui.IsOverUi(p.Position);
            }
            if (_startedOverUi) { if (p.Up) _activeId = int.MinValue; return; }

            if (!_moved && Vector2.Distance(p.Position, _downPos) > 10f * dpiScale) _moved = true;
            if (_moved)
            {
                PanScreen(_lastPos, p.Position);
                _focus = null;
            }
            _lastPos = p.Position;

            if (p.Up)
            {
                if (!_moved && Time.unscaledTime - _downTime < 0.5f) Tapped?.Invoke(p.Position);
                _activeId = int.MinValue;
            }
        }

        /// <summary>Move the target so the ground point under 'from' ends up under 'to' (the map sticks to the finger).</summary>
        void PanScreen(Vector2 from, Vector2 to)
        {
            if (GroundPoint(from, 0, out var a) && GroundPoint(to, 0, out var b))
            {
                var delta = a - b; delta.y = 0;
                if (delta.sqrMagnitude < 2500f) _target += delta;
            }
        }
    }
}
